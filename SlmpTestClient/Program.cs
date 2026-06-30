using System.Net.Sockets;

// ───────────────────────────────────────────────────────────────────────────
// SLMP 3E 서버(STEP 2B) 자기검증용 미니 클라이언트.  ※ 데모/제품 아님.
//
// 사용법:
//   1) WPF 앱(DigitalTwin.Dashboard)을 실행하고 [시작] 버튼을 눌러 PLC 루프를 가동한다.
//   2) dotnet run --project SlmpTestClient
//
// 점검 시나리오(update.md §6 / T6):
//   ① D0~D2 Word Read 로 현재 위치 확인
//   ② D6~D8 Word Write 로 타겟 이동 후 D0~D2 재독해 변화 확인
//   ③ M100 Bit Read
//   ④ D11(XMax)을 현재 X 아래로 Write → M100=ON 확인
//   ⑤ (추가) 읽기전용/맵밖 쓰기 → 0이 아닌 end code, 상태 불변
//   ⑥ (추가) 쪼개진 프레임(부분 수신) 정상 파싱
// ───────────────────────────────────────────────────────────────────────────

const string Host = "127.0.0.1";
const int Port = 5007;
const int Scale = 10;

// SLMP 3E 상수
const ushort CmdRead = 0x0401;
const ushort CmdWrite = 0x1401;
const ushort SubWord = 0x0000;
const ushort SubBit = 0x0001;
const byte DevD = 0xA8;
const byte DevM = 0x90;

int passed = 0;
int failed = 0;

void Check(string label, bool ok, string detail)
{
    if (ok) { passed++; Console.WriteLine($"  [PASS] {label} — {detail}"); }
    else { failed++; Console.WriteLine($"  [FAIL] {label} — {detail}"); }
}

using var client = new TcpClient();
try
{
    client.Connect(Host, Port);
}
catch (Exception ex)
{
    Console.WriteLine($"서버(:{Port}) 연결 실패: {ex.Message}");
    Console.WriteLine("WPF 앱을 먼저 실행하고 [시작]을 눌렀는지 확인하세요.");
    return 1;
}

var stream = client.GetStream();

Console.WriteLine($"SLMP 3E 서버 {Host}:{Port} 연결됨\n");

// ── 시나리오 ① D0~D2 Word Read ──
Console.WriteLine("① D0~D2 Word Read (현재 위치)");
var (ec1, data1) = Request(BuildWordRead(DevD, 0, 3));
Check("D0~D2 읽기 endcode=0", ec1 == 0, $"endcode=0x{ec1:X4}");
float cx0 = WordToFloat(data1, 0), cy0 = WordToFloat(data1, 1), cz0 = WordToFloat(data1, 2);
Console.WriteLine($"     현재 X={cx0:F1} Y={cy0:F1} Z={cz0:F1} mm");

// ── 시나리오 ② D6~D8 Word Write 후 재독해 ──
Console.WriteLine("\n② D6~D8 Word Write (타겟 이동) 후 D0~D2 재독해");
float tgX = 30f, tgY = 20f, tgZ = -10f;
var (ec2, _) = Request(BuildWordWrite(DevD, 6, new[] { ToWord(tgX), ToWord(tgY), ToWord(tgZ) }));
Check("D6~D8 쓰기 endcode=0", ec2 == 0, $"endcode=0x{ec2:X4}");

// PLC 보간 루프가 타겟까지 이동할 시간을 준다.
Thread.Sleep(2000);

var (_, data2) = Request(BuildWordRead(DevD, 0, 3));
float cx1 = WordToFloat(data2, 0), cy1 = WordToFloat(data2, 1), cz1 = WordToFloat(data2, 2);
Console.WriteLine($"     이동 후 X={cx1:F1} Y={cy1:F1} Z={cz1:F1} mm");
Check("현재 위치가 타겟 방향으로 변함",
    Math.Abs(cx1 - cx0) > 0.05f || Math.Abs(cy1 - cy0) > 0.05f || Math.Abs(cz1 - cz0) > 0.05f,
    $"ΔX={cx1 - cx0:F1} ΔY={cy1 - cy0:F1} ΔZ={cz1 - cz0:F1}");

