using DigitalTwin.Dashboard.Models;

namespace DigitalTwin.Dashboard.Services
{
    internal class VirtualPLC
    {
        private const float MaxAccel = 500.0f;
        private const int UpdateRate = 100;

        private bool isRunning = false;
        private CancellationTokenSource cts;

        public event Action<AxisData> OnDataUpdated;
        public event Action<string> OnError;

        // 물리적 이동 한계 = travel clamp (알람 경계와는 별개, P1).
        private readonly float _xLimit;
        private readonly float _yLimit;
        private readonly float _zMin;
        private readonly float _zMax;

        // 보간 최대 속도 = 설정값(mm/s). 산출 속도의 상한을 결정하므로
        // 과속 경보 임계(DeviceConfig.AlarmMaxVelocity)와 짝을 이룬다.
        private readonly float _maxSpeed;

        private readonly DeviceTable _deviceTable;
        private readonly ErrorDetector _errorDetector;

        public bool IsRunning => isRunning;

        public VirtualPLC(DeviceTable deviceTable, ErrorDetector errorDetector, DeviceConfig config)
        {
            _deviceTable = deviceTable;
            _errorDetector = errorDetector;
            _xLimit = config.XLimit;
            _yLimit = config.YLimit;
            _zMin = config.ZMin;
            _zMax = config.ZMax;
            // 0 이하면 축이 영원히 멈추므로 기본값으로 되돌린다.
            _maxSpeed = config.MaxSpeed > 0f ? config.MaxSpeed : 100f;
        }

        public float MaxSpeed => _maxSpeed;

        public Task Start()
        {
            if (isRunning)
            {
                return Task.CompletedTask;
            }

            isRunning = true;
            cts = new CancellationTokenSource();

            _ = Task.Run(() => UpdateLoop(cts.Token));

            return Task.CompletedTask;
        }

        public void Stop()
        {
            isRunning = false;
            cts?.Cancel();
        }

        private async Task UpdateLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // ① DeviceTable에서 target / 이전 current 읽기
                    var snap = _deviceTable.Snapshot();

                    float deltaTime = 1f / UpdateRate;

                    // ② 기존 보간 로직으로 새 current 계산 (travel clamp는 여기 유지 = P1)
                    float targetX = Math.Clamp(snap.TargetX, -_xLimit, _xLimit);
                    float targetY = Math.Clamp(snap.TargetY, -_yLimit, _yLimit);
                    float targetZ = Math.Clamp(snap.TargetZ, _zMin, _zMax);

                    float currentX = MoveTowards(snap.CurrentX, targetX, _maxSpeed * deltaTime);
                    float currentY = MoveTowards(snap.CurrentY, targetY, _maxSpeed * deltaTime);
                    float currentZ = MoveTowards(snap.CurrentZ, targetZ, _maxSpeed * deltaTime);

                    // 속도 계산 (실제 이동한 거리 / 시간)
                    float velX = (currentX - snap.CurrentX) / deltaTime;
                    float velY = (currentY - snap.CurrentY) / deltaTime;
                    float velZ = (currentZ - snap.CurrentZ) / deltaTime;

                    // ③ DeviceTable에 current/velocity 기록
                    _deviceTable.SetCurrentAndVelocity(currentX, currentY, currentZ, velX, velY, velZ);

                    // 경계 판정 (current 기록 직후, 100Hz 결정적 판정, UI 스레드 밖)
                    _errorDetector.Evaluate();

                    // Unity 송신 트리거 (cadence 기존 그대로 유지 = 100Hz·백그라운드, T6)
                    OnDataUpdated?.Invoke(new AxisData
                    {
                        X = currentX,
                        Y = currentY,
                        Z = currentZ,
                        VelocityX = velX,
                        VelocityY = velY,
                        VelocityZ = velZ,
                        Timestamp = DateTime.Now
                    });

                    await Task.Delay(1000 / UpdateRate, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    OnError?.Invoke($"UpdateLoop 전송 오류: {e.Message}");
                }
            }
        }

        private float MoveTowards(float current, float target, float maxDelta)
        {
            if (Math.Abs(target - current) <= maxDelta)
            {
                return target;
            }

            return current + Math.Sign(target - current) * maxDelta;
        }
    }
}
