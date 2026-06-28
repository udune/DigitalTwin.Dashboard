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
    }
}