// 타겟 재독해(D6)도 확인
var (_, dataT) = Request(BuildWordRead(DevD, 6, 3));
Check("D6(TargetX) 쓴 값 반영", Math.Abs(WordToFloat(dataT, 0) - tgX) < 0.2f,
    $"TargetX={WordToFloat(dataT, 0):F1} (기대 {tgX:F1})");

// ── 시나리오 ③ M100 Bit Read ──
Console.WriteLine("\n③ M100~M103 Bit Read");
var (ec3, data3) = Request(BuildBitRead(DevM, 100, 4));
Check("M100~M103 읽기 endcode=0", ec3 == 0, $"endcode=0x{ec3:X4}");
bool[] bits = UnpackBits(data3, 4);
Console.WriteLine($"     M100(ErrorLamp)={bits[0]} M101(X)={bits[1]} M102(Y)={bits[2]} M103(Z)={bits[3]}");

// ── 시나리오 ④ D11(XMax)을 현재 X 아래로 → M100=ON ──
Console.WriteLine("\n④ D11(XMax)을 현재 X 아래로 Write → M100=ON 확인");
var (_, dataNow) = Request(BuildWordRead(DevD, 0, 1));
float curX = WordToFloat(dataNow, 0);
float newXMax = curX - 10f;
var (ec4, _) = Request(BuildWordWrite(DevD, 11, new[] { ToWord(newXMax) }));
Check("D11(XMax) 쓰기 endcode=0", ec4 == 0, $"endcode=0x{ec4:X4}");
Console.WriteLine($"     XMax={newXMax:F1} mm 로 설정 (현재 X={curX:F1})");

Thread.Sleep(500); // ErrorDetector 평가 대기

var (_, data4) = Request(BuildBitRead(DevM, 100, 2));
bool[] bits4 = UnpackBits(data4, 2);
Check("M100(ErrorLamp)=ON", bits4[0], $"M100={bits4[0]}");
Check("M101(XError)=ON", bits4[1], $"M101={bits4[1]}");

// ── 시나리오 ⑤ 읽기전용/맵밖 쓰기 → 비정상 endcode, 상태 불변 ──
Console.WriteLine("\n⑤ 읽기전용/맵밖 접근 → 0이 아닌 end code");
var (ec5a, _) = Request(BuildWordWrite(DevD, 0, new[] { ToWord(999f) })); // D0 읽기전용
Check("D0(읽기전용) 쓰기 거부", ec5a != 0, $"endcode=0x{ec5a:X4}");
var (ec5b, _) = Request(BuildWordRead(DevD, 9, 1)); // D9 맵 밖
Check("D9(맵 밖) 읽기 거부", ec5b != 0, $"endcode=0x{ec5b:X4}");
var (ec5c, _) = Request(BuildBitRead(DevM, 100, 1, writeAttempt: true)); // M 비트쓰기
Check("M100 비트쓰기 거부", ec5c != 0, $"endcode=0x{ec5c:X4}");

// 상태 불변 확인: D0 다시 읽어 999가 아님
var (_, data5) = Request(BuildWordRead(DevD, 0, 1));
Check("거부 후 상태 불변(D0!=999)", Math.Abs(WordToFloat(data5, 0) - 999f) > 1f,
    $"CurrentX={WordToFloat(data5, 0):F1}");

// ── 시나리오 ⑥ 쪼개진 프레임 부분 수신 ──
Console.WriteLine("\n⑥ 쪼개진 프레임(부분 수신) 정상 파싱");
byte[] split = BuildWordRead(DevD, 0, 2);
stream.Write(split, 0, 5);      // 앞 5바이트만 먼저
stream.Flush();
Thread.Sleep(200);              // 일부러 지연
stream.Write(split, 5, split.Length - 5); // 나머지
stream.Flush();
var (ec6, data6) = ReadResponse();
Check("쪼갠 프레임 응답 endcode=0", ec6 == 0 && data6.Length == 4, $"endcode=0x{ec6:X4}, len={data6.Length}");

