using System;
using System.Collections.Generic;
using System.Text;

namespace DigitalTwin.Dashboard.Models
{
    internal class AxisData
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float VelocityX { get; set; }
        public float VelocityY { get; set; }
        public float VelocityZ { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
