using DigitalTwin.Dashboard.Models;

namespace DigitalTwin.Dashboard.Services
{
    internal class ErrorDetector
    {
        // 알람 경계(X/Y/Z 한계)는 DeviceTable의 Limits에서 읽는다(P2). 하드코딩 제거.
        private const float Z_SAFE_HEIGHT = -30f;  // -30보다 아래에서 XY 이동 시 경고
        private const float MAX_VELOCITY = 150f;

        // 반복 알람 간격 설정 (초 단위) - 같은 에러의 재발생 간격
        private double _repeatIntervalSeconds = 30.0;

        // 각 에러 종류별 마지막 발생 시각 추적
        private Dictionary<string, DateTime> _lastAlarmTimes = new Dictionary<string, DateTime>();

        private readonly DeviceTable _deviceTable;

        public event Action<AlarmData> OnErrorDetected;

        public ErrorDetector(DeviceTable deviceTable)
        {
            _deviceTable = deviceTable;
        }

        // 반복 알람 간격 설정 (초 단위)
        public void SetCheckInterval(double seconds)
        {
            _repeatIntervalSeconds = Math.Max(0.1, seconds); // 최소 0.1초
        }

        public double GetCheckInterval() => _repeatIntervalSeconds;

        // DeviceTable의 current를 Limits와 비교해 알람을 발생시키고 에러 플래그를 기록한다.
        // VirtualPLC 100Hz 루프가 current 기록 직후 호출(결정적 판정, UI 스레드 밖).
        public void Evaluate()
        {
            var s = _deviceTable.Snapshot();

            // 위치 한계 위반 = 축 에러 플래그
            bool xError = s.CurrentX < s.XMin || s.CurrentX > s.XMax;
            bool yError = s.CurrentY < s.YMin || s.CurrentY > s.YMax;
            bool zError = s.CurrentZ < s.ZMin || s.CurrentZ > s.ZMax;

            // X축 리미트 체크
            if (s.CurrentX > s.XMax)
            {
                RaiseError("Error", "X_AXIS", $"X축 리미트 초과: {s.CurrentX:F1}mm (제한: {s.XMax:F1}mm)");
            }
            if (s.CurrentX < s.XMin)
            {
                RaiseError("Error", "X_AXIS", $"X축 리미트 초과: {s.CurrentX:F1}mm (제한: {s.XMin:F1}mm)");
            }

            // Y축 리미트 체크
            if (s.CurrentY > s.YMax)
            {
                RaiseError("Error", "Y_AXIS", $"Y축 리미트 초과: {s.CurrentY:F1}mm (제한: {s.YMax:F1}mm)");
            }
            if (s.CurrentY < s.YMin)
            {
                RaiseError("Error", "Y_AXIS", $"Y축 리미트 초과: {s.CurrentY:F1}mm (제한: {s.YMin:F1}mm)");
            }

            // Z축 범위 체크 (상한 초과 또는 하한 미만)
            if (s.CurrentZ > s.ZMax)
            {
                RaiseError("Error", "Z_AXIS", $"Z축 상한 초과: {s.CurrentZ:F1}mm (제한: {s.ZMax:F1}mm 이하)");
            }
            if (s.CurrentZ < s.ZMin)
            {
                RaiseError("Error", "Z_AXIS", $"Z축 하한 초과: {s.CurrentZ:F1}mm (제한: {s.ZMin:F1}mm 이상)");
            }

            // Z축 안전 높이 체크
            if (s.CurrentZ < Z_SAFE_HEIGHT && (Math.Abs(s.VelocityX) > 0.1f || Math.Abs(s.VelocityY) > 0.1f))
            {
                RaiseError("Warning", "Z_AXIS", $"Z축 안전 높이 미달: {s.CurrentZ:F1}mm (XY 이동 중)");
            }

            // 과속 체크
            if (Math.Abs(s.VelocityX) > MAX_VELOCITY)
            {
                RaiseError("Warning", "X_AXIS",
                    $"X축 과속: {s.VelocityX:F1}mm/s (제한: {MAX_VELOCITY}mm/s)");
            }

            if (Math.Abs(s.VelocityY) > MAX_VELOCITY)
            {
                RaiseError("Warning", "Y_AXIS", $"Y축 과속: {s.VelocityY:F1}mm/s");
            }

            if (Math.Abs(s.VelocityZ) > MAX_VELOCITY)
            {
                RaiseError("Warning", "Z_AXIS", $"Z축 과속: {s.VelocityZ:F1}mm/s");
            }

            // 에러 플래그 기록 (램프 = 축 에러 중 하나라도)
            _deviceTable.SetErrorFlags(xError || yError || zError, xError, yError, zError);
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
