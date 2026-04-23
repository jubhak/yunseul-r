using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenRecorder
{
    /// <summary>
    /// yunseul-s 스플래시 스타일의 처리 중 오버레이
    /// 녹화 종료 시 파일 처리가 끝날 때까지 표시
    /// </summary>
    public partial class ProcessingOverlay : Window
    {
        private DispatcherTimer? _animTimer;
        private double _angle1;
        private double _angle2;
        private double _pulse;
        private bool _pulseUp = true;

        public ProcessingOverlay()
        {
            InitializeComponent();

            // 원형 윈도우 클리핑
            Loaded += (s, e) =>
            {
                var clip = new EllipseGeometry(new Point(90, 90), 90, 90);
                this.Clip = clip;
            };

            // 애니메이션 타이머
            _pulse = 0.7;
            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _animTimer.Tick += AnimTick;
            _animTimer.Start();
        }

        private void AnimTick(object? sender, EventArgs e)
        {
            // 링 회전
            _angle1 = (_angle1 + 3) % 360;
            _angle2 = (_angle2 - 1.8) % 360;
            if (_angle2 < 0) _angle2 += 360;

            ring1Rotate.Angle = _angle1;
            ring2Rotate.Angle = _angle2;

            // 글자 펄스
            if (_pulseUp)
            {
                _pulse += 0.02;
                if (_pulse >= 1.0) { _pulse = 1.0; _pulseUp = false; }
            }
            else
            {
                _pulse -= 0.02;
                if (_pulse <= 0.4) { _pulse = 0.4; _pulseUp = true; }
            }

            var alpha = (byte)(255 * _pulse);
            txtLetter.Foreground = new SolidColorBrush(Color.FromArgb(alpha, 0x58, 0xa6, 0xff));
        }

        /// <summary>
        /// 진행 상태 텍스트 업데이트
        /// </summary>
        public void UpdateProgress(string text)
        {
            Dispatcher.Invoke(() => txtProgress.Text = text);
        }

        /// <summary>
        /// 페이드아웃 후 닫기
        /// </summary>
        public void FadeOutAndClose()
        {
            var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            fadeTimer.Tick += (s, e) =>
            {
                if (Opacity > 0.05)
                {
                    Opacity -= 0.08;
                }
                else
                {
                    fadeTimer.Stop();
                    _animTimer?.Stop();
                    _animTimer = null;
                    Close();
                }
            };
            fadeTimer.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            _animTimer?.Stop();
            _animTimer = null;
            base.OnClosed(e);
        }
    }
}
