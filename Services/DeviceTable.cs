namespace DigitalTwin.Dashboard.Services
{
    // DeviceTable의 한 시점 상태를 통째로 복사한 불변 스냅샷.
    // 소비자(UI 폴링 타이머, Unity 송신, 향후 SLMP/OPC UA 어댑터)는 이것만 읽어
    // 락 경합과 필드 티어링을 피한다. (보고서 F-1)
    internal readonly record struct DeviceSnapshot(
        float TargetX, float TargetY, float TargetZ,
        float CurrentX, float CurrentY, float CurrentZ,
        float VelocityX, float VelocityY, float VelocityZ,
        float XMin, float XMax,
        float YMin, float YMax,
        float ZMin, float ZMax,
        bool ErrorLamp, bool XError, bool YError, bool ZError);

    // 시스템 상태의 단일 진실(single source of truth).
    // 모든 get/set은 단일 lock 아래에서 수행되며, 크리티컬 섹션은 필드 복사뿐이라
    // 마이크로초 단위다 — lock으로 충분하다(ReaderWriterLockSlim은 과함).
    // 변경 통지 이벤트는 두지 않는다(100Hz 이벤트 폭주 회피). 소비는 전부 Snapshot pull.
    internal class DeviceTable
    {
        private readonly object _lock = new object();

        // 타겟 위치 (수동 조작·Unity 수신이 last-writer-wins로 기록, P4)
        private float _targetX;
        private float _targetY;
        private float _targetZ;

        // 현재 위치 / 속도 (VirtualPLC 100Hz 루프가 기록)
        private float _currentX;
        private float _currentY;
        private float _currentZ;
        private float _velocityX;
        private float _velocityY;
        private float _velocityZ;

        // 알람 경계 (P2 디폴트 = 현재 ErrorDetector 값: X/Y ±125.9, Z −60~0). 런타임 변경 가능.
        private float _xMin = -125.9f;
        private float _xMax = 125.9f;
        private float _yMin = -125.9f;
        private float _yMax = 125.9f;
        private float _zMin = -60f;
        private float _zMax = 0f;

        // 에러 플래그 (ErrorDetector가 경계 판정 결과를 기록)
        private bool _errorLamp;
        private bool _xError;
        private bool _yError;
        private bool _zError;

        // lock 한 번 잡고 전 필드를 복사한 불변 스냅샷 반환
        public DeviceSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new DeviceSnapshot(
                    _targetX, _targetY, _targetZ,
                    _currentX, _currentY, _currentZ,
                    _velocityX, _velocityY, _velocityZ,
                    _xMin, _xMax,
                    _yMin, _yMax,
                    _zMin, _zMax,
                    _errorLamp, _xError, _yError, _zError);
            }
        }

        public void SetTarget(float x, float y, float z)
        {
            lock (_lock)
            {
                _targetX = x;
                _targetY = y;
                _targetZ = z;
            }
        }

        public void SetCurrentAndVelocity(
            float currentX, float currentY, float currentZ,
            float velocityX, float velocityY, float velocityZ)
        {
            lock (_lock)
            {
                _currentX = currentX;
                _currentY = currentY;
                _currentZ = currentZ;
                _velocityX = velocityX;
                _velocityY = velocityY;
                _velocityZ = velocityZ;
            }
        }

        public void SetErrorFlags(bool errorLamp, bool xError, bool yError, bool zError)
        {
            lock (_lock)
            {
                _errorLamp = errorLamp;
                _xError = xError;
                _yError = yError;
                _zError = zError;
            }
        }

        public void SetLimits(
            float xMin, float xMax,
            float yMin, float yMax,
            float zMin, float zMax)
        {
            lock (_lock)
            {
                _xMin = xMin;
                _xMax = xMax;
                _yMin = yMin;
                _yMax = yMax;
                _zMin = zMin;
                _zMax = zMax;
            }
        }
    }
}
