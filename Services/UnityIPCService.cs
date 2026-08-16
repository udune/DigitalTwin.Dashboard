using DigitalTwin.Dashboard.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.IO.Pipes;

namespace DigitalTwin.Dashboard.Services
{
    internal class UnityIPCService
    {
        private NamedPipeServerStream pipeServer;
        private StreamWriter writer;
        private StreamReader reader;
        private volatile bool isRunning = false;
        private volatile bool isConnected = false;
        private CancellationTokenSource readCancellation;
        private Task acceptLoopTask;

        // writer/reader는 재연결 때마다 교체되고, 100Hz 제어 스레드·UI 스레드·Accept 루프가
        // 동시에 접근하므로 반드시 lock으로 보호한다. (§4-4)
        private readonly object _writeLock = new object();

        public bool IsConnected => isConnected;

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<string> OnError;

        private const string PipeName = "DigitalTwinPipe";

        private readonly DeviceTable _deviceTable;

        public UnityIPCService(DeviceTable deviceTable)
        {
            _deviceTable = deviceTable;
        }

        public Task Start()
        {
            if (isRunning)
            {
                return Task.CompletedTask;
            }

            isRunning = true;
            readCancellation = new CancellationTokenSource();

            // Accept 루프를 Task로 보관해 두었다가 Stop()에서 종료를 짧게 기다린다. (§4-5)
            acceptLoopTask = Task.Run(() => AcceptLoop(readCancellation.Token));
            return Task.CompletedTask;
        }

        // Unity가 끊기면 파이프를 정리하고 다시 대기 상태로 돌아간다.
        // STOP 또는 종료(취소) 전까지 이 반복을 계속한다. (§3, §4-1)
        private async Task AcceptLoop(CancellationToken token)
        {
            while (isRunning && !token.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                bool wasConnected = false;

                try
                {
                    // 매 회차마다 새 인스턴스를 만들고, finally에서 Dispose 한다.
                    // 안 치우면 max instances=1 자리를 계속 점유해 다음 연결이 실패한다. (§2-(d))
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                     );

                    pipeServer = server;

                    Console.WriteLine("유니티 연결 대기중...");

                    await server.WaitForConnectionAsync(token);

                    // 연결 성립
                    SetStream(new StreamWriter(server) { AutoFlush = true },
                              new StreamReader(server));
                    isConnected = true;
                    wasConnected = true;

                    Console.WriteLine("유니티 연결 성공!");
                    OnConnected?.Invoke();

                    // ★ 반드시 await — 던져놓으면 연결 종료 시점을 알 수 없어 재대기가 불가능. (§4-2)
                    await ReadLoop(token);
                }
                catch (OperationCanceledException)
                {
                    break;                       // STOP 또는 종료
                }
                catch (Exception e)
                {
                    OnError?.Invoke($"IPC 오류: {e.Message}");
                }
                finally
                {
                    isConnected = false;
                    SetStream(null, null);       // writer/reader 교체 + 정리
                    try { server?.Dispose(); } catch { }

                    // 끊김 통보는 여기 한 곳에서만, 실제로 연결됐던 경우에만 1회. (§4-3)
                    if (wasConnected) OnDisconnected?.Invoke();
                }

                // 연속 실패 시 CPU를 태우지 않도록 재대기 전 짧은 지연. (§4-2 #5)
                if (isRunning && !token.IsCancellationRequested)
                {
                    try { await Task.Delay(500, token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            isRunning = false;
        }

        // 읽기 루프는 isConnected = false 만 설정하고 조용히 빠져나온다.
        // 끊김 통보(OnDisconnected)는 AcceptLoop의 finally가 전담한다. (§4-3)
        private async Task ReadLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (isRunning && !cancellationToken.IsCancellationRequested)
                {
                    string json = await reader.ReadLineAsync(cancellationToken);

                    if (json == null)
                    {
                        // 상대가 연결을 닫음(정상 EOF)
                        break;
                    }

                    ProcessMessage(json);
                }
            }
            catch (IOException)
            {
                // 파이프가 급하게 끊긴 경우도 정상적인 끊김으로 처리 — 조용히 빠져나온다.
            }
            finally
            {
                isConnected = false;
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
                        // 수신부는 어댑터로 강등: DeviceTable에 target 직접 기록(P4, last-writer-wins).
                        // Unity가 보내는 velocity는 항상 0이므로 사용하지 않는다(WPF가 자체 산출).
                        float x = data["x"]?.Value<float>() ?? 0;
                        float y = data["y"]?.Value<float>() ?? 0;
                        float z = data["z"]?.Value<float>() ?? 0;

                        _deviceTable.SetTarget(x, y, z);
                    }
                }
            }
            catch (Exception e)
            {
                OnError?.Invoke($"메시지 파싱 오류: {e.Message}");
            }
        }

