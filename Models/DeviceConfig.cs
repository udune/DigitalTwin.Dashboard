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
    }
}
