using System.Net.Sockets;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

// ───────────────────────────────────────────────────────────────────────────
// OPC UA 서버(STEP 2C) 자기검증용 미니 클라이언트.  ※ 데모/제품 아님.
// UaExpert가 1차 검증, 이 콘솔이 2차/대체 검증(헤드리스 환경용).
//
// 사용법:
//   1) WPF 앱을 실행하고 [시작]을 눌러 PLC 루프를 가동한다.
//   2) dotnet run --project OpcUaTestClient
//
// 점검 시나리오(update.md §6):
//   ① 접속·브라우즈(네임스페이스/노드 확인) + Current/Target 읽기
//   ② TargetX/Y/Z 쓰기 → Current* 변화 확인(축 이동)
//   ③ XMax를 현재 X 아래로 쓰기 → ErrorLamp=true
//   ④ 단일 진실 교차검증: SLMP로 D6 쓰면 OPC TargetX에 반영 / OPC로 쓰면 SLMP D7에서 보임
//   ⑤ 읽기 전용 노드(CurrentX) 쓰기 거부
// ───────────────────────────────────────────────────────────────────────────

const string EndpointUrl = "opc.tcp://localhost:4840";
const string Ns = "http://digitaltwin/pickplace";

int passed = 0, failed = 0;
void Check(string label, bool ok, string detail)
{
    if (ok) { passed++; Console.WriteLine($"  [PASS] {label} — {detail}"); }
    else { failed++; Console.WriteLine($"  [FAIL] {label} — {detail}"); }
}

// ── 클라이언트 설정/세션 ──
var config = new ApplicationConfiguration
{
    ApplicationName = "OpcUaTestClient",
    ApplicationUri = "urn:DigitalTwin:OpcUaTestClient",
    ApplicationType = ApplicationType.Client,
    SecurityConfiguration = new SecurityConfiguration
    {
        ApplicationCertificate = new CertificateIdentifier
        {
            StoreType = CertificateStoreType.Directory,
            StorePath = Path.Combine(AppContext.BaseDirectory, "pki", "own"),
            SubjectName = "CN=OpcUaTestClient, O=DigitalTwin, DC=localhost"
        },
        TrustedPeerCertificates = new CertificateTrustList
        { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(AppContext.BaseDirectory, "pki", "trusted") },
        TrustedIssuerCertificates = new CertificateTrustList
        { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(AppContext.BaseDirectory, "pki", "issuer") },
        RejectedCertificateStore = new CertificateTrustList
        { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(AppContext.BaseDirectory, "pki", "rejected") },
        AutoAcceptUntrustedCertificates = true,
        AddAppCertToTrustedStore = true
    },
    TransportConfigurations = new TransportConfigurationCollection(),
    TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
    ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
    TraceConfiguration = new TraceConfiguration()
};
await config.Validate(ApplicationType.Client);
config.CertificateValidator.CertificateValidation += (s, e) => { e.Accept = true; };

var appInstance = new ApplicationInstance
{
    ApplicationName = "OpcUaTestClient",
    ApplicationType = ApplicationType.Client,
    ApplicationConfiguration = config
};
await appInstance.CheckApplicationInstanceCertificatesAsync(true, null);

Session session;
try
{
    var endpointDescription = CoreClientUtils.SelectEndpoint(config, EndpointUrl, useSecurity: false);
    var endpointConfiguration = EndpointConfiguration.Create(config);
    var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

    session = await Session.Create(config, endpoint, false, "OpcUaTestClient",
        60000, new UserIdentity(new AnonymousIdentityToken()), null);
}
catch (Exception ex)
{
    Console.WriteLine($"OPC UA 서버({EndpointUrl}) 접속 실패: {ex.Message}");
    Console.WriteLine("WPF 앱을 먼저 실행하고 [시작]을 눌렀는지 확인하세요.");
    return 1;
}

