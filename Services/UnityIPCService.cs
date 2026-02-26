using DigitalTwin.Dashboard.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace DigitalTwin.Dashboard.Services
{
    internal class UnityIPCService
    {
        private NamedPipeServerStream pipeServer;
        private StreamWriter writer;
        private bool isRunning = false;

        public async Task Start()
        {
            isRunning = true;

            await Task.Run(() =>
            {
                pipeServer = new NamedPipeServerStream("DigitalTwinPipe", PipeDirection.InOut, 1);
                Console.WriteLine("유니티 연결 대기중...");
                pipeServer.WaitForConnection();
                Console.WriteLine("유니티 연결 성공!");

                writer = new StreamWriter(pipeServer)
                {
                    AutoFlush = true
                };
            });
        }

        public void SendAxisData(AxisData data)
        {
            if (!isRunning || writer == null)
            {
                return;
            }

            try
            {
                var json = JsonConvert.SerializeObject(
                    new
                    {
                        type = "axis_data",
                        data = data
                    });

                writer.WriteLine(json);
            }
            catch (Exception e)
            {
                Console.WriteLine($"전송 오류 {e.Message}");
            }
        }

        public void SendError(AlarmData alarm)
        {
            if (!isRunning || writer == null)
            {
                return;
            }

            try
            {
                var json = JsonConvert.SerializeObject(new
                {
                    type = "error",
                    location = alarm.Location,
                    level = alarm.Level,
                    message = alarm.Message
                });

                writer.WriteLine(json);
            }
            catch (Exception e)
            {
                Console.WriteLine($"전송 오류 {e.Message}");
            }
        }

        public void Stop()
        {
            isRunning = false;
            writer?.Close();
            pipeServer?.Close();
        }
    }
}
