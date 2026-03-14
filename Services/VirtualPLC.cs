using DigitalTwin.Dashboard.Models;

namespace DigitalTwin.Dashboard.Services
{
    internal class VirtualPLC
    {
        private float currentX = 0f;
        private float currentY = 0f;
        private float currentZ = 0f;

        private float previousX = 0f;
        private float previousY = 0f;
        private float previousZ = 0f;

        private float targetX = 0f;
        private float targetY = 0f;
        private float targetZ = 0f;

        private const float MaxSpeed = 100.0f;
        private const float MaxAccel = 500.0f;
        private const int UpdateRate = 100;

        private bool isRunning = false;
        private CancellationTokenSource cts;

        public event Action<AxisData> OnDataUpdated;
        public event Action<string> OnError;

        // 물리적 이동 한계 (알람은 ErrorDetector에서 처리)
        private const float X_LIMIT = 500f;
        private const float Y_LIMIT = 500f;
        private const float Z_MIN = -100f;
        private const float Z_MAX = 50f;

        public bool IsRunning => isRunning;

        public async Task Start()
        {
            if (isRunning)
            {
                return;
            }

            isRunning = true;
            cts = new CancellationTokenSource();

            await Task.Run(() => UpdateLoop(cts.Token));
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
                    // 이전 위치 저장
                    previousX = currentX;
                    previousY = currentY;
                    previousZ = currentZ;

                    // 위치 업데이트 (부드럽게 이동)
                    float deltaTime = 1f / UpdateRate;

                    currentX = MoveTowards(currentX, targetX, MaxSpeed * deltaTime);
                    currentY = MoveTowards(currentY, targetY, MaxSpeed * deltaTime);
                    currentZ = MoveTowards(currentZ, targetZ, MaxSpeed * deltaTime);

                    // 속도 계산 (실제 이동한 거리 / 시간)
                    float velX = (currentX - previousX) / deltaTime;
                    float velY = (currentY - previousY) / deltaTime;
                    float velZ = (currentZ - previousZ) / deltaTime;

                    // 데이터 전송
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

        public void MoveX(float position) => targetX = Math.Clamp(position, -X_LIMIT, X_LIMIT);

        public void MoveY(float position) => targetY = Math.Clamp(position, -Y_LIMIT, Y_LIMIT);

        public void MoveZ(float position) => targetZ = Math.Clamp(position, Z_MIN, Z_MAX);

        public void HomeAll()
        {
            targetX = 0f;
            targetY = 0f;
            targetZ = 0f;
        }

        public void SetPosition(float x, float y, float z)
        {
            // Unity에서 받은 위치를 직접 설정 (현재 위치와 타겟 위치 모두)
            currentX = Math.Clamp(x, -X_LIMIT, X_LIMIT);
            currentY = Math.Clamp(y, -Y_LIMIT, Y_LIMIT);
            currentZ = Math.Clamp(z, Z_MIN, Z_MAX);

            targetX = currentX;
            targetY = currentY;
            targetZ = currentZ;
        }

        public void EmergencyStop()
        {
            targetX = currentX;
            targetY = currentY;
            targetZ = currentZ;
        }

        public (float X, float Y, float Z) GetCurrentPosition()
        {
            return (currentX, currentY, currentZ);
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
