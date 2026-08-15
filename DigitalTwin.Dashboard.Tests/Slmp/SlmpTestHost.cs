using System.IO;
using System.Net;
using System.Net.Sockets;
using DigitalTwin.Dashboard.Models;
using DigitalTwin.Dashboard.Services;

namespace DigitalTwin.Dashboard.Tests.Slmp
{
    // SLMP 서버를 빈 포트에 띄우고 실제 TCP로 말을 거는 테스트 하네스.
    //
    // 파서를 리플렉션이나 가시성 확대로 끄집어내지 않고 실제 와이어로 검증한다.
    // ProcessFrame/BuildResponse/ReadExactAsync가 한 덩어리로 같이 검증되고,
    // 프로덕션 코드를 한 줄도 바꾸지 않아도 된다.
    internal sealed class SlmpTestHost : IDisposable
    {
        public DeviceTable Table { get; }
        public int Port { get; }

        private readonly SlmpServer _server;

        public SlmpTestHost(DeviceConfig? config = null)
        {
            Table = new DeviceTable(config ?? new DeviceConfig());
            Port = FindFreePort();
            _server = new SlmpServer(Table, Port);
            _server.Start(); // Start()는 리슨 시작까지 동기로 마친 뒤 반환한다.
        }

        public SlmpConnection Connect() => new SlmpConnection(Port);

        public void Dispose() => _server.Stop();

        private static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    // 요청 프레임을 만들고 응답을 파싱하는 최소 SLMP 3E 클라이언트.
    internal sealed class SlmpConnection : IDisposable
    {
        public const byte DevD = 0xA8;
        public const byte DevM = 0x90;

        private const ushort CmdRead = 0x0401;
        private const ushort CmdWrite = 0x1401;
        private const ushort SubWord = 0x0000;
        private const ushort SubBit = 0x0001;
        private const int Scale = 10;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        public SlmpConnection(int port)
        {
            _client = new TcpClient();
            _client.Connect(IPAddress.Loopback, port);
            _client.ReceiveTimeout = 5000;
            _stream = _client.GetStream();
        }

        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }

        // 프레임을 통째로 보내고 응답을 받는다.
        public (ushort EndCode, byte[] Data) Request(byte[] frame)
        {
            _stream.Write(frame, 0, frame.Length);
            return ReadResponse();
        }

        // 프레임을 지정한 지점에서 쪼개 보낸다(부분 수신 검증용).
        public (ushort EndCode, byte[] Data) RequestSplit(byte[] frame, params int[] cutPoints)
        {
            int offset = 0;
            foreach (int cut in cutPoints)
            {
                _stream.Write(frame, offset, cut - offset);
                _stream.Flush();
                Thread.Sleep(20); // 서버가 부분 수신 상태에 머무르는지 확인
                offset = cut;
            }
            _stream.Write(frame, offset, frame.Length - offset);
            _stream.Flush();
            return ReadResponse();
        }

        // 응답이 아예 오지 않는지 확인(비정상 프레임 드롭). true면 무응답.
        public bool ExpectNoResponse(byte[] frame, int waitMs = 300)
        {
            _stream.Write(frame, 0, frame.Length);
            _stream.Flush();
            _client.ReceiveTimeout = waitMs;
            try
            {
                var one = new byte[1];
                int n = _stream.Read(one, 0, 1);
                return n == 0; // EOF면 데이터는 안 온 것
            }
            catch (IOException)
            {
                return true; // 타임아웃 = 무응답
            }
            finally
            {
                _client.ReceiveTimeout = 5000;
            }
        }

        private (ushort, byte[]) ReadResponse()
        {
            // D0 00 + echo(5) + responseDataLength(2)
            byte[] head = ReadExact(11);
            int respLen = head[7] | (head[8] << 8);
            ushort endCode = (ushort)(head[9] | (head[10] << 8));

            byte[] data = respLen > 2 ? ReadExact(respLen - 2) : Array.Empty<byte>();
            return (endCode, data);
        }

        private byte[] ReadExact(int count)
        {
            byte[] buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buf, read, count - read);
                if (n == 0)
                {
                    throw new IOException("연결이 예상보다 먼저 끊겼습니다.");
                }
                read += n;
            }
            return buf;
        }

        // ── 프레임 빌더 ──
        public static byte[] WordRead(byte device, int number, int count)
            => Frame(CmdRead, SubWord, device, number, count, null);

        public static byte[] WordWrite(byte device, int number, params short[] words)
        {
            byte[] data = new byte[words.Length * 2];
            for (int i = 0; i < words.Length; i++)
            {
                data[i * 2] = (byte)(words[i] & 0xFF);
                data[i * 2 + 1] = (byte)((words[i] >> 8) & 0xFF);
            }
            return Frame(CmdWrite, SubWord, device, number, words.Length, data);
        }

        public static byte[] BitRead(byte device, int number, int count)
            => Frame(CmdRead, SubBit, device, number, count, null);

        public static byte[] BitWrite(byte device, int number, int count)
            => Frame(CmdWrite, SubBit, device, number, count, new byte[] { 0x10 });

        public static byte[] Frame(
            ushort command, ushort subcommand, byte deviceCode,
            int deviceNumber, int count, byte[]? writeData)
        {
            var body = new List<byte>
            {
                0x10, 0x00,                                              // monitoring timer
                (byte)(command & 0xFF), (byte)(command >> 8),
                (byte)(subcommand & 0xFF), (byte)(subcommand >> 8),
                (byte)(deviceNumber & 0xFF),
                (byte)((deviceNumber >> 8) & 0xFF),
                (byte)((deviceNumber >> 16) & 0xFF),
                deviceCode,
                (byte)(count & 0xFF), (byte)(count >> 8),
            };
            if (writeData != null)
            {
                body.AddRange(writeData);
            }

            var frame = new List<byte> { 0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00 };
            frame.Add((byte)(body.Count & 0xFF));
            frame.Add((byte)(body.Count >> 8));
            frame.AddRange(body);
            return frame.ToArray();
        }

        // ── 값 변환 ──
        public static short ToWord(float mm)
            => (short)Math.Clamp(MathF.Round(mm * Scale), short.MinValue, short.MaxValue);

        public static float WordAt(byte[] data, int index)
            => (short)(data[index * 2] | (data[index * 2 + 1] << 8)) / (float)Scale;

        public static short RawWordAt(byte[] data, int index)
            => (short)(data[index * 2] | (data[index * 2 + 1] << 8));

        public static bool BitAt(byte[] data, int index)
        {
            byte b = data[index / 2];
            int nibble = (index % 2 == 0) ? (b >> 4) & 0xF : b & 0xF;
            return nibble != 0;
        }
    }
}
