using DigitalTwin.Dashboard.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalTwin.Dashboard.Services
{
    internal class ErrorDetector
    {
        // Unity 안전 영역에 맞춰 제한
        private const float X_LIMIT = 125.9f;
        private const float Y_LIMIT = 125.9f;
        private const float Z_MIN = -60f;
        private const float Z_MAX = 0f;
        private const float Z_SAFE_HEIGHT = -30f;  // Z축 범위: -60~0mm, -30보다 아래면 위험
        private const float MAX_VELOCITY = 150f;

        // 반복 알람 간격 설정 (초 단위) - 같은 에러의 재발생 간격
        private double _repeatIntervalSeconds = 30.0;

        // 각 에러 종류별 마지막 발생 시각 추적
        private Dictionary<string, DateTime> _lastAlarmTimes = new Dictionary<string, DateTime>();

        public event Action<AlarmData> OnErrorDetected;

        // 반복 알람 간격 설정 (초 단위)
        public void SetCheckInterval(double seconds)
        {
            _repeatIntervalSeconds = Math.Max(0.1, seconds); // 최소 0.1초
        }

        public double GetCheckInterval() => _repeatIntervalSeconds;

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

            // Z축 범위 체크 (0 초과 또는 -60 미만)
            if (data.Z > Z_MAX)
            {
                RaiseError("Error", "Z_AXIS", $"Z축 상한 초과: {data.Z:F1}mm (제한: {Z_MAX}mm 이하)");
            }
            if (data.Z < Z_MIN)
            {
                RaiseError("Error", "Z_AXIS", $"Z축 하한 초과: {data.Z:F1}mm (제한: {Z_MIN}mm 이상)");
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
            // 에러 그룹 키 생성 (AlarmData.GetGroupKey()와 동일한 방식)
            string errorKey = $"{level}|{location}|{message}";
            DateTime now = DateTime.Now;

            // 이 에러가 이전에 발생했는지 확인
            if (_lastAlarmTimes.TryGetValue(errorKey, out DateTime lastTime))
            {
                // 같은 에러가 이전에 발생했음
                // 마지막 발생 후 설정된 간격이 지났는지 확인
                double elapsedSeconds = (now - lastTime).TotalSeconds;

                if (elapsedSeconds < _repeatIntervalSeconds)
                {
                    // 아직 간격이 안 지났으면 알람 발생 안 함
                    return;
                }
            }

            // 새로운 에러이거나 간격이 지난 에러 → 알람 발생
            _lastAlarmTimes[errorKey] = now;

            OnErrorDetected?.Invoke(new AlarmData
            {
                Time = now,
                Level = level,
                Location = location,
                Message = message
            });
        }
    }
}
