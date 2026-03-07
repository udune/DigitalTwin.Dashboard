using DigitalTwin.Dashboard.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        private StreamReader reader;
        private bool isRunning = false;
        private bool isConnected = false;
        private CancellationTokenSource readCancellation;

        public bool IsConnected => isConnected;

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<string> OnError;
        public event Action<AxisData> OnAxisDataReceived;

        private const string PipeName = "DigitalTwinPipe";

        public async Task Start()
        {
            if (isRunning)
            {
                return;
            }

            isRunning = true;
            readCancellation = new CancellationTokenSource();

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

                    reader = new StreamReader(pipeServer);

                    Console.WriteLine("유니티 연결 성공!");
                    OnConnected?.Invoke();

                    // 읽기 루프 시작
                    _ = StartReadLoop(readCancellation.Token);
                }
                catch (Exception e)
                {
                    isConnected = false;
                    OnError?.Invoke($"Start 오류: {e.Message}");
                }
            });
        }

        private async Task StartReadLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (isRunning && !cancellationToken.IsCancellationRequested)
                {
                    string json = await reader.ReadLineAsync();

                    if (json == null)
                    {
                        // 연결 종료
                        isConnected = false;
                        OnDisconnected?.Invoke();
                        break;
                    }

                    ProcessMessage(json);
                }
            }
            catch (IOException)
            {
                isConnected = false;
                OnDisconnected?.Invoke();
            }
            catch (Exception e)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    OnError?.Invoke($"읽기 오류: {e.Message}");
                }
            }
        }

        private void ProcessMessage(string json)
        {
            try
            {
                var jObject = JObject.Parse(json);
                string messageType = jObject["type"]?.ToString();

                if (messageType == "axis_data")
                {
                    var data = jObject["data"];
                    if (data != null)
                    {
                        var axisData = new AxisData
                        {
                            X = data["x"]?.Value<float>() ?? 0,
                            Y = data["y"]?.Value<float>() ?? 0,
                            Z = data["z"]?.Value<float>() ?? 0,
                            VelocityX = data["velocityX"]?.Value<float>() ?? 0,
                            VelocityY = data["velocityY"]?.Value<float>() ?? 0,
                            VelocityZ = data["velocityZ"]?.Value<float>() ?? 0,
                            Timestamp = DateTime.Now
                        };

                        OnAxisDataReceived?.Invoke(axisData);
                    }
                }
            }
            catch (Exception e)
            {
                OnError?.Invoke($"메시지 파싱 오류: {e.Message}");
            }
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

            readCancellation?.Cancel();
            reader?.Close();
            writer?.Close();
            pipeServer?.Close();

            Console.WriteLine("Service stopped");
        }
    }
}
