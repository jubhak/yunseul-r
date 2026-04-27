using System.Diagnostics;
using System.Windows;

namespace ScreenRecorder
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 프로세스 우선순위를 높음으로 설정 — CPU 자원 우선 확보
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            }
            catch { }
        }
    }
}
