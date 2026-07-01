using System.IO;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace DigitalTwin.Dashboard.Services
{
    // OPC UA 서버 어댑터 (STEP 2C). 북향 게이트웨이.
    // 외부 OPC UA 클라이언트(UaExpert 등)가 DeviceTable의 위치/속도/타겟/한계/에러를
    // 네이티브 OPC UA 노드(Double/Boolean, mm 그대로 — ×10 변환 없음)로 읽고 쓴다.
    //
    // 경계(STEP 2C):
    //  - DeviceTable 내부 표현·100Hz 루프·UI·Named Pipe·SlmpServer는 건드리지 않는 순수 추가 어댑터.
    //  - 읽기 = Snapshot(), 쓰기 = SetTarget/SetLimits 뿐. DeviceTable이 thread-safe하므로 새 lock 불필요.
    //  - SLMP와 동일 접근 모델: Current/Velocity/Error = R, Target/Limits = R/W.
    internal class OpcUaServer
    {
        private const string ApplicationUri = "urn:DigitalTwin:PickPlace:Server";
        private const string ApplicationName = "DigitalTwin PickPlace Server";

        private readonly DeviceTable _deviceTable;
        private readonly int _port;

        private PickPlaceServer? _server;
        private ApplicationInstance? _application;
        private volatile bool _isRunning;

        public event Action<string>? OnError;

        public bool IsRunning => _isRunning;

        public OpcUaServer(DeviceTable deviceTable, int port = 4840)
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

            // 인증서 생성·서버 기동은 시간이 걸릴 수 있어 UI 스레드를 막지 않도록 백그라운드에서 수행.
            _ = Task.Run(async () =>
            {
                try
                {
                    ApplicationConfiguration config = BuildConfiguration();
                    await config.Validate(ApplicationType.Server);

                    // 개발용: 신뢰되지 않은 인증서 자동 수락 (운영용 아님).
                    config.CertificateValidator.CertificateValidation += (sender, e) =>
                    {
                        e.Accept = true;
                    };

                    _application = new ApplicationInstance
                    {
                        ApplicationName = ApplicationName,
                        ApplicationType = ApplicationType.Server,
                        ApplicationConfiguration = config
                    };

                    // 자체 서명 인증서 보장(최초 실행 시 생성).
                    await _application.CheckApplicationInstanceCertificatesAsync(true, null);

                    _server = new PickPlaceServer(_deviceTable);
                    await _application.StartAsync(_server);

                    Console.WriteLine($"OPC UA 서버 리슨 시작: opc.tcp://localhost:{_port}");
                }
                catch (Exception e)
                {
                    _isRunning = false;
                    OnError?.Invoke($"OPC UA Start 오류: {e.Message}");
                }
            });
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;

            try
            {
                _application?.Stop();
            }
            catch
            {
                // 종료 중 예외는 무시
            }
            finally
            {
                _server = null;
                _application = null;
            }

            Console.WriteLine("OPC UA 서버 정지");
        }

        private ApplicationConfiguration BuildConfiguration()
        {
            string pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki");

            return new ApplicationConfiguration
            {
                ApplicationName = ApplicationName,
                ApplicationUri = ApplicationUri,
                ApplicationType = ApplicationType.Server,

                ServerConfiguration = new ServerConfiguration
                {
                    BaseAddresses = { $"opc.tcp://localhost:{_port}" },
                    SecurityPolicies = new ServerSecurityPolicyCollection
                    {
                        // 데모 한정: 보안 None 엔드포인트(UaExpert가 바로 붙게). 운영용 아님.
                        new ServerSecurityPolicy
                        {
                            SecurityMode = MessageSecurityMode.None,
                            SecurityPolicyUri = SecurityPolicies.None
                        }
                    },
                    UserTokenPolicies = new UserTokenPolicyCollection
                    {
                        new UserTokenPolicy(UserTokenType.Anonymous)
                    },
                    MinRequestThreadCount = 5,
                    MaxRequestThreadCount = 100,
                    MaxQueuedRequestCount = 200
                },

                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(pkiRoot, "own"),
                        SubjectName = $"CN={ApplicationName}, O=DigitalTwin, DC=localhost"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(pkiRoot, "trusted")
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(pkiRoot, "issuer")
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(pkiRoot, "rejected")
                    },
                    // 데모 한정 — 운영용 아님.
                    AutoAcceptUntrustedCertificates = true,
                    AddAppCertToTrustedStore = true
                },

                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                TraceConfiguration = new TraceConfiguration()
            };
        }
    }

    // StandardServer 서브클래스: 커스텀 NodeManager만 등록한다.
    internal class PickPlaceServer : StandardServer
    {
        private readonly DeviceTable _deviceTable;

        public PickPlaceServer(DeviceTable deviceTable)
        {
            _deviceTable = deviceTable;
        }

        protected override MasterNodeManager CreateMasterNodeManager(
            IServerInternal server, ApplicationConfiguration configuration)
        {
            var nodeManagers = new INodeManager[]
            {
                new PickPlaceNodeManager(server, configuration, _deviceTable)
            };

            return new MasterNodeManager(server, configuration, null, nodeManagers);
        }
    }

    // PickPlace 폴더·변수를 생성하고, ~30Hz로 DeviceTable Snapshot을 노드에 푸시한다.
    // 쓰기 가능 노드(Target/Limits)는 OnWriteValue 콜백으로 SetTarget/SetLimits에 연결.
    internal class PickPlaceNodeManager : CustomNodeManager2
    {
        private const string Namespace = "http://digitaltwin/pickplace";

        private readonly DeviceTable _deviceTable;
        private readonly Dictionary<string, BaseDataVariableState> _vars = new();
        private Timer? _pushTimer;

        public PickPlaceNodeManager(
            IServerInternal server, ApplicationConfiguration configuration, DeviceTable deviceTable)
            : base(server, configuration, Namespace)
        {
            _deviceTable = deviceTable;
            SystemContext.NodeIdFactory = this;
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }

                FolderState folder = CreateFolder(null, "PickPlace");
                folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));

                // 읽기 전용 (Current/Velocity/Error)
                CreateVariable(folder, "CurrentX", DataTypeIds.Double, writable: false);
                CreateVariable(folder, "CurrentY", DataTypeIds.Double, writable: false);
                CreateVariable(folder, "CurrentZ", DataTypeIds.Double, writable: false);
                CreateVariable(folder, "VelocityX", DataTypeIds.Double, writable: false);
                CreateVariable(folder, "VelocityY", DataTypeIds.Double, writable: false);
                CreateVariable(folder, "VelocityZ", DataTypeIds.Double, writable: false);

                // 읽기/쓰기 (Target → SetTarget)
                CreateVariable(folder, "TargetX", DataTypeIds.Double, writable: true);
                CreateVariable(folder, "TargetY", DataTypeIds.Double, writable: true);
                CreateVariable(folder, "TargetZ", DataTypeIds.Double, writable: true);

                // 읽기/쓰기 (Limits → SetLimits)
                CreateVariable(folder, "XMin", DataTypeIds.Double, writable: true);
                CreateVariable(folder, "XMax", DataTypeIds.Double, writable: true);
                CreateVariable(folder, "YMin", DataTypeIds.Double, writable: true);
                CreateVariable(folder, "YMax", DataTypeIds.Double, writable: true);
                CreateVariable(folder, "ZMin", DataTypeIds.Double, writable: true);
                CreateVariable(folder, "ZMax", DataTypeIds.Double, writable: true);

                // 읽기 전용 (Error 비트)
                CreateVariable(folder, "ErrorLamp", DataTypeIds.Boolean, writable: false);
                CreateVariable(folder, "XError", DataTypeIds.Boolean, writable: false);
                CreateVariable(folder, "YError", DataTypeIds.Boolean, writable: false);
                CreateVariable(folder, "ZError", DataTypeIds.Boolean, writable: false);

                AddPredefinedNode(SystemContext, folder);
            }

            // SLMP·UI와 동일 원칙: 100Hz 직접 연동 금지. ~30Hz pull/throttle 푸시.
            _pushTimer = new Timer(PushSnapshot, null, 0, 33);
        }

        private void PushSnapshot(object? state)
        {
            try
            {
                var s = _deviceTable.Snapshot();
                lock (Lock)
                {
                    SetValue("CurrentX", s.CurrentX);
                    SetValue("CurrentY", s.CurrentY);
                    SetValue("CurrentZ", s.CurrentZ);
                    SetValue("VelocityX", s.VelocityX);
                    SetValue("VelocityY", s.VelocityY);
                    SetValue("VelocityZ", s.VelocityZ);
                    SetValue("TargetX", s.TargetX);
                    SetValue("TargetY", s.TargetY);
                    SetValue("TargetZ", s.TargetZ);
                    SetValue("XMin", s.XMin);
                    SetValue("XMax", s.XMax);
                    SetValue("YMin", s.YMin);
                    SetValue("YMax", s.YMax);
                    SetValue("ZMin", s.ZMin);
                    SetValue("ZMax", s.ZMax);
                    SetBool("ErrorLamp", s.ErrorLamp);
                    SetBool("XError", s.XError);
                    SetBool("YError", s.YError);
                    SetBool("ZError", s.ZError);
                }
            }
            catch
            {
                // 푸시 중 일시적 예외는 무시(다음 틱에 복구)
            }
        }

        private void SetValue(string name, float v)
        {
            if (_vars.TryGetValue(name, out var node))
            {
                node.Value = (double)v;
                node.Timestamp = DateTime.UtcNow;
                node.ClearChangeMasks(SystemContext, false);
            }
        }

        private void SetBool(string name, bool v)
        {
            if (_vars.TryGetValue(name, out var node))
            {
                node.Value = v;
                node.Timestamp = DateTime.UtcNow;
                node.ClearChangeMasks(SystemContext, false);
            }
        }

        // 쓰기 콜백: 들어온 값을 DeviceTable에 반영. 나머지 축·한계는 현재 Snapshot 값 유지(부분 쓰기 안전).
        private ServiceResult OnWriteVariable(
            ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding,
            ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            try
            {
                double v = Convert.ToDouble(value);
                ApplyWrite(node.SymbolicName, (float)v);
            }
            catch (Exception)
            {
                return StatusCodes.BadTypeMismatch;
            }

            return ServiceResult.Good;
        }

        private void ApplyWrite(string name, float v)
        {
            var s = _deviceTable.Snapshot();
            switch (name)
            {
                case "TargetX": _deviceTable.SetTarget(v, s.TargetY, s.TargetZ); break;
                case "TargetY": _deviceTable.SetTarget(s.TargetX, v, s.TargetZ); break;
                case "TargetZ": _deviceTable.SetTarget(s.TargetX, s.TargetY, v); break;
                case "XMin": _deviceTable.SetLimits(v, s.XMax, s.YMin, s.YMax, s.ZMin, s.ZMax); break;
                case "XMax": _deviceTable.SetLimits(s.XMin, v, s.YMin, s.YMax, s.ZMin, s.ZMax); break;
                case "YMin": _deviceTable.SetLimits(s.XMin, s.XMax, v, s.YMax, s.ZMin, s.ZMax); break;
                case "YMax": _deviceTable.SetLimits(s.XMin, s.XMax, s.YMin, v, s.ZMin, s.ZMax); break;
                case "ZMin": _deviceTable.SetLimits(s.XMin, s.XMax, s.YMin, s.YMax, v, s.ZMax); break;
                case "ZMax": _deviceTable.SetLimits(s.XMin, s.XMax, s.YMin, s.YMax, s.ZMin, v); break;
            }
        }

        private FolderState CreateFolder(NodeState? parent, string name)
        {
            var folder = new FolderState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId(name, NamespaceIndex),
                BrowseName = new QualifiedName(name, NamespaceIndex),
                DisplayName = name,
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                EventNotifier = EventNotifiers.None
            };

            parent?.AddChild(folder);
            return folder;
        }

        private BaseDataVariableState CreateVariable(NodeState parent, string name, NodeId dataType, bool writable)
        {
            byte access = writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead;

            var variable = new BaseDataVariableState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                NodeId = new NodeId(name, NamespaceIndex),
                BrowseName = new QualifiedName(name, NamespaceIndex),
                DisplayName = name,
                DataType = dataType,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = access,
                UserAccessLevel = access,
                Historizing = false,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow,
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None
            };

            variable.Value = dataType == DataTypeIds.Boolean ? false : (object)0.0;

            if (writable)
            {
                variable.OnWriteValue = OnWriteVariable;
            }

            parent.AddChild(variable);
            _vars[name] = variable;
            return variable;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pushTimer?.Dispose();
                _pushTimer = null;
            }
            base.Dispose(disposing);
        }
    }
}
