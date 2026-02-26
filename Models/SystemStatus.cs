using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalTwin.Dashboard.Models
{
    internal class SystemStatus
    {
        // PLC 연결 상태
        public bool IsPlcConnected { get; set; }

        // Unity 연결 상태
        public bool IsUnityConnected { get; set; }

        // 시스템 가동 상태
        public bool IsRunning { get; set; }

        // Manual, Auto, Simulation
        public string CurrentMode { get; set; }

        // 금일 사이클 횟수
        public int TodayCycleCount { get; set; }

        // 평균 사이클 타임 (초)
        public double AverageCycleTime { get; set; }

        // 금일 알람 수
        public int TodayAlarmCount { get; set; }         

        public DateTime LastUpdateTime { get; set; }

        public SystemStatus()
        {
            CurrentMode = "Manual";
            LastUpdateTime = DateTime.Now;
        }
    }
}
