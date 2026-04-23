using ScreenRecorder.Models;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// FFmpeg gdigrab title= + NAudio WASAPI Named Pipe 동시 녹화 서비스
    /// 
    /// 방식:
    /// - gdigrab title="윈도우타이틀"로 해당 앱만 캡처 (다른 창에 가려져도 해당 앱만 녹화)
    /// - NAudio WASAPI Loopback → Named Pipe → FFmpeg 오디오 입력
    /// - FFmpeg 하나가 비디오+오디오를 동시에 받아 MP4로 바로 인코딩
    /// - -movflags +faststart로 후처리 없이 즉시 완료
    /// 
    /// 참고: GPU 가속 앱(Edge, Chrome)은 gdigrab title= 방식에서 검정 화면이 될 수 있음.
    /// 이 경우 해당 앱의 설정에서 하드웨어 가속을 끄면 해결됨.
    /// 
    /// 시작 순서:
    /// 1) Named Pipe 서버 생성
    /// 2) FFmpeg 시작 (gdigrab title= + pipe 입력)
    /// 3) FFmpeg가 pipe에 연결되면 NAudio 오디오 캡처 시작
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

            // 모드에 따라 캡처 대상 정보 준비
            string? windowTitle = null;
            int wx = 0, wy = 0, ww = 0, wh = 0;

            if (mode == CaptureMode.AppOnly)
            {
                windowTitle = GetWindowTitle(target.Handle);
                if (string.IsNullOrEmpty(windowTitle))
                    throw new InvalidOperationException("윈도우 타이틀을 가져올 수 없습니다.");
            }
            else // Region
            {
                if (!GetWindowBounds(target.Handle, out wx, out wy, out ww, out wh))
                    throw new InvalidOperationException("윈도우 크기를 가져올 수 없습니다.");
            }

            _outputPath = FileNameService.GetOutputFilePath();

            // 오디오 포맷 확인
            _hasAudio = false;
            NAudio.Wave.WaveFormat? audioFormat = null;
            try
            {
                var test = new NAudio.Wave.WasapiLoopbackCapture();
                audioFormat = test.WaveFormat;
                test.Dispose();
                _hasAudio = true;
            }
            catch { }

            _audioPipeName = $"screenrec_{Guid.NewGuid():N}";

            // ── 시작 순서 ──

            // 1) Named Pipe 서버 생성
            if (_hasAudio && audioFormat != null)
            {
                StartAudioPipeServer(audioFormat);
                await Task.Delay(300);
            }

            // crop 필터 문자열 (값이 있을 때만)
            string cropFilter = "";
            if (cropTop > 0 || cropRight > 0 || cropBottom > 0 || cropLeft > 0)
            {
                // crop=in_w-left-right:in_h-top-bottom:left:top
                cropFilter = $"crop=in_w-{cropLeft}-{cropRight}:in_h-{cropTop}-{cropBottom}:{cropLeft}:{cropTop}";
            }

            // 2) FFmpeg 인자 구성 + 시작
            string args;
            if (_hasAudio && audioFormat != null)
            {
                if (mode == CaptureMode.AppOnly)
                    args = BuildArgsTitleWithAudio(windowTitle!, audioFormat, _audioPipeName, _outputPath, cropFilter);
                else
                    args = BuildArgsRegionWithAudio(wx, wy, ww, wh, audioFormat, _audioPipeName, _outputPath, cropFilter);
            }
            else
            {
                if (mode == CaptureMode.AppOnly)
                    args = BuildArgsTitleVideoOnly(windowTitle!, _outputPath, cropFilter);
                else
                    args = BuildArgsRegionVideoOnly(wx, wy, ww, wh, _outputPath, cropFilter);
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
                    var modeInfo = mode == CaptureMode.AppOnly ? $"타이틀: {windowTitle}" : $"영역: {wx},{wy} {ww}x{wh}";
                    throw new InvalidOperationException(
                        $"FFmpeg 시작 실패.\n\n{modeInfo}\n\nFFmpeg 로그:\n{_ffmpegLog}");
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

        // ── FFmpeg 인자 구성 ──

        private string BuildArgsTitleWithAudio(string windowTitle,
            NAudio.Wave.WaveFormat fmt, string pipeName, string outputPath, string cropFilter)
        {
            string audioFmt = (fmt.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat || fmt.BitsPerSample == 32)
                ? "f32le" : (fmt.BitsPerSample == 16 ? "s16le" : "f32le");
            var escaped = windowTitle.Replace("\"", "\\\"");
            var vf = !string.IsNullOrEmpty(cropFilter) ? $"-vf \"{cropFilter}\" " : "";

            return $"-f gdigrab -framerate 30 -thread_queue_size 1024 " +
                   $"-i title=\"{escaped}\" " +
                   $"-f {audioFmt} -ar {fmt.SampleRate} -ac {fmt.Channels} " +
                   $"-thread_queue_size 1024 " +
                   $"-i \"\\\\.\\pipe\\{pipeName}\" " +
                   $"{vf}" +
                   $"-c:v libx264 -preset ultrafast -crf 20 " +
                   $"-g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
                   $"-c:a aac -b:a 192k " +
                   $"-movflags +faststart " +
                   $"-y \"{outputPath}\"";
        }

        private string BuildArgsTitleVideoOnly(string windowTitle, string outputPath, string cropFilter)
        {
            var escaped = windowTitle.Replace("\"", "\\\"");
            var vf = !string.IsNullOrEmpty(cropFilter) ? $"-vf \"{cropFilter}\" " : "";

            return $"-f gdigrab -framerate 30 -thread_queue_size 1024 " +
                   $"-i title=\"{escaped}\" " +
                   $"{vf}" +
                   $"-c:v libx264 -preset ultrafast -crf 20 " +
                   $"-g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
                   $"-movflags +faststart " +
                   $"-y \"{outputPath}\"";
        }

        private string BuildArgsRegionWithAudio(int x, int y, int w, int h,
            NAudio.Wave.WaveFormat fmt, string pipeName, string outputPath, string cropFilter)
        {
            string audioFmt = (fmt.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat || fmt.BitsPerSample == 32)
                ? "f32le" : (fmt.BitsPerSample == 16 ? "s16le" : "f32le");
            var vf = !string.IsNullOrEmpty(cropFilter) ? $"-vf \"{cropFilter}\" " : "";

            return $"-f gdigrab -framerate 30 -thread_queue_size 1024 " +
                   $"-offset_x {x} -offset_y {y} -video_size {w}x{h} " +
                   $"-i desktop " +
                   $"-f {audioFmt} -ar {fmt.SampleRate} -ac {fmt.Channels} " +
                   $"-thread_queue_size 1024 " +
                   $"-i \"\\\\.\\pipe\\{pipeName}\" " +
                   $"{vf}" +
                   $"-c:v libx264 -preset ultrafast -crf 20 " +
                   $"-g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
                   $"-c:a aac -b:a 192k " +
                   $"-movflags +faststart " +
                   $"-y \"{outputPath}\"";
        }

        private string BuildArgsRegionVideoOnly(int x, int y, int w, int h, string outputPath, string cropFilter)
        {
            var vf = !string.IsNullOrEmpty(cropFilter) ? $"-vf \"{cropFilter}\" " : "";

            return $"-f gdigrab -framerate 30 -thread_queue_size 1024 " +
                   $"-offset_x {x} -offset_y {y} -video_size {w}x{h} " +
                   $"-i desktop " +
                   $"{vf}" +
                   $"-c:v libx264 -preset ultrafast -crf 20 " +
                   $"-g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
                   $"-movflags +faststart " +
                   $"-y \"{outputPath}\"";
        }

        // ── 오디오 Named Pipe ──

        private void StartAudioPipeServer(NAudio.Wave.WaveFormat format)
        {
            _stopAudioPipe = false;

            _audioPipeThread = new Thread(() =>
            {
                NamedPipeServerStream? pipe = null;
                NAudio.Wave.WasapiLoopbackCapture? capture = null;

                try
                {
                    pipe = new NamedPipeServerStream(
                        _audioPipeName!, PipeDirection.Out, 1,
                        PipeTransmissionMode.Byte, PipeOptions.WriteThrough,
                        0, 1024 * 1024);
                    _audioPipe = pipe;

                    var cts = new CancellationTokenSource(15000);
                    try { pipe.WaitForConnectionAsync(cts.Token).Wait(); }
                    catch { pipe.Dispose(); _audioPipe = null; return; }

                    if (_stopAudioPipe) return;

                    capture = new NAudio.Wave.WasapiLoopbackCapture();
                    _audioCapture = capture;

                    capture.DataAvailable += (s, e) =>
                    {
                        if (_stopAudioPipe || pipe == null || !pipe.IsConnected) return;
                        try { pipe.Write(e.Buffer, 0, e.BytesRecorded); }
                        catch { _stopAudioPipe = true; }
                    };

                    capture.StartRecording();

                    while (!_stopAudioPipe) Thread.Sleep(50);
                    capture.StopRecording();
                }
                catch { }
                finally
                {
                    try { capture?.Dispose(); } catch { }
                    try { pipe?.Dispose(); } catch { }
                    _audioCapture = null;
                    _audioPipe = null;
                }
            })
            { IsBackground = true, Name = "AudioPipe" };

            _audioPipeThread.Start();
        }

        private void StopAudioPipe()
        {
            _stopAudioPipe = true;
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

                progressCallback?.Invoke("오디오 종료 중...");
                StopAudioPipe();

                progressCallback?.Invoke("파일 마무리 중...");
                bool exited = await Task.Run(() => _ffmpegProcess.WaitForExit(30000));
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
