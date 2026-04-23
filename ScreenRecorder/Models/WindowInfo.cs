namespace ScreenRecorder.Models
{
    /// <summary>
    /// 실행 중인 윈도우 정보
    /// </summary>
    public class WindowInfo
    {
        public IntPtr Handle { get; set; }
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "";
        public string Title { get; set; } = "";

        /// <summary>
        /// 목록에 표시할 텍스트
        /// </summary>
        public string DisplayText
        {
            get
            {
                var title = Title.Length > 50 ? Title[..47] + "..." : Title;
                return $"{ProcessName}  —  {title}";
            }
        }

        public override string ToString() => DisplayText;
    }
}