Console.WriteLine($"OPC UA 서버 {EndpointUrl} 접속됨\n");

int nsIndex = session.NamespaceUris.GetIndex(Ns);
NodeId N(string name) => new NodeId(name, (ushort)nsIndex);

double ReadD(string name) => Convert.ToDouble(session.ReadValue(N(name)).Value);
bool ReadB(string name) => Convert.ToBoolean(session.ReadValue(N(name)).Value);

StatusCode WriteD(string name, double v)
{
    var toWrite = new WriteValueCollection
    {
        new WriteValue
        {
            NodeId = N(name),
            AttributeId = Attributes.Value,
            Value = new DataValue(new Variant(v))
        }
    };
    session.Write(null, toWrite, out StatusCodeCollection results, out _);
    return results[0];
}

// ── ① 접속·브라우즈·읽기 ──
Console.WriteLine("① 네임스페이스/노드 확인 + Current/Target 읽기");
Check("네임스페이스 등록됨", nsIndex > 0, $"ns='{Ns}' index={nsIndex}");
double cx0 = ReadD("CurrentX"), cy0 = ReadD("CurrentY"), cz0 = ReadD("CurrentZ");
Console.WriteLine($"     Current X={cx0:F1} Y={cy0:F1} Z={cz0:F1}");
Console.WriteLine($"     Target  X={ReadD("TargetX"):F1} Y={ReadD("TargetY"):F1} Z={ReadD("TargetZ"):F1}");

// ── ② Target 쓰기 → Current 변화 ──
Console.WriteLine("\n② TargetX/Y/Z 쓰기 → Current 변화(축 이동)");
Check("TargetX 쓰기 Good", StatusCode.IsGood(WriteD("TargetX", 40)), "");
Check("TargetY 쓰기 Good", StatusCode.IsGood(WriteD("TargetY", -25)), "");
Check("TargetZ 쓰기 Good", StatusCode.IsGood(WriteD("TargetZ", -15)), "");
await Task.Delay(2000); // PLC 보간 이동 대기
double cx1 = ReadD("CurrentX"), cy1 = ReadD("CurrentY"), cz1 = ReadD("CurrentZ");
Console.WriteLine($"     이동 후 Current X={cx1:F1} Y={cy1:F1} Z={cz1:F1}");
Check("Current가 Target 방향으로 변함",
    Math.Abs(cx1 - cx0) > 0.05 || Math.Abs(cy1 - cy0) > 0.05 || Math.Abs(cz1 - cz0) > 0.05,
    $"ΔX={cx1 - cx0:F1} ΔY={cy1 - cy0:F1} ΔZ={cz1 - cz0:F1}");

// ── ③ XMax를 현재 X 아래로 → ErrorLamp=true ──
Console.WriteLine("\n③ XMax를 현재 X 아래로 쓰기 → ErrorLamp=true");
double curX = ReadD("CurrentX");
Check("XMax 쓰기 Good", StatusCode.IsGood(WriteD("XMax", curX - 10)), $"XMax={curX - 10:F1}");
await Task.Delay(500);
Check("ErrorLamp=true", ReadB("ErrorLamp"), $"ErrorLamp={ReadB("ErrorLamp")}, XError={ReadB("XError")}");
// 한계 복구(이후 검증 영향 최소화)
WriteD("XMax", 125.9);

// ── ④ 단일 진실 교차검증 (SLMP ↔ OPC UA) ──
Console.WriteLine("\n④ 단일 진실 교차검증 (SLMP ↔ OPC UA)");
// (a) SLMP로 D6(TargetX)=12.3 쓰기 → OPC TargetX에서 보임
SlmpWriteWord(6, (short)Math.Round(12.3 * 10));
await Task.Delay(300);
double opcTx = ReadD("TargetX");
Check("SLMP D6 쓰기가 OPC TargetX에 반영", Math.Abs(opcTx - 12.3) < 0.2, $"OPC TargetX={opcTx:F1} (기대 12.3)");
// (b) OPC로 TargetY=-7.7 쓰기 → SLMP D7에서 보임
WriteD("TargetY", -7.7);
await Task.Delay(300);
double slmpD7 = SlmpReadWord(7) / 10.0;
Check("OPC TargetY 쓰기가 SLMP D7에 반영", Math.Abs(slmpD7 - (-7.7)) < 0.2, $"SLMP D7={slmpD7:F1} (기대 -7.7)");

