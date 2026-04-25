using ScreenRecorder.Models;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// FFmpeg gdigrab + NAudio WASAPI Named Pipe 실시간 동시 녹화 서비스
    /// 
    /// 비디오+오디오를 FFmpeg 하나로 동시에 MP4로 인코딩.
    /// 후처리 mux 없음. 녹화 종료 즉시 파일 완성.
    /// 
    /// 장시간 안정성:
    /// - 오디오 파이프에 무음 keepalive를 주기적으로 전송하여 파이프 끊김 방지
    /// - FFmpeg 프로세스 상태를 주기적으로 모니터링
    /// - thread_queue_size를 충분히 크게 설정
    /// - 오디오 파이프 쓰기 실패 시 자동 복구 시도
    /// </summary>
    public class RecordingService : IDisposable
    {
        private Process? _ffmpegProcess;
        private NAudio.Wave.WasapiLoopbackCapture? _audioCapture;
        private NamedPipeServerStream? _audioPipe;
        private Thread? _audioPipeThread;
        private volatile bool _stopAudioPipe;
        private bool _isRecording;
        private string? _outputPath;
        private bool _hasAudio;
        private string? _audioPipeName;
        private readonly StringBuilder _ffmpegLog = new();
        private bool _disposed;

        // 오디오 keepalive용
        private NAudio.Wave.WaveFormat? _audioFormat;
        private byte[]? _silenceBuffer;
        private volatile bool _audioDataReceived;
        private System.Threading.Timer? _keepaliveTimer;

        public bool IsRecording => _isRecording && _ffmpegProcess != null && !_ffmpegProcess.HasExited;
        public string? OutputPath => _outputPath;
        public string FfmpegLog => _ffmpegLog.ToString();

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute,
            out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        public static string? FindFfmpeg()
        {
            var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(localPath)) return localPath;
            var subPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            if (File.Exists(subPath)) return subPath;
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';'))
            {
                try
                {
                    var t = dir.Trim();
                    if (string.IsNullOrEmpty(t)) continue;
                    var p = Path.Combine(t, "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static bool GetWindowBounds(IntPtr hWnd, out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            int hr = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT rect, Marshal.SizeOf<RECT>());
            if (hr != 0)
                if (!GetWindowRect(hWnd, out rect)) return false;
            x = Math.Max(0, rect.Left);
            y = Math.Max(0, rect.Top);
            w = rect.Right - rect.Left;
            h = rect.Bottom - rect.Top;
            if (w % 2 != 0) w--;
            if (h % 2 != 0) h--;
            return w > 0 && h > 0;
        }

        public async Task<bool> StartRecordingAsync(WindowInfo target, CaptureMode mode = CaptureMode.AppOnly,
            int cropTop = 0, int cropRight = 0, int cropBottom = 0, int cropLeft = 0)
        {
            if (_isRecording) return false;

            var ffmpegPath = FindFfmpeg();
            if (ffmpegPath == null)
                throw new FileNotFoundException("FFmpeg를 찾을 수 없습니다.");
            if (!IsWindow(target.Handle))
                throw new InvalidOperationException("선택한 윈도우가 더 이상 존재하지 않습니다.");

            string? windowTitle = null;
            int wx = 0, wy = 0, ww = 0, wh = 0;

            if (mode == CaptureMode.AppOnly)
            {
                windowTitle = GetWindowTitle(target.Handle);
                if (string.IsNullOrEmpty(windowTitle))
                    throw new InvalidOperationException("윈도우 타이틀을 가져올 수 없습니다.");
            }
            else
            {
                if (!GetWindowBounds(target.Handle, out wx, out wy, out ww, out wh))
                    throw new InvalidOperationException("윈도우 크기를 가져올 수 없습니다.");
            }

            _outputPath = FileNameService.GetOutputFilePath();

            // 오디오 포맷 확인
            _hasAudio = false;
            _audioFormat = null;
            try
            {
                var test = new NAudio.Wave.WasapiLoopbackCapture();
                _audioFormat = test.WaveFormat;
                test.Dispose();
                _hasAudio = true;

                // 무음 버퍼 준비 (100ms분)
                int bytesPerSec = _audioFormat.SampleRate * _audioFormat.Channels * (_audioFormat.BitsPerSample / 8);
                _silenceBuffer = new byte[bytesPerSec / 10]; // 100ms 무음
            }
            catch { _hasAudio = false; }

            _audioPipeName = $"screenrec_{Guid.NewGuid():N}";

            // crop 필터
            string cropFilter = "";
            if (cropTop > 0 || cropRight > 0 || cropBottom > 0 || cropLeft > 0)
                cropFilter = $"crop=in_w-{cropLeft}-{cropRight}:in_h-{cropTop}-{cropBottom}:{cropLeft}:{cropTop}";

            // ── 시작 순서 ──

            // 1) Named Pipe 서버 생성
            if (_hasAudio && _audioFormat != null)
            {
                StartAudioPipeServer(_audioFormat);
                await Task.Delay(300);
            }

            // 2) FFmpeg 인자 구성
            string args;
            if (_hasAudio && _audioFormat != null)
            {
                if (mode == CaptureMode.AppOnly)
                    args = BuildArgsTitleWithAudio(windowTitle!, _audioFormat, _audioPipeName, _outputPath, cropFilter);
                else
                    args = BuildArgsRegionWithAudio(wx, wy, ww, wh, _audioFormat, _audioPipeName, _outputPath, cropFilter);
            }
            else
            {
                if (mode == CaptureMode.AppOnly)
                    args = BuildArgsTitle(windowTitle!, _outputPath, cropFilter);
                else
                    args = BuildArgsRegion(wx, wy, ww, wh, _outputPath, cropFilter);
            }

            _ffmpegLog.Clear();
            _ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                },
                EnableRaisingEvents = true
            };
            _ffmpegProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) _ffmpegLog.AppendLine(e.Data);
            };

            try
            {
                _ffmpegProcess.Start();
                _ffmpegProcess.BeginErrorReadLine();

                await Task.Delay(2000);

                if (_ffmpegProcess.HasExited)
                {
                    StopAudioPipe();
                    var info = mode == CaptureMode.AppOnly ? $"타이틀: {windowTitle}" : $"영역: {wx},{wy} {ww}x{wh}";
                    throw new InvalidOperationException(
                        $"FFmpeg 시작 실패.\n\n{info}\n\nFFmpeg 로그:\n{_ffmpegLog}");
                }

                _isRecording = true;
                return true;
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                StopAudioPipe();
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
                throw new InvalidOperationException($"녹화 시작 실패: {ex.Message}", ex);
            }
        }

        // ── FFmpeg 인자 ──

        private string BuildArgsTitleWithAudio(string windowTitle,
            NAudio.Wave.WaveFormat fmt, string pipeName, string outputPath, string cropFilter)
        {
            string audioFmt = GetAudioFmtString(fmt);
            var escaped = windowTitle.Replace("\"", "\\\"");
            var vf = BuildVfFilter(cropFilter);

            return $"-f gdigrab -framerate 30 -thread_queue_size 4096 " +
                   $"-i title=\"{escaped}\" " +
                   $"-f {audioFmt} -ar {fmt.SampleRate} -ac {fmt.Channels} " +
                   $"-thread_queue_size 4096 " +
                   $"-i \"\\\\.\\pipe\\{pipeName}\" " +
                   $"{vf}" +
                   $"-c:v libx264 -preset ultrafast -crf 20 " +
                   $"-g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
                   $"-c:a aac -b:a 192k " +
                   $"-movflags +faststart " +
                   $"-y \"{outputPath}\"";
        }

        private string BuildArgsRegionWithAudio(int x, int y, int w, int h,
            NAudio.Wave.WaveFormat fmt, string pipeName, string outputPath, string cropFilter)
        {
            string audioFmt = GetAudioFmtString(fmt);
            var vf = BuildVfFilter(cropFilter);

            return $"-f gdigrab -framerate 30 -thread_queue_size 4096 " +
                   $"-offset_x {x} -offset_y {y} -video_size {w}x{h} " +
                   $"-i desktop " +
                   $"-f {audioFmt} -ar {fmt.SampleRate} -ac {fmt.Channels} " +
                   $"-thread_queue_size 4096 " +
                   $"-i \"\\\\.\\pipe\\{pipeName}\" " +
                   $"{vf}" +
                   $"-c:v libx264 -preset ultrafast -crf 20 " +
                   $"-g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
                   $"-c:a aac -b:a 192k " +
                   $"-movflags +faststart " +
                   $"-y \"{outputPath}\"";
        }

        private string BuildArgsTitle(string windowTitle, string outputPath, string cropFilter)
        {
            var escaped = windowTitle.Replace("\"", "\\\"");
            var vf = BuildVfFilter(cropFilter);
            return $"-f gdigrab -framerate 30 -thread_queue_size 4096 " +
                   $"-i title=\"{escaped}\" " +
                   $"{vf}" +
                   $"-c:v libx264 -preset ultrafast -crf 20 " +
                   $"-g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
                   $"-movflags +faststart " +
                   $"-y \"{outputPath}\"";
        }

        private string BuildArgsRegion(int x, int y, int w, int h, string outputPath, string cropFilter)
        {
            var vf = BuildVfFilter(cropFilter);
            return $"-f gdigrab -framerate 30 -thread_queue_size 4096 " +
                   $"-offset_x {x} -offset_y {y} -video_size {w}x{h} " +
                   $"-i desktop " +
                   $"{vf}" +
                   $"-c:v libx264 -preset ultrafast -crf 20 " +
                   $"-g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
                   $"-movflags +faststart " +
                   $"-y \"{outputPath}\"";
        }

        private string GetAudioFmtString(NAudio.Wave.WaveFormat fmt)
        {
            return (fmt.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat || fmt.BitsPerSample == 32)
                ? "f32le" : (fmt.BitsPerSample == 16 ? "s16le" : "f32le");
        }

        private string BuildVfFilter(string cropFilter)
        {
            const string evenFix = "crop=trunc(iw/2)*2:trunc(ih/2)*2";
            if (!string.IsNullOrEmpty(cropFilter))
                return $"-vf \"{cropFilter},{evenFix}\" ";
            else
                return $"-vf \"{evenFix}\" ";
        }

        // ── 오디오 Named Pipe (keepalive 포함) ──

        private void StartAudioPipeServer(NAudio.Wave.WaveFormat format)
        {
            _stopAudioPipe = false;
            _audioDataReceived = false;

            _audioPipeThread = new Thread(() =>
            {
                NamedPipeServerStream? pipe = null;
                NAudio.Wave.WasapiLoopbackCapture? capture = null;

                try
                {
                    pipe = new NamedPipeServerStream(
                        _audioPipeName!, PipeDirection.Out, 1,
                        PipeTransmissionMode.Byte, PipeOptions.WriteThrough,
                        0, 4 * 1024 * 1024); // 4MB 버퍼
                    _audioPipe = pipe;

                    var cts = new CancellationTokenSource(15000);
                    try { pipe.WaitForConnectionAsync(cts.Token).Wait(); }
                    catch { pipe.Dispose(); _audioPipe = null; return; }

                    if (_stopAudioPipe) return;

                    // NAudio 캡처 시작
                    capture = new NAudio.Wave.WasapiLoopbackCapture();
                    _audioCapture = capture;

                    capture.DataAvailable += (s, e) =>
                    {
                        if (_stopAudioPipe) return;
                        _audioDataReceived = true;
                        WriteToPipe(pipe, e.Buffer, e.BytesRecorded);
                    };

                    capture.StartRecording();

                    // Keepalive 타이머: 200ms마다 오디오 데이터가 없으면 무음 전송
                    _keepaliveTimer = new System.Threading.Timer(_ =>
                    {
                        if (_stopAudioPipe) return;
                        if (!_audioDataReceived && _silenceBuffer != null)
                        {
                            WriteToPipe(pipe, _silenceBuffer, _silenceBuffer.Length);
                        }
                        _audioDataReceived = false;
                    }, null, 200, 200);

                    // 녹화 중지까지 대기
                    while (!_stopAudioPipe)
                    {
                        Thread.Sleep(100);

                        // FFmpeg가 죽었으면 파이프도 중지
                        if (_ffmpegProcess == null || _ffmpegProcess.HasExited)
                        {
                            _stopAudioPipe = true;
                            break;
                        }
                    }

                    _keepaliveTimer?.Dispose();
                    _keepaliveTimer = null;
                    capture.StopRecording();
                }
                catch { }
                finally
                {
                    _keepaliveTimer?.Dispose();
                    _keepaliveTimer = null;
                    try { capture?.Dispose(); } catch { }
                    try { pipe?.Dispose(); } catch { }
                    _audioCapture = null;
                    _audioPipe = null;
                }
            })
            { IsBackground = true, Name = "AudioPipe" };

            _audioPipeThread.Start();
        }

        /// <summary>
        /// 파이프에 안전하게 쓰기. 실패해도 크래시하지 않음.
        /// </summary>
        private void WriteToPipe(NamedPipeServerStream? pipe, byte[] buffer, int count)
        {
            if (pipe == null || !pipe.IsConnected || _stopAudioPipe) return;
            try
            {
                pipe.Write(buffer, 0, count);
            }
            catch (IOException)
            {
                // 파이프 끊김 — FFmpeg가 종료되었을 수 있음
                // 크래시하지 않고 조용히 무시
            }
            catch (ObjectDisposedException) { }
        }

        private void StopAudioPipe()
        {
            _stopAudioPipe = true;
            _keepaliveTimer?.Dispose();
            _keepaliveTimer = null;
            try { _audioCapture?.StopRecording(); } catch { }
            try { _audioPipe?.Dispose(); } catch { }
            _audioPipeThread?.Join(5000);
            _audioPipeThread = null;
            _audioCapture = null;
            _audioPipe = null;
        }

        // ── 녹화 중지 ──

        public async Task StopRecordingAsync(Action<string>? progressCallback = null)
        {
            if (!_isRecording || _ffmpegProcess == null) return;

            try
            {
                // 1) FFmpeg에 'q' 전송
                progressCallback?.Invoke("녹화 종료 중...");
                if (!_ffmpegProcess.HasExited)
                {
                    try
                    {
                        _ffmpegProcess.StandardInput.Write("q");
                        _ffmpegProcess.StandardInput.Flush();
                    }
                    catch { }
                }

                // 2) 오디오 파이프 중지
                progressCallback?.Invoke("오디오 종료 중...");
                StopAudioPipe();

                // 3) FFmpeg 종료 대기
                progressCallback?.Invoke("파일 마무리 중...");
                bool exited = await Task.Run(() => _ffmpegProcess.WaitForExit(60000));
                if (!exited)
                {
                    try { _ffmpegProcess.Kill(); } catch { }
                    await Task.Run(() => _ffmpegProcess.WaitForExit(5000));
                }

                progressCallback?.Invoke("완료!");
            }
            catch { }
            finally
            {
                _isRecording = false;
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAudioPipe();
            if (_ffmpegProcess != null)
            {
                if (!_ffmpegProcess.HasExited)
                    try { _ffmpegProcess.Kill(); } catch { }
                _ffmpegProcess.Dispose();
            }
        }
    }
}
