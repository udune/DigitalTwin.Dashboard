using DigitalTwin.Dashboard.Models;

namespace DigitalTwin.Dashboard.Services
{
    internal class ErrorDetector
    {
        // 알람 경계(X/Y/Z 한계)는 DeviceTable의 Limits에서 읽는다(P2). 하드코딩 제거.
        private const float Z_SAFE_HEIGHT = -30f;  // -30보다 아래에서 XY 이동 시 경고

        // 과속 임계와 보간 최대 속도는 설정값(appsettings.json)이다.
        // 산출 속도의 상한이 _maxSpeed이므로 _maxVelocity >= _maxSpeed면 과속 경보는 도달 불가능하다.
        private readonly float _maxVelocity;
        private readonly float _maxSpeed;

        // 반복 알람 간격 설정 (초 단위) - 같은 에러의 재발생 간격
        private double _repeatIntervalSeconds = 30.0;

        // 각 에러 종류별 마지막 발생 시각 추적
        private Dictionary<string, DateTime> _lastAlarmTimes = new Dictionary<string, DateTime>();

        private readonly DeviceTable _deviceTable;

        public event Action<AlarmData> OnErrorDetected;

        public ErrorDetector(DeviceTable deviceTable, DeviceConfig config)
        {
            _deviceTable = deviceTable;
            _maxVelocity = config.AlarmMaxVelocity;
            _maxSpeed = config.MaxSpeed > 0f ? config.MaxSpeed : 100f;
        }

        // 과속 경보가 실제로 발생할 수 있는 설정인지 여부.
        public bool IsOverspeedReachable => _maxVelocity < _maxSpeed;

        // 설정 불일치 점검. OnErrorDetected 구독을 끝낸 뒤 1회 호출한다.
        // 과속 임계가 최고 속도 이상이면 해당 경보는 영원히 울리지 않으므로,
        // 조용히 죽어 있는 대신 경고 알람으로 한 번 드러낸다.
        public void ValidateConfiguration()
        {
            if (IsOverspeedReachable)
            {
                return;
            }

            RaiseError("Warning", "SYSTEM", "CONFIG_OVERSPEED_UNREACHABLE",
                $"과속 경보 도달 불가: 임계 {_maxVelocity:F1}mm/s ≥ 최고 속도 {_maxSpeed:F1}mm/s " +
                $"(appsettings.json의 AlarmMaxVelocity 또는 MaxSpeed 확인)");
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
                RaiseError("Error", "X_AXIS", "X_LIMIT", $"X축 리미트 초과: {s.CurrentX:F1}mm (제한: {s.XMax:F1}mm)");
            }
            if (s.CurrentX < s.XMin)
            {
                RaiseError("Error", "X_AXIS", "X_LIMIT", $"X축 리미트 초과: {s.CurrentX:F1}mm (제한: {s.XMin:F1}mm)");
            }

            // Y축 리미트 체크
            if (s.CurrentY > s.YMax)
            {
                RaiseError("Error", "Y_AXIS", "Y_LIMIT", $"Y축 리미트 초과: {s.CurrentY:F1}mm (제한: {s.YMax:F1}mm)");
            }
            if (s.CurrentY < s.YMin)
            {
                RaiseError("Error", "Y_AXIS", "Y_LIMIT", $"Y축 리미트 초과: {s.CurrentY:F1}mm (제한: {s.YMin:F1}mm)");
            }

            // Z축 범위 체크 (상한 초과 또는 하한 미만)
            if (s.CurrentZ > s.ZMax)
            {
                RaiseError("Error", "Z_AXIS", "Z_LIMIT", $"Z축 상한 초과: {s.CurrentZ:F1}mm (제한: {s.ZMax:F1}mm 이하)");
            }
            if (s.CurrentZ < s.ZMin)
            {
                RaiseError("Error", "Z_AXIS", "Z_LIMIT", $"Z축 하한 초과: {s.CurrentZ:F1}mm (제한: {s.ZMin:F1}mm 이상)");
            }

            // Z축 안전 높이 체크
            if (s.CurrentZ < Z_SAFE_HEIGHT && (Math.Abs(s.VelocityX) > 0.1f || Math.Abs(s.VelocityY) > 0.1f))
            {
                RaiseError("Warning", "Z_AXIS", "Z_SAFE_HEIGHT", $"Z축 안전 높이 미달: {s.CurrentZ:F1}mm (XY 이동 중)");
            }

            // 과속 체크 (임계는 설정값 AlarmMaxVelocity)
            if (Math.Abs(s.VelocityX) > _maxVelocity)
            {
                RaiseError("Warning", "X_AXIS", "X_OVERSPEED",
                    $"X축 과속: {s.VelocityX:F1}mm/s (제한: {_maxVelocity:F1}mm/s)");
            }

            if (Math.Abs(s.VelocityY) > _maxVelocity)
            {
                RaiseError("Warning", "Y_AXIS", "Y_OVERSPEED",
                    $"Y축 과속: {s.VelocityY:F1}mm/s (제한: {_maxVelocity:F1}mm/s)");
            }

            if (Math.Abs(s.VelocityZ) > _maxVelocity)
            {
                RaiseError("Warning", "Z_AXIS", "Z_OVERSPEED",
                    $"Z축 과속: {s.VelocityZ:F1}mm/s (제한: {_maxVelocity:F1}mm/s)");
            }

            // 에러 플래그 기록 (램프 = 축 에러 중 하나라도)
            _deviceTable.SetErrorFlags(xError || yError || zError, xError, yError, zError);
        }

        private void RaiseError(string level, string location, string code, string message)
        {
            // 에러 그룹 키 생성 (AlarmData.GetGroupKey()와 동일한 방식)
            string errorKey = $"{level}|{location}|{code}";
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
                Code = code,
                Message = message
            });
        }
    }
}
