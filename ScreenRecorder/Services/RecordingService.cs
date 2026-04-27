using ScreenRecorder.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// FFmpeg gdigrab 비디오 + NAudio WAV 오디오 → 종료 시 -c:v copy mux
    /// 
    /// 장시간 안정성 최우선:
    /// - FFmpeg는 비디오만 단일 입력으로 녹화 (gdigrab → MP4)
    /// - NAudio는 독립적으로 WAV 파일에 오디오 저장
    /// - 두 프로세스가 완전히 독립적이라 서로 블로킹/끊김 없음
    /// - 종료 시 -c:v copy로 합침 (비디오 재인코딩 없음, 수 GB도 10~30초)
    /// 
    /// Named Pipe 방식은 3시간+ 녹화 시 thread_queue 포화로 프레임 드롭 발생.
    /// </summary>
    public class RecordingService : IDisposable
    {
        private Process? _ffmpegProcess;
        private NAudio.Wave.WasapiLoopbackCapture? _audioCapture;
        private NAudio.Wave.WaveFileWriter? _audioWriter;
        private string? _tempAudioPath;
        private bool _isRecording;
        private string? _outputPath;
        private bool _hasAudio;
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

            // 1) 오디오 캡처 시작 (WAV 파일, 독립적)
            _hasAudio = StartAudioCapture();

            // 2) crop 필터
            string cropFilter = "";
            if (cropTop > 0 || cropRight > 0 || cropBottom > 0 || cropLeft > 0)
                cropFilter = $"crop=in_w-{cropLeft}-{cropRight}:in_h-{cropTop}-{cropBottom}:{cropLeft}:{cropTop}";

            // 3) FFmpeg: 비디오만 녹화
            string args;
            if (mode == CaptureMode.AppOnly)
                args = BuildArgsTitle(windowTitle!, _outputPath, cropFilter);
            else
                args = BuildArgsRegion(wx, wy, ww, wh, _outputPath, cropFilter);

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
                // FFmpeg 프로세스도 높은 우선순위로 설정
                try { _ffmpegProcess.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
                _ffmpegProcess.BeginErrorReadLine();

                await Task.Delay(2000);

                if (_ffmpegProcess.HasExited)
                {
                    StopAudioCapture();
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
                StopAudioCapture();
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
                throw new InvalidOperationException($"녹화 시작 실패: {ex.Message}", ex);
            }
        }

        // ── FFmpeg 인자 (비디오만, 단일 입력) ──

        private string BuildArgsTitle(string windowTitle, string outputPath, string cropFilter)
        {
            var escaped = windowTitle.Replace("\"", "\\\"");
            var vf = BuildVfFilter(cropFilter);
            return $"-f gdigrab -framerate 30 -thread_queue_size 4096 -rtbufsize 256M " +
                   $"-i title=\"{escaped}\" " +
                   $"{vf}" +
                   $"-c:v libx264 -preset ultrafast -tune zerolatency -crf 20 " +
                   $"-g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
                   $"-threads 0 " +
                   $"-y \"{outputPath}\"";
        }

        private string BuildArgsRegion(int x, int y, int w, int h, string outputPath, string cropFilter)
        {
            var vf = BuildVfFilter(cropFilter);
            return $"-f gdigrab -framerate 30 -thread_queue_size 4096 -rtbufsize 256M " +
                   $"-offset_x {x} -offset_y {y} -video_size {w}x{h} " +
                   $"-i desktop " +
                   $"{vf}" +
                   $"-c:v libx264 -preset ultrafast -tune zerolatency -crf 20 " +
                   $"-g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
                   $"-threads 0 " +
                   $"-y \"{outputPath}\"";
        }

        private string BuildVfFilter(string cropFilter)
        {
            const string evenFix = "crop=trunc(iw/2)*2:trunc(ih/2)*2";
            if (!string.IsNullOrEmpty(cropFilter))
                return $"-vf \"{cropFilter},{evenFix}\" ";
            else
                return $"-vf \"{evenFix}\" ";
        }

        // ── 오디오 (WAV 파일, 완전 독립) ──

        private bool StartAudioCapture()
        {
            try
            {
                _tempAudioPath = Path.Combine(Path.GetTempPath(), $"screenrec_{Guid.NewGuid():N}.wav");
                _audioCapture = new NAudio.Wave.WasapiLoopbackCapture();
                _audioWriter = new NAudio.Wave.WaveFileWriter(_tempAudioPath, _audioCapture.WaveFormat);

                _audioCapture.DataAvailable += (s, e) =>
                {
                    try { _audioWriter?.Write(e.Buffer, 0, e.BytesRecorded); } catch { }
                };
                _audioCapture.RecordingStopped += (s, e) =>
                {
                    try { _audioWriter?.Dispose(); _audioWriter = null; } catch { }
                };
                _audioCapture.StartRecording();
                return true;
            }
            catch
            {
                StopAudioCapture();
                _tempAudioPath = null;
                return false;
            }
        }

        private void StopAudioCapture()
        {
            try { _audioCapture?.StopRecording(); } catch { }
            try { _audioWriter?.Dispose(); _audioWriter = null; } catch { }
            try { _audioCapture?.Dispose(); _audioCapture = null; } catch { }
        }

        // ── 녹화 중지 ──

        public async Task StopRecordingAsync(Action<string>? progressCallback = null)
        {
            if (!_isRecording || _ffmpegProcess == null) return;

            try
            {
                // 1) FFmpeg 정상 종료
                progressCallback?.Invoke("녹화 종료 중...");
                if (!_ffmpegProcess.HasExited)
                {
                    try
                    {
                        _ffmpegProcess.StandardInput.Write("q");
                        _ffmpegProcess.StandardInput.Flush();
                    }
                    catch { }

                    bool exited = await Task.Run(() => _ffmpegProcess.WaitForExit(60000));
                    if (!exited)
                    {
                        try { _ffmpegProcess.Kill(); } catch { }
                        await Task.Run(() => _ffmpegProcess.WaitForExit(5000));
                    }
                }

                // 2) 오디오 캡처 중지
                progressCallback?.Invoke("오디오 처리 중...");
                StopAudioCapture();

                // 3) 오디오 합치기 (-c:v copy, 비디오 재인코딩 없음)
                if (_hasAudio && _outputPath != null && _tempAudioPath != null
                    && File.Exists(_outputPath) && new FileInfo(_outputPath).Length > 0
                    && File.Exists(_tempAudioPath) && new FileInfo(_tempAudioPath).Length > 44)
                {
                    progressCallback?.Invoke("오디오 합치는 중...");
                    await MuxAudioAsync(_outputPath, _tempAudioPath);
                }

                progressCallback?.Invoke("완료!");
            }
            catch { }
            finally
            {
                _isRecording = false;
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
                CleanupTempFile(_tempAudioPath);
                _tempAudioPath = null;
            }
        }

        /// <summary>
        /// -c:v copy로 비디오 재인코딩 없이 오디오만 합침.
        /// 비디오를 그대로 복사하므로 수 GB 파일도 10~30초.
        /// </summary>
        private async Task MuxAudioAsync(string videoPath, string audioPath)
        {
            var ffmpegPath = FindFfmpeg();
            if (ffmpegPath == null) return;

            var tmp = videoPath + ".tmp.mp4";
            // -c:v copy: 비디오 스트림 그대로 복사 (재인코딩 없음)
            // -c:a aac: 오디오만 AAC로 인코딩
            // -movflags +faststart: 빠른 탐색
            // -shortest: 짧은 쪽에 맞춤
            var args = $"-i \"{videoPath}\" -i \"{audioPath}\" " +
                       $"-c:v copy -c:a aac -b:a 192k " +
                       $"-movflags +faststart -shortest " +
                       $"-y \"{tmp}\"";
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath, Arguments = args,
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardError = true
                    }
                };
                proc.Start();
                // -c:v copy라 빠름. 넉넉히 10분 대기.
                bool ok = await Task.Run(() => proc.WaitForExit(600000));
                if (!ok) { try { proc.Kill(); } catch { } }

                if (File.Exists(tmp) && new FileInfo(tmp).Length > 0)
                { File.Delete(videoPath); File.Move(tmp, videoPath); }
                else { CleanupTempFile(tmp); }
            }
            catch { CleanupTempFile(tmp); }
        }

        private void CleanupTempFile(string? p)
        {
            if (p == null) return;
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAudioCapture();
            if (_ffmpegProcess != null)
            {
                if (!_ffmpegProcess.HasExited)
                    try { _ffmpegProcess.Kill(); } catch { }
                _ffmpegProcess.Dispose();
            }
        }
    }
}
