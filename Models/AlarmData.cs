using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalTwin.Dashboard.Models
{
    internal class AlarmData
    {
        public DateTime Time { get; set; }
        public string Level { get; set; }
        public string Location { get; set; }
        public string Message { get; set; }
    }
}
