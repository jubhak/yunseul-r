using Newtonsoft.Json;
using ScreenRecorder.Models;
using System.IO;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// 녹화 설정(영역 정보)을 JSON 파일로 저장/로드하는 서비스
    /// </summary>
    public static class SettingsService
    {
        private static readonly string SettingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "recording_settings.json");

        public static RecordingSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonConvert.DeserializeObject<RecordingSettings>(json) ?? new RecordingSettings();
                }
            }
            catch
            {
                // 설정 파일 손상 시 기본값 반환
            }
            return new RecordingSettings();
        }

        public static void Save(RecordingSettings settings)
        {
            try
            {
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // 저장 실패 시 무시
            }
        }
    }
}
