using ScreenRecorder.Models;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenRecorder.Services
{
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
        private byte[]? _silenceBuffer;
        private volatile bool _audioDataReceived;
        private System.Threading.Timer? _keepaliveTimer;

        public bool IsRecording => _isRecording && _ffmpegProcess != null && !_ffmpegProcess.HasExited;
        public string? OutputPath => _outputPath;
        public string FfmpegLog => _ffmpegLog.ToString();

        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        public static string? FindFfmpeg()
        {
            var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(p)) return p;
            p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            if (File.Exists(p)) return p;
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { var t = d.Trim(); if (t.Length > 0) { p = Path.Combine(t, "ffmpeg.exe"); if (File.Exists(p)) return p; } } catch { }
            }
            return null;
        }

        private static string GetWindowTitle(IntPtr h)
        {
            int n = GetWindowTextLength(h); if (n == 0) return "";
            var sb = new StringBuilder(n + 1); GetWindowTextW(h, sb, sb.Capacity); return sb.ToString();
        }

        private static bool GetWindowBounds(IntPtr h, out int x, out int y, out int w, out int hh)
        {
            x = y = w = hh = 0;
            int hr = DwmGetWindowAttribute(h, 9, out RECT r, Marshal.SizeOf<RECT>());
            if (hr != 0) if (!GetWindowRect(h, out r)) return false;
            x = Math.Max(0, r.Left); y = Math.Max(0, r.Top);
            w = r.Right - r.Left; hh = r.Bottom - r.Top;
            if (w % 2 != 0) w--; if (hh % 2 != 0) hh--;
            return w > 0 && hh > 0;
        }

        public async Task<bool> StartRecordingAsync(WindowInfo target, CaptureMode mode = CaptureMode.AppOnly,
            int cropTop = 0, int cropRight = 0, int cropBottom = 0, int cropLeft = 0)
        {
            if (_isRecording) return false;
            var ffmpegPath = FindFfmpeg();
            if (ffmpegPath == null) throw new FileNotFoundException("FFmpeg not found.");
            if (!IsWindow(target.Handle)) throw new InvalidOperationException("Window gone.");

            string? winTitle = null; int wx = 0, wy = 0, ww = 0, wh = 0;
            if (mode == CaptureMode.AppOnly)
            { winTitle = GetWindowTitle(target.Handle); if (string.IsNullOrEmpty(winTitle)) throw new InvalidOperationException("No title."); }
            else { if (!GetWindowBounds(target.Handle, out wx, out wy, out ww, out wh)) throw new InvalidOperationException("No bounds."); }

            _outputPath = FileNameService.GetOutputFilePath();

            _hasAudio = false; NAudio.Wave.WaveFormat? af = null;
            try { var t = new NAudio.Wave.WasapiLoopbackCapture(); af = t.WaveFormat; t.Dispose(); _hasAudio = true;
                _silenceBuffer = new byte[af.SampleRate * af.Channels * (af.BitsPerSample / 8) / 10]; } catch { }

            _audioPipeName = $"sr_{Guid.NewGuid():N}";
            string crop = ""; if (cropTop > 0 || cropRight > 0 || cropBottom > 0 || cropLeft > 0)
                crop = $"crop=in_w-{cropLeft}-{cropRight}:in_h-{cropTop}-{cropBottom}:{cropLeft}:{cropTop}";

            if (_hasAudio && af != null) { StartAudioPipeServer(af); await Task.Delay(300); }

            string args;
            if (_hasAudio && af != null)
                args = mode == CaptureMode.AppOnly
                    ? BuildTitleAudio(winTitle!, af, _audioPipeName!, _outputPath, crop)
                    : BuildRegionAudio(wx, wy, ww, wh, af, _audioPipeName!, _outputPath, crop);
            else
                args = mode == CaptureMode.AppOnly
                    ? BuildTitle(winTitle!, _outputPath, crop)
                    : BuildRegion(wx, wy, ww, wh, _outputPath, crop);

            _ffmpegLog.Clear();
            _ffmpegProcess = new Process { StartInfo = new ProcessStartInfo {
                FileName = ffmpegPath, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true },
                EnableRaisingEvents = true };
            _ffmpegProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) _ffmpegLog.AppendLine(e.Data); };

            try
            {
                _ffmpegProcess.Start();
                try { _ffmpegProcess.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
                _ffmpegProcess.BeginErrorReadLine();
                await Task.Delay(3000);
                if (_ffmpegProcess.HasExited) { StopAudioPipe(); throw new InvalidOperationException($"FFmpeg failed.\n\n{_ffmpegLog}"); }
                _isRecording = true; return true;
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) { StopAudioPipe(); _ffmpegProcess?.Dispose(); _ffmpegProcess = null;
                throw new InvalidOperationException($"Start failed: {ex.Message}", ex); }
        }

        // ── FFmpeg args (no -movflags +faststart, applied post-recording) ──

        private string Af(NAudio.Wave.WaveFormat f) =>
            (f.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat || f.BitsPerSample == 32) ? "f32le" : (f.BitsPerSample == 16 ? "s16le" : "f32le");

        private string Vf(string crop)
        {
            const string fix = "crop=trunc(iw/2)*2:trunc(ih/2)*2";
            return !string.IsNullOrEmpty(crop) ? $"-vf \"{crop},{fix}\" " : $"-vf \"{fix}\" ";
        }

        private string BuildTitleAudio(string title, NAudio.Wave.WaveFormat f, string pipe, string o, string crop)
        {
            var e = title.Replace("\"", "\\\"");
            return $"-f gdigrab -framerate 30 -thread_queue_size 4096 -rtbufsize 256M -i title=\"{e}\" " +
                   $"-use_wallclock_as_timestamps 1 -f {Af(f)} -ar {f.SampleRate} -ac {f.Channels} -thread_queue_size 4096 " +
                   $"-i \"\\\\.\\pipe\\{pipe}\" {Vf(crop)}" +
                   $"-c:v libx264 -preset ultrafast -tune zerolatency -crf 20 -g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
                   $"-c:a aac -b:a 192k -threads 0 -y \"{o}\"";
        }

        private string BuildRegionAudio(int x, int y, int w, int h, NAudio.Wave.WaveFormat f, string pipe, string o, string crop) =>
            $"-f gdigrab -framerate 30 -thread_queue_size 4096 -rtbufsize 256M -offset_x {x} -offset_y {y} -video_size {w}x{h} -i desktop " +
            $"-use_wallclock_as_timestamps 1 -f {Af(f)} -ar {f.SampleRate} -ac {f.Channels} -thread_queue_size 4096 " +
            $"-i \"\\\\.\\pipe\\{pipe}\" {Vf(crop)}" +
            $"-c:v libx264 -preset ultrafast -tune zerolatency -crf 20 -g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p " +
            $"-c:a aac -b:a 192k -threads 0 -y \"{o}\"";

        private string BuildTitle(string title, string o, string crop)
        { var e = title.Replace("\"", "\\\""); return $"-f gdigrab -framerate 30 -thread_queue_size 4096 -rtbufsize 256M -i title=\"{e}\" {Vf(crop)}" +
            $"-c:v libx264 -preset ultrafast -tune zerolatency -crf 20 -g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p -threads 0 -y \"{o}\""; }

        private string BuildRegion(int x, int y, int w, int h, string o, string crop) =>
            $"-f gdigrab -framerate 30 -thread_queue_size 4096 -rtbufsize 256M -offset_x {x} -offset_y {y} -video_size {w}x{h} -i desktop {Vf(crop)}" +
            $"-c:v libx264 -preset ultrafast -tune zerolatency -crf 20 -g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p -threads 0 -y \"{o}\"";

        // ── Audio pipe ──

        private void StartAudioPipeServer(NAudio.Wave.WaveFormat fmt)
        {
            _stopAudioPipe = false; _audioDataReceived = false;
            _audioPipeThread = new Thread(() =>
            {
                NamedPipeServerStream? pipe = null; NAudio.Wave.WasapiLoopbackCapture? cap = null;
                try
                {
                    pipe = new NamedPipeServerStream(_audioPipeName!, PipeDirection.Out, 1,
                        PipeTransmissionMode.Byte, PipeOptions.WriteThrough, 0, 4 * 1024 * 1024);
                    _audioPipe = pipe;
                    var cts = new CancellationTokenSource(15000);
                    try { pipe.WaitForConnectionAsync(cts.Token).Wait(); } catch { pipe.Dispose(); _audioPipe = null; return; }
                    if (_stopAudioPipe) return;

                    cap = new NAudio.Wave.WasapiLoopbackCapture(); _audioCapture = cap;
                    cap.DataAvailable += (s, e) => { if (!_stopAudioPipe) { _audioDataReceived = true; SafeWrite(pipe, e.Buffer, e.BytesRecorded); } };
                    cap.StartRecording();

                    _keepaliveTimer = new System.Threading.Timer(_ => {
                        if (_stopAudioPipe) return;
                        if (!_audioDataReceived && _silenceBuffer != null) SafeWrite(pipe, _silenceBuffer, _silenceBuffer.Length);
                        _audioDataReceived = false;
                    }, null, 200, 200);

                    while (!_stopAudioPipe) { Thread.Sleep(100); if (_ffmpegProcess == null || _ffmpegProcess.HasExited) break; }
                    _keepaliveTimer?.Dispose(); _keepaliveTimer = null; cap.StopRecording();
                }
                catch { }
                finally { _keepaliveTimer?.Dispose(); _keepaliveTimer = null;
                    try { cap?.Dispose(); } catch { } try { pipe?.Dispose(); } catch { }
                    _audioCapture = null; _audioPipe = null; }
            }) { IsBackground = true, Name = "AudioPipe" };
            _audioPipeThread.Start();
        }

        private void SafeWrite(NamedPipeServerStream? p, byte[] b, int c)
        { if (p == null || !p.IsConnected || _stopAudioPipe) return;
            try { p.Write(b, 0, c); } catch (IOException) { } catch (ObjectDisposedException) { } }

        private void StopAudioPipe()
        { _stopAudioPipe = true; _keepaliveTimer?.Dispose(); _keepaliveTimer = null;
            try { _audioCapture?.StopRecording(); } catch { } try { _audioPipe?.Dispose(); } catch { }
            _audioPipeThread?.Join(5000); _audioPipeThread = null; _audioCapture = null; _audioPipe = null; }

        // ── Stop ──

        public async Task StopRecordingAsync(Action<string>? progressCallback = null)
        {
            if (!_isRecording || _ffmpegProcess == null) return;
            try
            {
                progressCallback?.Invoke("녹화 종료 중...");
                if (!_ffmpegProcess.HasExited)
                    try { _ffmpegProcess.StandardInput.Write("q"); _ffmpegProcess.StandardInput.Flush(); } catch { }

                progressCallback?.Invoke("오디오 종료 중...");
                StopAudioPipe();

                progressCallback?.Invoke("파일 마무리 중...");
                bool exited = await Task.Run(() => _ffmpegProcess.WaitForExit(60000));
                if (!exited) { try { _ffmpegProcess.Kill(); } catch { } await Task.Run(() => _ffmpegProcess.WaitForExit(5000)); }

                // faststart 적용 — moov atom을 파일 앞으로 이동하여 탐색 가능하게
                if (_outputPath != null && File.Exists(_outputPath) && new FileInfo(_outputPath).Length > 0)
                {
                    progressCallback?.Invoke("탐색 최적화 중...");
                    await ApplyFaststartAsync(_outputPath);
                }

                progressCallback?.Invoke("완료!");
            }
            catch { }
            finally { _isRecording = false; _ffmpegProcess?.Dispose(); _ffmpegProcess = null; }
        }

        private async Task ApplyFaststartAsync(string filePath)
        {
            var ffmpeg = FindFfmpeg(); if (ffmpeg == null) return;
            var tmp = filePath + ".fs.mp4";
            try
            {
                using var p = new Process { StartInfo = new ProcessStartInfo {
                    FileName = ffmpeg, Arguments = $"-i \"{filePath}\" -c copy -movflags +faststart -y \"{tmp}\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
                p.Start();
                bool ok = await Task.Run(() => p.WaitForExit(600000));
                if (!ok) try { p.Kill(); } catch { }
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 0)
                { File.Delete(filePath); File.Move(tmp, filePath); }
                else { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            }
            catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        public void Dispose()
        { if (_disposed) return; _disposed = true; StopAudioPipe();
            if (_ffmpegProcess != null) { if (!_ffmpegProcess.HasExited) try { _ffmpegProcess.Kill(); } catch { } _ffmpegProcess.Dispose(); } }
    }
}
