using DigitalTwin.Dashboard.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalTwin.Dashboard.Services
{
    internal class VirtualPLC
    {
        private float currentX = 0f;
        private float currentY = 0f;
        private float currentZ = 0f;

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

        private const float X_LIMIT = 400f;
        private const float Y_LIMIT = 300f;
        private const float Z_MIN = -60f;
        private const float Z_MAX = 0f;

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
                    // 위치 업데이트 (부드럽게 이동)
                    float deltaTime = 1f / UpdateRate;

                    currentX = MoveTowards(currentX, targetX, MaxSpeed * deltaTime);
                    currentY = MoveTowards(currentY, targetY, MaxSpeed * deltaTime);
                    currentZ = MoveTowards(currentZ, targetZ, MaxSpeed * deltaTime);

                    // 속도 계산
                    float velX = (targetX - currentX) * UpdateRate / 10f;
                    float velY = (targetY - currentY) * UpdateRate / 10f;
                    float velZ = (targetZ - currentZ) * UpdateRate / 10f;

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

        public void MoveX(float position)
        {
            targetX = Clamp(position, -X_LIMIT, X_LIMIT);
        }

        public void MoveY(float position)
        {
            targetY = Clamp(position, -Y_LIMIT, Y_LIMIT);
        }

        public void MoveZ(float position)
        {
            targetZ = Clamp(position, Z_MIN, Z_MAX);
        }

        public void HomeAll()
        {
            targetX = 0f;
            targetY = 0f;
            targetZ = 0f;
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

        private float Clamp(float value, float min, float max)
        {
            if (value < min)
            {
                return min;
            }
            if (value > max)
            {
                return max;
            }
            return value;
        }
    }
}
