using ScreenRecorder.Models;
using ScreenRecorder.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenRecorder
{
    public partial class MainWindow : Window
    {
        private RecordingSettings _settings;
        private RecordingService? _recordingService;
        private DispatcherTimer? _timer;
        private DispatcherTimer? _previewTimer;
        private DateTime _recordingStartTime;
        private List<WindowInfo> _windows = new();
        private WindowInfo? _selectedWindow;
        private Models.CaptureMode _captureMode = Models.CaptureMode.Region;
        private bool _isRecording;

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        private const uint PW_RENDERFULLCONTENT = 2;

        // 글로벌 핫키
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        private const int HOTKEY_ID = 9001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_F2 = 0x71;

        public MainWindow()
        {
            InitializeComponent();
            _settings = SettingsService.Load();
            CheckFfmpeg();
            RefreshWindowList();
            UpdateModeButton();
            LoadCropSettings();

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _previewTimer.Tick += PreviewTimer_Tick;
            _previewTimer.Start();

            // Loaded 후에 TextChanged 이벤트 연결 (XAML 초기화 중 NullRef 방지)
            Loaded += (s, e) =>
            {
                txtCropTop.TextChanged += CropInput_TextChanged;
                txtCropRight.TextChanged += CropInput_TextChanged;
                txtCropBottom.TextChanged += CropInput_TextChanged;
                txtCropLeft.TextChanged += CropInput_TextChanged;

                // 글로벌 핫키 등록: Ctrl+F2
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                RegisterHotKey(hwnd, HOTKEY_ID, MOD_CONTROL, VK_F2);

                // WndProc 후킹
                var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProc);
            };
        }

        // ── 타이틀바 ──
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        { if (e.ClickCount == 1) DragMove(); }
        private void BtnMinimize_MouseDown(object sender, MouseButtonEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnClose_MouseDown(object sender, MouseButtonEventArgs e) => Close();

        // ── FFmpeg ──
        private void CheckFfmpeg()
        {
            var path = RecordingService.FindFfmpeg();
            txtFfmpegPath.Text = path != null ? $"FFmpeg: {path}" : "⚠ FFmpeg 미발견";
            if (path == null) txtFfmpegPath.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf8, 0x51, 0x49));
        }

        // ── 캡처 모드 ──
        private void BtnMode_Click(object sender, RoutedEventArgs e)
        {
            _captureMode = _captureMode == Models.CaptureMode.AppOnly ? Models.CaptureMode.Region : Models.CaptureMode.AppOnly;
            UpdateModeButton();
        }
        private void UpdateModeButton()
        {
            btnMode.Content = _captureMode == Models.CaptureMode.AppOnly ? "🎯 앱 전용" : "🖥 영역 모드";
        }

        // ── 녹화파일 폴더 열기 ──
        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }

        // ── 미리보기 토글 ──
        private bool _previewVisible = true;
        private void BtnPreviewToggle_Click(object sender, RoutedEventArgs e)
        {
            _previewVisible = !_previewVisible;
            ApplyPreviewVisibility();
        }
        private void ApplyPreviewVisibility()
        {
            if (_previewVisible)
            {
                panelPreview.Visibility = Visibility.Visible;
                divider.Visibility = Visibility.Visible;
                colDivider.Width = new GridLength(6);
                colPreview.Width = new GridLength(400);
                Width = 840;
                btnPreviewToggle.Content = "👁 미리보기";
            }
            else
            {
                panelPreview.Visibility = Visibility.Collapsed;
                divider.Visibility = Visibility.Collapsed;
                colDivider.Width = new GridLength(0);
                colPreview.Width = new GridLength(0);
                Width = 440;
                btnPreviewToggle.Content = "👁 미리보기 ▸";
            }
        }

        // ── Crop 설정 ──
        private void LoadCropSettings()
        {
            txtCropTop.Text = _settings.CropTop.ToString();
            txtCropRight.Text = _settings.CropRight.ToString();
            txtCropBottom.Text = _settings.CropBottom.ToString();
            txtCropLeft.Text = _settings.CropLeft.ToString();
        }
        private void SaveCropSettings()
        {
            _settings.CropTop = ParseCrop(txtCropTop.Text);
            _settings.CropRight = ParseCrop(txtCropRight.Text);
            _settings.CropBottom = ParseCrop(txtCropBottom.Text);
            _settings.CropLeft = ParseCrop(txtCropLeft.Text);
            SettingsService.Save(_settings);
        }
        private int ParseCrop(string text) => int.TryParse(text, out int v) && v >= 0 ? v : 0;
        private void CropInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
        { e.Handled = !Regex.IsMatch(e.Text, @"^\d$"); }
        private void CropInput_LostFocus(object sender, RoutedEventArgs e) => SaveCropSettings();
        private void CropInput_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.Dispatcher.BeginInvoke(new Action(() => tb.SelectAll()), System.Windows.Threading.DispatcherPriority.Input);
        }
        private void CropInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            // XAML 초기화 중에는 다른 TextBox가 아직 null일 수 있음
            if (txtCropTop == null || txtCropRight == null || txtCropBottom == null || txtCropLeft == null) return;
            SaveCropSettings();
        }

        // ── 미리보기 ──
        private void PreviewTimer_Tick(object? sender, EventArgs e)
        {
            if (_selectedWindow == null || !IsWindow(_selectedWindow.Handle))
            { imgPreview.Source = null; txtPreviewHint.Visibility = Visibility.Visible; txtPreviewHint.Text = "앱을 선택하면\n미리보기가 표시됩니다"; return; }
            try
            {
                var bmp = CaptureWindowBitmap(_selectedWindow.Handle);
                if (bmp != null)
                {
                    // crop 적용
                    int ct = ParseCrop(txtCropTop.Text), cr = ParseCrop(txtCropRight.Text);
                    int cb = ParseCrop(txtCropBottom.Text), cl = ParseCrop(txtCropLeft.Text);
                    if (ct + cb < bmp.Height && cl + cr < bmp.Width && (ct > 0 || cr > 0 || cb > 0 || cl > 0))
                    {
                        int cw = bmp.Width - cl - cr;
                        int ch = bmp.Height - ct - cb;
                        if (cw > 0 && ch > 0)
                        {
                            var cropped = bmp.Clone(new Rectangle(cl, ct, cw, ch), bmp.PixelFormat);
                            bmp.Dispose();
                            bmp = cropped;
                        }
                    }
                    imgPreview.Source = BitmapToImageSource(bmp);
                    txtPreviewHint.Visibility = Visibility.Collapsed;
                    bmp.Dispose();
                }
                else { txtPreviewHint.Text = "미리보기 불가\n(최소화 상태)"; txtPreviewHint.Visibility = Visibility.Visible; }
            }
            catch { imgPreview.Source = null; txtPreviewHint.Visibility = Visibility.Visible; }
        }
        private Bitmap? CaptureWindowBitmap(IntPtr hWnd)
        {
            if (IsIconic(hWnd)) return null;
            if (!GetWindowRect(hWnd, out RECT rect)) return null;
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) return null;
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT); g.ReleaseHdc(hdc); }
            return bmp;
        }
        private BitmapSource BitmapToImageSource(Bitmap bitmap)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Bmp); ms.Position = 0;
            var img = new BitmapImage(); img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit(); img.Freeze();
            return img;
        }

        // ── 프로세스 목록 ──
        private void RefreshWindowList()
        {
            _windows = WindowService.GetRecordableWindows();
            lstWindows.Items.Clear();
            int sel = -1;
            for (int i = 0; i < _windows.Count; i++)
            {
                lstWindows.Items.Add(_windows[i].DisplayText);
                if (_settings.LastProcessName != null && _windows[i].ProcessName.Equals(_settings.LastProcessName, StringComparison.OrdinalIgnoreCase) && sel < 0) sel = i;
            }
            if (sel >= 0) { lstWindows.SelectedIndex = sel; lstWindows.ScrollIntoView(lstWindows.Items[sel]); }
        }
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshWindowList();
        private void LstWindows_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var idx = lstWindows.SelectedIndex;
            if (idx >= 0 && idx < _windows.Count) { _selectedWindow = _windows[idx]; if (!_isRecording) btnRecord.IsEnabled = true; }
            else { _selectedWindow = null; if (!_isRecording) btnRecord.IsEnabled = false; }
        }

        // ── 녹화 시작/중지 통합 버튼 ──
        private async void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecording)
            {
                // 녹화 중지
                await StopRecording();
            }
            else
            {
                // 녹화 시작
                if (RecordingService.FindFfmpeg() == null)
                { MessageBox.Show("FFmpeg를 찾을 수 없습니다.", "FFmpeg 필요", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                if (_selectedWindow == null)
                { MessageBox.Show("녹화할 프로그램을 선택해주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                _settings.LastProcessName = _selectedWindow.ProcessName;
                SaveCropSettings();
                await StartRecording(_selectedWindow);
            }
        }

        private async Task StartRecording(WindowInfo target)
        {
            _recordingService = new RecordingService();
            btnRecord.IsEnabled = false;
            btnRefresh.IsEnabled = false;
            btnMode.IsEnabled = false;
            lstWindows.IsEnabled = false;

            // 녹화 시작 시 미리보기 숨기기 (먼저 처리)
            _previewVisible = false;
            ApplyPreviewVisibility();

            // 영역 모드에서는 자기 자신이 녹화에 포함되지 않도록 최소화
            if (_captureMode == Models.CaptureMode.Region)
                WindowState = WindowState.Minimized;

            var modeLabel = _captureMode == Models.CaptureMode.AppOnly ? "앱 전용" : "영역";
            txtStatus.Text = $"녹화 시작 중... ({target.ProcessName})";
            txtIndicator.Text = "INIT";
            txtIndicator.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xd2, 0x99, 0x22));

            try
            {
                bool started = await _recordingService.StartRecordingAsync(target, _captureMode,
                    _settings.CropTop, _settings.CropRight, _settings.CropBottom, _settings.CropLeft);

                if (!started) { ResetUI(); MessageBox.Show("녹화를 시작할 수 없습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error); return; }

                _isRecording = true;
                btnRecord.IsEnabled = true;
                btnRecord.Content = "⏹  녹화 중지  (Ctrl+F2)";
                btnRecord.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x21, 0x26, 0x2d));
                txtStatus.Text = $"녹화 중 — {target.ProcessName} ({modeLabel})";
                txtIndicator.Text = "● REC";
                txtIndicator.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf8, 0x51, 0x49));

                _recordingStartTime = DateTime.Now;
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += Timer_Tick;
                _timer.Start();
            }
            catch (Exception ex)
            {
                _recordingService?.Dispose(); _recordingService = null;
                ResetUI();
                MessageBox.Show($"녹화 시작 오류:\n\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task StopRecording()
        {
            btnRecord.IsEnabled = false;
            _timer?.Stop();

            var overlay = new ProcessingOverlay();
            overlay.Show();

            try
            {
                if (_recordingService != null)
                {
                    await _recordingService.StopRecordingAsync(p => overlay.UpdateProgress(p));
                    var outputPath = _recordingService.OutputPath;
                    var log = _recordingService.FfmpegLog;
                    _recordingService.Dispose(); _recordingService = null;
                    ResetUI();
                    overlay.FadeOutAndClose();

                    if (outputPath != null && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    {
                        var sizeMB = new FileInfo(outputPath).Length / (1024.0 * 1024.0);
                        txtStatus.Text = $"완료 — {sizeMB:F1} MB";
                    }
                    else
                    {
                        MessageBox.Show($"녹화 파일이 생성되지 않았습니다.\n\nFFmpeg 로그:\n{(string.IsNullOrEmpty(log) ? "(없음)" : log.Length > 1500 ? log[^1500..] : log)}",
                            "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else { overlay.FadeOutAndClose(); }
            }
            catch (Exception ex)
            {
                try { overlay.Close(); } catch { }
                MessageBox.Show($"녹화 중지 오류:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                ResetUI();
            }
        }

        // ── 타이머 ──
        private void Timer_Tick(object? sender, EventArgs e)
        {
            txtTimer.Text = $"{DateTime.Now - _recordingStartTime:hh\\:mm\\:ss}";
            if (_recordingService != null && !_recordingService.IsRecording)
            {
                _timer?.Stop();
                var log = _recordingService.FfmpegLog;
                _recordingService.Dispose(); _recordingService = null;
                ResetUI();
                MessageBox.Show($"녹화가 예기치 않게 중단되었습니다.\n\nFFmpeg 로그:\n{(string.IsNullOrEmpty(log) ? "(없음)" : log.Length > 1000 ? log[^1000..] : log)}",
                    "녹화 중단", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ── UI 리셋 ──
        private void ResetUI()
        {
            _timer?.Stop(); _timer = null;
            _isRecording = false;
            btnRecord.Content = "⏺  녹화 시작  (Ctrl+F2)";
            btnRecord.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6e, 0x20, 0x20));
            btnRefresh.IsEnabled = true;
            btnMode.IsEnabled = true;
            lstWindows.IsEnabled = true;
            txtTimer.Text = "00:00:00";
            txtIndicator.Text = "IDLE";
            txtIndicator.Foreground = (SolidColorBrush)FindResource("Fg2Brush");
            btnRecord.IsEnabled = _selectedWindow != null;

            // 창 상태 복원 (영역 모드에서 최소화했을 수 있음)
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            // 미리보기 복원
            _previewVisible = true;
            ApplyPreviewVisibility();

            RefreshWindowList();
        }

        // ── 글로벌 핫키 (Ctrl+F2) ──
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                // 녹화 버튼 클릭과 동일한 동작
                if (btnRecord.IsEnabled)
                    BtnRecord_Click(this, new RoutedEventArgs());
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ── 종료 ──
        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 핫키 해제
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_ID);

            _previewTimer?.Stop();
            SaveCropSettings();
            if (_recordingService?.IsRecording == true)
            {
                var r = MessageBox.Show("녹화 중입니다. 중지하고 종료할까요?", "종료 확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.No) { e.Cancel = true; return; }
                e.Cancel = true;
                await _recordingService.StopRecordingAsync();
                _recordingService.Dispose(); _recordingService = null;
                base.OnClosing(e); Application.Current.Shutdown(); return;
            }
            base.OnClosing(e);
        }
    }
}
