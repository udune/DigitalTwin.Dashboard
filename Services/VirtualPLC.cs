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
        private bool isRunning = false;

        public event Action<AxisData> OnDataUpdated;

        public async Task Start()
        {
            isRunning = true;

            await Task.Run(() =>
            {
                while (isRunning)
                {
                    // 위치 업데이트 (부드럽게 이동)
                    currentX = MoveTowards(currentX, targetX, MaxSpeed * 0.01f);
                    currentY = MoveTowards(currentY, targetY, MaxSpeed * 0.01f);
                    currentZ = MoveTowards(currentZ, targetZ, MaxSpeed * 0.01f);

                    // 데이터 전송
                    OnDataUpdated?.Invoke(new AxisData
                    {
                        X = currentX,
                        Y = currentY,
                        Z = currentZ,
                        VelocityX = (targetX - currentX) * 10,
                        VelocityY = (targetY - currentY) * 10,
                        VelocityZ = (targetZ - currentZ) * 10,
                        Timestamp = DateTime.Now
                    });

                    Thread.Sleep(10); // 100Hz
                }
            });
        }

        public void Stop()
        {
            isRunning = false;
        }

        public void MoveX(float position)
        {
            targetX = Math.Clamp(position, -400f, 400f);
        }

        public void MoveY(float position)
        {
            targetY = Math.Clamp(position, -300f, 300f);
        }

        public void MoveZ(float position)
        {
            targetZ = Math.Clamp(position, -60f, 0f);
        }

        public void HomeAll()
        {
            targetX = 0f;
            targetY = 0f;
            targetZ = 0f;
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
