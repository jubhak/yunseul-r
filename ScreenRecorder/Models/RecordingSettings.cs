namespace ScreenRecorder.Models
{
    public class RecordingSettings
    {
        public string? LastProcessName { get; set; }

        /// <summary>Crop 상단 (px)</summary>
        public int CropTop { get; set; }
        /// <summary>Crop 우측 (px)</summary>
        public int CropRight { get; set; }
        /// <summary>Crop 하단 (px)</summary>
        public int CropBottom { get; set; }
        /// <summary>Crop 좌측 (px)</summary>
        public int CropLeft { get; set; }
    }
}
