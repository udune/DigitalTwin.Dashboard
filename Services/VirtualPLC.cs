using DigitalTwin.Dashboard.Models;

namespace DigitalTwin.Dashboard.Services
{
    internal class VirtualPLC
    {
        private const float MaxSpeed = 100.0f;
        private const float MaxAccel = 500.0f;
        private const int UpdateRate = 100;

        private bool isRunning = false;
        private CancellationTokenSource cts;

        public event Action<AxisData> OnDataUpdated;
        public event Action<string> OnError;

        // 물리적 이동 한계 = travel clamp (알람 경계와는 별개, P1). 여기서 유지.
        private const float X_LIMIT = 500f;
        private const float Y_LIMIT = 500f;
        private const float Z_MIN = -100f;
        private const float Z_MAX = 50f;

        private readonly DeviceTable _deviceTable;
        private readonly ErrorDetector _errorDetector;

        public bool IsRunning => isRunning;

        public VirtualPLC(DeviceTable deviceTable, ErrorDetector errorDetector)
        {
            _deviceTable = deviceTable;
            _errorDetector = errorDetector;
        }

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

        private void UpdateLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // ① DeviceTable에서 target / 이전 current 읽기
                    var snap = _deviceTable.Snapshot();

                    float deltaTime = 1f / UpdateRate;

                    // ② 기존 보간 로직으로 새 current 계산 (±500 / Z −100~50 travel clamp는 여기 유지 = P1)
                    float targetX = Math.Clamp(snap.TargetX, -X_LIMIT, X_LIMIT);
                    float targetY = Math.Clamp(snap.TargetY, -Y_LIMIT, Y_LIMIT);
                    float targetZ = Math.Clamp(snap.TargetZ, Z_MIN, Z_MAX);

                    float currentX = MoveTowards(snap.CurrentX, targetX, MaxSpeed * deltaTime);
                    float currentY = MoveTowards(snap.CurrentY, targetY, MaxSpeed * deltaTime);
                    float currentZ = MoveTowards(snap.CurrentZ, targetZ, MaxSpeed * deltaTime);

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

                    Thread.Sleep(1000 / UpdateRate);
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
