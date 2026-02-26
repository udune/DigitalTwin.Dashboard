using DigitalTwin.Dashboard.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalTwin.Dashboard.Services
{
    internal class ErrorDetector
    {
        private const float X_LIMIT = 400f;
        private const float Y_LIMIT = 400f;
        private const float Z_SAFE_HEIGHT = 30f;
        private const float MAX_VELOCITY = 150f;

        public event Action<AlarmData> OnErrorDetected;

        public void CheckAxisData(AxisData data)
        {
            // X축 리미트 체크
            if (Math.Abs(data.X) > X_LIMIT)
            {
                RaiseError("Error", "X_AXIS", $"X축 리미트 초과: {data.X:F1}mm (제한: ±{X_LIMIT}mm)");
            }

            // Y축 리미트 체크
            if (Math.Abs(data.Y) > Y_LIMIT)
            {
                RaiseError("Error", "Y_AXIS", $"Y축 리미트 초과: {data.Y:F1}mm (제한: ±{Y_LIMIT}mm)");
            }

            // Z축 안전 높이 체크
            if (data.Z < Z_SAFE_HEIGHT && (Math.Abs(data.VelocityX) > 0.1f || Math.Abs(data.VelocityY) > 0.1f))
            {
                RaiseError("Warning", "Z_AXIS", $"Z축 안전 높이 미달: {data.Z:F1}mm (XY 이동 중)");
            }

            // 과속 체크
            if (Math.Abs(data.VelocityX) > MAX_VELOCITY)
            {
                RaiseError("Warning", "X_AXIS",
                    $"X축 과속: {data.VelocityX:F1}mm/s (제한: {MAX_VELOCITY}mm/s)");
            }

            if (Math.Abs(data.VelocityY) > MAX_VELOCITY)
            {
                RaiseError("Warning", "Y_AXIS", $"Y축 과속: {data.VelocityY:F1}mm/s");
            }

            if (Math.Abs(data.VelocityZ) > MAX_VELOCITY)
            {
                RaiseError("Warning", "Z_AXIS", $"Z축 과속: {data.VelocityZ:F1}mm/s");
            }
        }

        private void RaiseError(string level, string location, string message)
        {
            OnErrorDetected?.Invoke(new AlarmData
            {
                Time = DateTime.Now,
                Level = level,
                Location = location,
                Message = message
            });
        }
    }
}
