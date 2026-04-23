using ScreenRecorder.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// 실행 중인 윈도우 목록을 가져오는 서비스
    /// </summary>
    public static class WindowService
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out bool pvAttribute, int cbAttribute);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int DWMWA_CLOAKED = 14;

        /// <summary>
        /// 녹화 가능한 윈도우 목록을 반환
        /// </summary>
        public static List<WindowInfo> GetRecordableWindows()
        {
            var result = new List<WindowInfo>();
            var shellWindow = GetShellWindow();
            var myPid = (uint)Environment.ProcessId;

            EnumWindows((hWnd, _) =>
            {
                // 보이지 않는 윈도우 제외
                if (!IsWindowVisible(hWnd)) return true;

                // 셸 윈도우(바탕화면) 제외
                if (hWnd == shellWindow) return true;

                // 타이틀 없는 윈도우 제외
                int titleLen = GetWindowTextLength(hWnd);
                if (titleLen == 0) return true;

                // 툴 윈도우 제외
                int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

                // DWM Cloaked 윈도우 제외 (UWP 숨겨진 윈도우)
                try
                {
                    DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out bool cloaked, Marshal.SizeOf<bool>());
                    if (cloaked) return true;
                }
                catch { }

                // 프로세스 정보
                GetWindowThreadProcessId(hWnd, out uint pid);

                // 자기 자신 제외
                if (pid == myPid) return true;

                // 타이틀 가져오기
                var sb = new StringBuilder(titleLen + 1);
                GetWindowTextW(hWnd, sb, sb.Capacity);
                var title = sb.ToString();

                // 프로세스 이름
                string processName = "";
                try
                {
                    var proc = Process.GetProcessById((int)pid);
                    processName = proc.ProcessName;
                }
                catch { return true; }

                // 시스템 프로세스 제외
                if (IsSystemProcess(processName)) return true;

                result.Add(new WindowInfo
                {
                    Handle = hWnd,
                    ProcessId = (int)pid,
                    ProcessName = processName,
                    Title = title
                });

                return true;
            }, IntPtr.Zero);

            // 프로세스 이름 → 타이틀 순 정렬
            result.Sort((a, b) =>
            {
                int cmp = string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });

            return result;
        }

        private static bool IsSystemProcess(string name)
        {
            var lower = name.ToLowerInvariant();
            return lower is "explorer" or "shellexperiencehost" or "searchhost"
                or "startmenuexperiencehost" or "textinputhost" or "applicationframehost"
                or "systemsettings" or "lockapp" or "searchui" or "cortana"
                or "widgets" or "widgetservice";
        }
    }
}
