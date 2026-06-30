using System.IO;
using System.Net;
using System.Net.Sockets;

namespace DigitalTwin.Dashboard.Services
{
    // SLMP 3E(binary, little-endian) 서버 어댑터.
    // 외부 SLMP 클라이언트(UaExpert 류 아님 — PLC 통신용)가 DeviceTable의
    // 위치/속도/타겟/한계/에러를 워드·비트로 읽고(쓰기 범위 내) 쓸 수 있게 한다.
    //
    // 경계(STEP 2B):
    //  - DeviceTable 내부 표현(float mm)은 건드리지 않는다. 정수 워드 변환은 이 파일 안에서만.
    //  - 읽기는 Snapshot(), 쓰기는 SetTarget/SetLimits 뿐. DeviceTable이 이미 thread-safe하므로
    //    SLMP 스레드가 4번째 writer로 들어와도 새 lock이 필요 없다.
    //  - Named Pipe·UI·VirtualPLC·ErrorDetector는 건드리지 않는 순수 추가 어댑터다.
    internal class SlmpServer
    {
        // 0.1mm 분해능. float→워드 변환의 유일한 SCALE 정의처(어댑터 밖으로 새지 않음).
        private const int Scale = 10;

        // 디바이스 코드 (SLMP 3E binary)
        private const byte DeviceCodeD = 0xA8;
        private const byte DeviceCodeM = 0x90;

        // End code
        private const ushort EndOk = 0x0000;
        private const ushort EndUnsupported = 0xC059; // 미지원 명령/디바이스/읽기전용 쓰기
        private const ushort EndCountRange = 0xC051;  // 요청 개수 범위 초과

        private const int MaxPointsPerRequest = 960; // SLMP 워드 읽기 상한(방어용)

        private readonly DeviceTable _deviceTable;
        private readonly int _port;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private volatile bool _isRunning;

        public event Action<string>? OnError;

        public bool IsRunning => _isRunning;

        public SlmpServer(DeviceTable deviceTable, int port = 5007)
        {
            _deviceTable = deviceTable;
            _port = port;
        }

        public void Start()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();

            Console.WriteLine($"SLMP 3E 서버 리슨 시작: :{_port}");

            _ = Task.Run(() => AcceptLoop(_cts.Token));
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _cts?.Cancel();

            try
            {
                _listener?.Stop();
            }
            catch
            {
                // 리스너 종료 중 예외는 무시
            }

