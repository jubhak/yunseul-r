using System.IO;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// 녹화 파일명 생성 서비스
    /// history 폴더에 yyyyMMdd-HHmm.mp4 형식으로 생성
    /// 중복 시 _2, _3 접미사 추가
    /// </summary>
    public static class FileNameService
    {
        public static string GetOutputFilePath()
        {
            var historyDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history");
            if (!Directory.Exists(historyDir))
            {
                Directory.CreateDirectory(historyDir);
            }

            var baseName = DateTime.Now.ToString("yyyyMMdd-HHmm");
            var filePath = Path.Combine(historyDir, $"{baseName}.mp4");

            if (!File.Exists(filePath))
            {
                return filePath;
            }

            // 중복 파일이 있으면 _2, _3, ... 접미사 추가
            int suffix = 2;
            while (true)
            {
                filePath = Path.Combine(historyDir, $"{baseName}_{suffix}.mp4");
                if (!File.Exists(filePath))
                {
                    return filePath;
                }
                suffix++;
            }
        }
    }
}