        // writer/reader 교체는 반드시 lock 안에서. 교체 시 이전 writer를 정리한다.
        // 주의: StreamWriter.Dispose는 내부 스트림(파이프)까지 닫지만, 여기선 매 회차
        // 새 파이프를 쓰고 finally에서 그 파이프를 Dispose 하므로 이중 정리로 안전하다. (§4-4)
        private void SetStream(StreamWriter w, StreamReader r)
        {
            lock (_writeLock)
            {
                try { writer?.Dispose(); } catch { }
                writer = w;
                reader = r;
            }
        }

        private void SendMessage(object message)
        {
            lock (_writeLock)
            {
                if (!isRunning || !isConnected || writer == null) return;

                try
                {
                    writer.WriteLine(JsonConvert.SerializeObject(message));
                }
                catch (IOException)
                {
                    // 끊김 통보는 AcceptLoop에서 전담. 여기선 상태만 내린다. (§4-3, §4-4)
                    isConnected = false;
                }
                catch (Exception e)
                {
                    OnError?.Invoke($"전송 오류: {e.Message}");
                }
            }
        }

        public void SendAxisData(AxisData data) => SendMessage(new
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
        });

        // code는 Unity가 오류를 식별하는 Id다. error_clear가 같은 code를 보내 해제하므로
        // 양쪽이 같은 키를 봐야 한다. Unity는 code가 비면 메시지를 무시한다.
        public void SendError(AlarmData alarm) => SendMessage(new
        {
            type = "error",
            code = alarm.Code,                        // "X_LIMIT" 등 — 오류 식별자
            source = ToUnitySource(alarm.Location),  // "X_AXIS" → "XAxis"
            errorType = alarm.Level,                  // "Error"/"Warning" (값은 그대로 일치)
            message = alarm.Message,
            timestamp = alarm.Time.ToString("o")
        });

        // 조건이 해소됐을 때 해당 오류 하나만 거두게 한다.
        // 이게 없으면 Unity는 clear_all_errors 전까지 오류 표시를 영원히 들고 있는다.
        public void SendErrorClear(string code) => SendMessage(new
        {
            type = "error_clear",
            code,
            timestamp = DateTime.Now.ToString("o")
        });

        public void SendClearError() => SendMessage(new { type = "clear_all_errors" });

        // AlarmData.Location("X_AXIS") → Unity ParseErrorSource가 받는 형식("XAxis")으로 변환.
        // (ErrorDetector 내부의 Location 문자열은 dedup 키·UI에 쓰이므로 건드리지 않고, 송신 경계에서만 변환)
        // 축이 아닌 위치(SYSTEM)도 반드시 매핑한다 — Unity는 모르는 source를 무시하므로
        // 빠뜨리면 해당 알람이 뷰어에서 조용히 사라진다.
        private static string ToUnitySource(string location) => location switch
        {
            "X_AXIS" => "XAxis",
            "Y_AXIS" => "YAxis",
            "Z_AXIS" => "ZAxis",
            "SYSTEM" => "System",
            _ => location
        };

        public void Stop()
        {
            isRunning = false;
            isConnected = false;

            // 반복문 종료 신호. 스트림 정리는 AcceptLoop의 finally가 전담하므로
            // 여기서 직접 닫지 않는다(이중 정리 예외 방지). (§4-5)
            readCancellation?.Cancel();

            // Accept 루프가 정리를 마칠 때까지 짧게 기다린다.
            try { acceptLoopTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }

            Console.WriteLine("Service stopped");
        }
    }
}
