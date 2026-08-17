using System;
using System.IO;

namespace DigitalTwin.Dashboard.Models
{
    internal class DeviceConfig
    {
        public float XLimit { get; set; } = 500f;
        public float YLimit { get; set; } = 500f;
        public float ZMin { get; set; } = -100f;
        public float ZMax { get; set; } = 50f;

        public float AlarmXMin { get; set; } = -125.9f;
        public float AlarmXMax { get; set; } = 125.9f;
        public float AlarmYMin { get; set; } = -125.9f;
        public float AlarmYMax { get; set; } = 125.9f;
        public float AlarmZMin { get; set; } = -60f;
        public float AlarmZMax { get; set; } = 0f;

        // 보간 최대 속도(mm/s). VirtualPLC가 프레임당 이동량을 이 값으로 자르므로
        // 산출되는 |속도|는 항상 이 값 이하가 된다.
        public float MaxSpeed { get; set; } = 100f;

        // 과속 경보 임계(mm/s). MaxSpeed 이상으로 두면 경보는 절대 발생하지 않는다
        // (ErrorDetector가 시작 시 이 불일치를 경고로 알린다).
        public float AlarmMaxVelocity { get; set; } = 150f;

        // 북향 서버 리슨 포트. 기본값은 종전 하드코딩 값과 같다.
        public int SlmpPort { get; set; } = 5007;
        public int OpcUaPort { get; set; } = 4840;

        // 실행 폴더의 설정 파일 경로.
        public static string DefaultPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        // 설정 파일 읽기. MainViewModel 생성자에서 파일 I/O를 떼어내기 위한 진입점이다.
        // 파일이 없으면 조용히 기본값을 쓰고, 있는데 못 읽으면 기본값 + warning으로 알린다.
        // 설정 하나 때문에 앱이 뜨지 못하는 일이 없도록 예외는 던지지 않는다.
        public static DeviceConfig Load(string? path, out string? warning)
        {
            warning = null;
            string target = path ?? DefaultPath;

            try
            {
                if (!File.Exists(target))
                {
                    return new DeviceConfig();
                }

                var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<DeviceConfig>(
                    File.ReadAllText(target));

                if (loaded == null)
                {
                    warning = $"설정 파일이 비어 있습니다: {target}";
                    return new DeviceConfig();
                }

                return loaded;
            }
            catch (Exception ex)
            {
                warning = $"설정 파일 로드 실패: {ex.Message}";
                return new DeviceConfig();
            }
        }
    }
}
