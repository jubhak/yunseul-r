namespace ScreenRecorder.Models
{
    /// <summary>
    /// 캡처 모드
    /// </summary>
    public enum CaptureMode
    {
        /// <summary>
        /// 앱 전용: gdigrab title= 방식. 다른 창에 가려져도 해당 앱만 녹화.
        /// 단, GPU 가속 앱(Edge, Chrome)은 검정 화면 가능.
        /// </summary>
        AppOnly,

        /// <summary>
        /// 영역 모드: gdigrab desktop + offset 방식. 앱 위치 기준 화면 영역 녹화.
        /// GPU 가속 앱도 정상 녹화. 단, 다른 창이 가리면 같이 녹화됨.
        /// </summary>
        Region
    }
}