Console.WriteLine($"\n=== 결과: PASS {passed} / FAIL {failed} ===");
return failed == 0 ? 0 : 1;


// ───────────────────────── 헬퍼 ─────────────────────────

// 요청 전송 후 응답 (endcode, data) 반환
(ushort, byte[]) Request(byte[] frame)
{
    stream.Write(frame, 0, frame.Length);
    stream.Flush();
    return ReadResponse();
}

(ushort, byte[]) ReadResponse()
{
    byte[] head = ReadExact(9); // D0 00 + echo5 + responseLen2
    int respLen = head[7] | (head[8] << 8);
    byte[] body = ReadExact(respLen); // endcode2 + data
    ushort endcode = (ushort)(body[0] | (body[1] << 8));
    byte[] data = new byte[respLen - 2];
    Array.Copy(body, 2, data, 0, data.Length);
    return (endcode, data);
}

byte[] ReadExact(int count)
{
    byte[] buf = new byte[count];
    int read = 0;
    while (read < count)
    {
        int n = stream.Read(buf, read, count - read);
        if (n == 0) throw new IOException("연결 종료");
        read += n;
    }
    return buf;
}

short ToWord(float v) => (short)Math.Clamp(MathF.Round(v * Scale), short.MinValue, short.MaxValue);

float WordToFloat(byte[] data, int index)
{
    short w = (short)(data[index * 2] | (data[index * 2 + 1] << 8));
    return w / (float)Scale;
}

bool[] UnpackBits(byte[] data, int count)
{
    var result = new bool[count];
    for (int i = 0; i < count; i++)
    {
        byte b = data[i / 2];
        int nibble = (i % 2 == 0) ? (b >> 4) & 0xF : b & 0xF;
        result[i] = nibble != 0;
    }
    return result;
}

// 3E binary 요청 프레임 빌더
byte[] BuildFrame(ushort command, ushort subcommand, byte deviceCode, int deviceNumber, int count, byte[]? writeData)
{
    var body = new List<byte>();
    // monitoring timer
    body.Add(0x10); body.Add(0x00);
    // command (LE)
    body.Add((byte)(command & 0xFF)); body.Add((byte)(command >> 8));
    // subcommand (LE)
    body.Add((byte)(subcommand & 0xFF)); body.Add((byte)(subcommand >> 8));
    // device number (LE, 3바이트)
    body.Add((byte)(deviceNumber & 0xFF));
    body.Add((byte)((deviceNumber >> 8) & 0xFF));
    body.Add((byte)((deviceNumber >> 16) & 0xFF));
    // device code
    body.Add(deviceCode);
    // device count (LE)
    body.Add((byte)(count & 0xFF)); body.Add((byte)(count >> 8));
    // write data
    if (writeData != null) body.AddRange(writeData);

    var frame = new List<byte>();
    frame.Add(0x50); frame.Add(0x00);                 // subheader
    frame.AddRange(new byte[] { 0x00, 0xFF, 0xFF, 0x03, 0x00 }); // network/pc/io/station
    frame.Add((byte)(body.Count & 0xFF)); frame.Add((byte)(body.Count >> 8)); // request data length
    frame.AddRange(body);
    return frame.ToArray();
}

byte[] BuildWordRead(byte deviceCode, int deviceNumber, int count)
    => BuildFrame(CmdRead, SubWord, deviceCode, deviceNumber, count, null);

byte[] BuildWordWrite(byte deviceCode, int deviceNumber, short[] words)
{
    var data = new byte[words.Length * 2];
    for (int i = 0; i < words.Length; i++)
    {
        data[i * 2] = (byte)(words[i] & 0xFF);
        data[i * 2 + 1] = (byte)((words[i] >> 8) & 0xFF);
    }
    return BuildFrame(CmdWrite, SubWord, deviceCode, deviceNumber, words.Length, data);
}

byte[] BuildBitRead(byte deviceCode, int deviceNumber, int count, bool writeAttempt = false)
    => BuildFrame(writeAttempt ? CmdWrite : CmdRead, SubBit, deviceCode, deviceNumber, count,
        writeAttempt ? new byte[] { 0x10 } : null);