// ── ⑤ 읽기 전용 노드 쓰기 거부 ──
Console.WriteLine("\n⑤ 읽기 전용 노드(CurrentX) 쓰기 거부");
StatusCode roResult = WriteD("CurrentX", 999);
Check("CurrentX 쓰기 거부(Bad)", StatusCode.IsBad(roResult), $"status={roResult}");

session.Close();
session.Dispose();

Console.WriteLine($"\n=== 결과: PASS {passed} / FAIL {failed} ===");
return failed == 0 ? 0 : 1;


// ───────────────────────── SLMP 3E 최소 헬퍼 (교차검증용) ─────────────────────────
short SlmpReadWord(int dNumber)
{
    using var c = new TcpClient();
    c.Connect("127.0.0.1", 5007);
    var st = c.GetStream();
    byte[] req = SlmpFrame(0x0401, 0x0000, 0xA8, dNumber, 1, null);
    st.Write(req, 0, req.Length);
    var (ec, data) = SlmpReadResponse(st);
    if (ec != 0 || data.Length < 2) throw new Exception($"SLMP read endcode=0x{ec:X4}");
    return (short)(data[0] | (data[1] << 8));
}

void SlmpWriteWord(int dNumber, short word)
{
    using var c = new TcpClient();
    c.Connect("127.0.0.1", 5007);
    var st = c.GetStream();
    byte[] payload = { (byte)(word & 0xFF), (byte)((word >> 8) & 0xFF) };
    byte[] req = SlmpFrame(0x1401, 0x0000, 0xA8, dNumber, 1, payload);
    st.Write(req, 0, req.Length);
    var (ec, _) = SlmpReadResponse(st);
    if (ec != 0) throw new Exception($"SLMP write endcode=0x{ec:X4}");
}

(ushort, byte[]) SlmpReadResponse(NetworkStream st)
{
    byte[] head = SlmpReadExact(st, 9);
    int respLen = head[7] | (head[8] << 8);
    byte[] body = SlmpReadExact(st, respLen);
    ushort endcode = (ushort)(body[0] | (body[1] << 8));
    byte[] data = new byte[respLen - 2];
    Array.Copy(body, 2, data, 0, data.Length);
    return (endcode, data);
}

byte[] SlmpReadExact(NetworkStream st, int count)
{
    byte[] buf = new byte[count];
    int read = 0;
    while (read < count)
    {
        int n = st.Read(buf, read, count - read);
        if (n == 0) throw new IOException("연결 종료");
        read += n;
    }
    return buf;
}

byte[] SlmpFrame(ushort command, ushort subcommand, byte deviceCode, int deviceNumber, int count, byte[]? writeData)
{
    var body = new List<byte>
    {
        0x10, 0x00, // monitoring timer
        (byte)(command & 0xFF), (byte)(command >> 8),
        (byte)(subcommand & 0xFF), (byte)(subcommand >> 8),
        (byte)(deviceNumber & 0xFF), (byte)((deviceNumber >> 8) & 0xFF), (byte)((deviceNumber >> 16) & 0xFF),
        deviceCode,
        (byte)(count & 0xFF), (byte)(count >> 8)
    };
    if (writeData != null) body.AddRange(writeData);

    var frame = new List<byte> { 0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00,
        (byte)(body.Count & 0xFF), (byte)(body.Count >> 8) };
    frame.AddRange(body);
    return frame.ToArray();
}
