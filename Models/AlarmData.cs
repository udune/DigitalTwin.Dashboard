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

        public AlarmData()
        {
            Time = DateTime.Now;
            IsAcknowledged = false;
        }

        public string TimeString => Time.ToString("HH:mm:ss");

        public string LevelIcon => Level switch
        {
            "Error" => "🔴",
            "Warning" => "🟡",
            "Info" => "🔵",
            _ => "⚪"
        };
    }
}
