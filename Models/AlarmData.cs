using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalTwin.Dashboard.Models
{
    internal class AlarmData
    {
        public int Id { get; set; }
        public DateTime Time { get; set; }
        public string Level { get; set; }
        public string Location { get; set; }
        public string Message { get; set; }
        public bool IsAcknowledged { get; set; }

        // 그룹화된 알람 정보
        public int Count { get; set; }
        public DateTime FirstTime { get; set; }
        public DateTime LastTime { get; set; }
        public bool IsExpanded { get; set; }
        public List<DateTime> OccurrenceTimes { get; set; }

        public AlarmData()
        {
            Time = DateTime.Now;
            FirstTime = DateTime.Now;
            LastTime = DateTime.Now;
            IsAcknowledged = false;
            Count = 1;
            IsExpanded = false;
            OccurrenceTimes = new List<DateTime> { DateTime.Now };
        }

        public string TimeString => LastTime.ToString("HH:mm:ss");

        public string TimeRangeString
        {
            get
            {
                if (Count == 1)
                    return FirstTime.ToString("HH:mm:ss");
                return $"{FirstTime:HH:mm:ss} ~ {LastTime:HH:mm:ss}";
            }
        }

        public string CountString => Count > 1 ? $"×{Count}" : "";

        public string LevelIcon => Level switch
        {
            "Error" => "🔴",
            "Warning" => "🟡",
            "Info" => "🔵",
            _ => "⚪"
        };

        // 알람 그룹 키 생성 (같은 알람인지 판별)
        public string GetGroupKey() => $"{Level}|{Location}|{Message}";
    }
}