            Console.WriteLine("SLMP 서버 정지");
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _listener != null)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleClient(client, token));
                }
            }
            catch (OperationCanceledException)
            {
                // 정상 종료
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                {
                    OnError?.Invoke($"SLMP Accept 오류: {e.Message}");
                }
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken token)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    while (!token.IsCancellationRequested)
                    {
                        // ── 부분 수신 처리 ──
                        // 고정 헤더(Subheader~Request data length = 9바이트)를 먼저 채운다.
                        byte[] header = new byte[9];
                        if (!await ReadExactAsync(stream, header, 0, 9, token))
                        {
                            break; // 연결 종료
                        }

                        // Request data length(LE)로 이후 본문 길이를 구해 나머지를 채울 때까지 버퍼링.
                        int bodyLength = header[7] | (header[8] << 8);
                        byte[] frame = new byte[9 + bodyLength];
                        Array.Copy(header, frame, 9);

                        if (bodyLength > 0 &&
                            !await ReadExactAsync(stream, frame, 9, bodyLength, token))
                        {
                            break; // 본문 도중 연결 종료
                        }

                        byte[] response = ProcessFrame(frame);
                        if (response.Length > 0)
                        {
                            await stream.WriteAsync(response, token);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 정상 종료
            }
            catch (IOException)
            {
                // 클라이언트 연결 끊김
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                {
                    OnError?.Invoke($"SLMP 클라이언트 처리 오류: {e.Message}");
                }
            }
        }

        // 스트림에서 정확히 count바이트를 채운다. 끝나면 false.
        private static async Task<bool> ReadExactAsync(
            NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), token);
                if (n == 0)
                {
                    return false; // EOF
                }
                read += n;
            }
            return true;
        }

        // ── 프레임 파서 + 디스패처 ──
        private byte[] ProcessFrame(byte[] frame)
        {
            // 최소 길이(헤더9 + 모니터링타이머2 + 커맨드2 + 서브2 + 디바이스번호3 + 코드1 + 개수2 = 21) 검증
            if (frame.Length < 21 || frame[0] != 0x50 || frame[1] != 0x00)
            {
                return Array.Empty<byte>(); // 비정상 프레임은 무응답(드롭)
            }

            // 응답 에코용 네트워크 5바이트 (network, PC, 모듈I/O 2, station)
            byte[] echo = new byte[5];
            Array.Copy(frame, 2, echo, 0, 5);

            ushort command = (ushort)(frame[11] | (frame[12] << 8));
            ushort subcommand = (ushort)(frame[13] | (frame[14] << 8));
            int deviceNumber = frame[15] | (frame[16] << 8) | (frame[17] << 16);
            byte deviceCode = frame[18];
            int count = frame[19] | (frame[20] << 8);

            // 쓰기 데이터는 21바이트 이후
            int dataOffset = 21;
            int dataLength = frame.Length - dataOffset;

            // 4종 분기: Word Read(0401/0000), Word Write(1401/0000),
            //           Bit Read(0401/0001), Bit Write(1401/0001)
            if (command == 0x0401 && subcommand == 0x0000)
            {
                return HandleWordRead(echo, deviceCode, deviceNumber, count);
            }
            if (command == 0x1401 && subcommand == 0x0000)
            {
                return HandleWordWrite(echo, deviceCode, deviceNumber, count, frame, dataOffset, dataLength);
            }
            if (command == 0x0401 && subcommand == 0x0001)
            {
                return HandleBitRead(echo, deviceCode, deviceNumber, count);
            }
            if (command == 0x1401 && subcommand == 0x0001)
            {
                // 본 맵에 쓰기 가능한 M 없음 → 미지원
                return BuildResponse(echo, EndUnsupported, Array.Empty<byte>());
            }

            return BuildResponse(echo, EndUnsupported, Array.Empty<byte>());
        }

        // ── 핸들러: Word Read ──
        private byte[] HandleWordRead(byte[] echo, byte deviceCode, int start, int count)
        {
            if (deviceCode != DeviceCodeD)
            {
                return BuildResponse(echo, EndUnsupported, Array.Empty<byte>());
            }
            if (count <= 0 || count > MaxPointsPerRequest)
            {
                return BuildResponse(echo, EndCountRange, Array.Empty<byte>());
            }

            // Snapshot 1회만 — 일관된 시점의 값으로 응답.
            var snap = _deviceTable.Snapshot();
            byte[] data = new byte[count * 2];

            for (int i = 0; i < count; i++)
            {
                float? value = ReadDWord(snap, start + i);
                if (value == null)
                {
                    return BuildResponse(echo, EndUnsupported, Array.Empty<byte>()); // 맵 밖 주소
                }

                short word = FloatToWord(value.Value);
                data[i * 2] = (byte)(word & 0xFF);
                data[i * 2 + 1] = (byte)((word >> 8) & 0xFF);
            }

            return BuildResponse(echo, EndOk, data);
        }

        // ── 핸들러: Word Write ──
        private byte[] HandleWordWrite(
            byte[] echo, byte deviceCode, int start, int count,
            byte[] frame, int dataOffset, int dataLength)
        {
            if (deviceCode != DeviceCodeD)
            {
                return BuildResponse(echo, EndUnsupported, Array.Empty<byte>());
            }
            if (count <= 0 || count > MaxPointsPerRequest)
            {
                return BuildResponse(echo, EndCountRange, Array.Empty<byte>());
            }
            if (dataLength < count * 2)
            {
                return BuildResponse(echo, EndCountRange, Array.Empty<byte>());
            }

            // 쓰기 전 전 주소 유효성·쓰기가능 여부를 먼저 검사 → 부분 적용 방지(상태 불변 보장).
            for (int i = 0; i < count; i++)
            {
                int d = start + i;
                bool writable = (d >= 6 && d <= 8) || (d >= 10 && d <= 15);
                if (!writable)
                {
                    return BuildResponse(echo, EndUnsupported, Array.Empty<byte>());
                }
            }

            // 현재 값을 기준으로 working copy 구성 (단일 워드만 와도 나머지는 현재값 유지).
            var snap = _deviceTable.Snapshot();
            float tx = snap.TargetX, ty = snap.TargetY, tz = snap.TargetZ;
            float xMin = snap.XMin, xMax = snap.XMax;
            float yMin = snap.YMin, yMax = snap.YMax;
            float zMin = snap.ZMin, zMax = snap.ZMax;

            bool targetTouched = false;
            bool limitTouched = false;

            for (int i = 0; i < count; i++)
            {
                int d = start + i;
                short word = (short)(frame[dataOffset + i * 2] | (frame[dataOffset + i * 2 + 1] << 8));
                float v = WordToFloat(word);

                switch (d)
                {
                    case 6: tx = v; targetTouched = true; break;
                    case 7: ty = v; targetTouched = true; break;
                    case 8: tz = v; targetTouched = true; break;
                    case 10: xMin = v; limitTouched = true; break;
                    case 11: xMax = v; limitTouched = true; break;
                    case 12: yMin = v; limitTouched = true; break;
                    case 13: yMax = v; limitTouched = true; break;
                    case 14: zMin = v; limitTouched = true; break;
                    case 15: zMax = v; limitTouched = true; break;
                }
            }

            if (targetTouched)
            {
                _deviceTable.SetTarget(tx, ty, tz);
            }
            if (limitTouched)
            {
                _deviceTable.SetLimits(xMin, xMax, yMin, yMax, zMin, zMax);
            }

            return BuildResponse(echo, EndOk, Array.Empty<byte>());
        }

        // ── 핸들러: Bit Read ──
        private byte[] HandleBitRead(byte[] echo, byte deviceCode, int start, int count)
        {
            if (deviceCode != DeviceCodeM)
            {
                return BuildResponse(echo, EndUnsupported, Array.Empty<byte>());
            }
            if (count <= 0 || count > MaxPointsPerRequest)
            {
                return BuildResponse(echo, EndCountRange, Array.Empty<byte>());
            }

            var snap = _deviceTable.Snapshot();

            // 2점/바이트: 상위니블=앞 점, 하위니블=뒤 점, 각 니블 0x0/0x1. 홀수면 마지막 하위니블 0.
            byte[] data = new byte[(count + 1) / 2];

            for (int i = 0; i < count; i++)
            {
                bool? bit = ReadMBit(snap, start + i);
                if (bit == null)
                {
                    return BuildResponse(echo, EndUnsupported, Array.Empty<byte>()); // 맵 밖 주소
                }

                byte nibble = (byte)(bit.Value ? 0x1 : 0x0);
                if (i % 2 == 0)
                {
                    data[i / 2] |= (byte)(nibble << 4); // 상위 니블
                }
                else
                {
                    data[i / 2] |= nibble; // 하위 니블
                }
            }

            return BuildResponse(echo, EndOk, data);
        }

        // ── 디바이스 맵 (§3): D 워드 읽기 ──
        private static float? ReadDWord(DeviceSnapshot s, int d) => d switch
        {
            0 => s.CurrentX,
            1 => s.CurrentY,
            2 => s.CurrentZ,
            3 => s.VelocityX,
            4 => s.VelocityY,
            5 => s.VelocityZ,
            6 => s.TargetX,
            7 => s.TargetY,
            8 => s.TargetZ,
            10 => s.XMin,
            11 => s.XMax,
            12 => s.YMin,
            13 => s.YMax,
            14 => s.ZMin,
            15 => s.ZMax,
            _ => null, // D9 등 맵 밖
        };

        // ── 디바이스 맵 (§3): M 비트 읽기 ──
        private static bool? ReadMBit(DeviceSnapshot s, int m) => m switch
        {
            100 => s.ErrorLamp,
            101 => s.XError,
            102 => s.YError,
            103 => s.ZError,
            _ => null,
        };

        // ── float↔워드 매핑 유틸 (§2) ──
        private static short FloatToWord(float v)
            => (short)Math.Clamp(MathF.Round(v * Scale), short.MinValue, short.MaxValue);

        private static float WordToFloat(short w)
            => w / (float)Scale;

        // ── 응답 프레임 빌더 ──
        private static byte[] BuildResponse(byte[] echo, ushort endCode, byte[] data)
        {
            // D0 00 + echo(5) + ResponseDataLength(2,LE) + EndCode(2,LE) + data
            int responseDataLength = 2 + data.Length; // endcode(2) + data
            byte[] response = new byte[2 + 5 + 2 + responseDataLength];

            int p = 0;
            response[p++] = 0xD0;
            response[p++] = 0x00;

            Array.Copy(echo, 0, response, p, 5);
            p += 5;

            response[p++] = (byte)(responseDataLength & 0xFF);
            response[p++] = (byte)((responseDataLength >> 8) & 0xFF);

            response[p++] = (byte)(endCode & 0xFF);
            response[p++] = (byte)((endCode >> 8) & 0xFF);

            if (data.Length > 0)
            {
                Array.Copy(data, 0, response, p, data.Length);
            }

            return response;
        }
    }
}
