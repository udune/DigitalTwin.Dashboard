using DigitalTwin.Dashboard.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using static CommunityToolkit.Mvvm.ComponentModel.__Internals.__TaskExtensions.TaskAwaitableWithoutEndValidation;

namespace DigitalTwin.Dashboard.Services
{
    internal class UnityIPCService
    {
        private NamedPipeServerStream pipeServer;
        private StreamWriter writer;
        private bool isRunning = false;
        private bool isConnected = false;

        public bool IsConnected => isConnected;

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<string> OnError;

        private const string PipeName = "DigitalTwinPipe";

        public async Task Start()
        {
            if (isRunning)
            {
                return;
            }

            isRunning = true;

            await Task.Run(async () =>
            {
                try
                {
                    pipeServer = new NamedPipeServerStream(
                        PipeName, 
                        PipeDirection.InOut, 
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                     );

                    Console.WriteLine("유니티 연결 대기중...");

                    await pipeServer.WaitForConnectionAsync();

                    isConnected = true;

                    writer = new StreamWriter(pipeServer)
                    {
                        AutoFlush = true
                    };

                    Console.WriteLine("유니티 연결 성공!");
                    OnConnected?.Invoke();
                }
                catch (Exception e)
                {
                    isConnected = false;
                    OnError?.Invoke($"Start 오류: {e.Message}");
                }
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
                var message = new
                {
                    type = "axis_data",
                    data = new
                    {
                        x = data.X,
                        y = data.Y,
                        z = data.Z,
                        velocityX = data.VelocityX,
                        velocityY = data.VelocityY,
                        velocityZ = data.VelocityZ,
                        timestamp = data.Timestamp.ToString("o")
                    }
                };
                var json = JsonConvert.SerializeObject(message);
                writer.WriteLine(json);
            }
            catch (IOException)
            {
                isConnected = false;
                OnDisconnected?.Invoke();
            }
            catch (Exception e)
            {
                OnError?.Invoke($"SendAxis 전송 오류 {e.Message}");
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
                var message = new
                {
                    type = "error",
                    location = alarm.Location,
                    level = alarm.Level,
                    message = alarm.Message,
                    timestamp = alarm.Time.ToString("o")
                };

                var json = JsonConvert.SerializeObject(message);
                writer.WriteLine(json);
            }
            catch (IOException)
            {
                isConnected = false;
                OnDisconnected?.Invoke();
            }
            catch (Exception e)
            {
                OnError?.Invoke($"Send 전송 오류 {e.Message}");
            }
        }

        public void SendClearError()
        {
            if (!isConnected || writer == null)
            {
                return;
            }

            try
            {
                var message = new { type = "clear_error" };
                string json = JsonConvert.SerializeObject(message);
                writer.WriteLine(json);
            }
            catch (Exception e)
            {
                OnError?.Invoke($"SendClear 전송 오류 {e.Message}");
            }
        }

        public void Stop()
        {
            isRunning = false;
            isConnected = false;

            writer?.Close();
            pipeServer?.Close();

            Console.WriteLine("Service stopped");
        }
    }
}
