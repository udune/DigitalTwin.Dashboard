using DigitalTwin.Dashboard.Models;
using DigitalTwin.Dashboard.Services;

namespace DigitalTwin.Dashboard.Tests
{
    // 경계 판정(안/밖)과 같은 경보의 반복 억제를 검증한다.
    public class ErrorDetectorTests
    {
        // 반복 억제에 걸리지 않게 넉넉한 경계값을 가진 기본 설정.
        private static DeviceConfig Config() => new DeviceConfig
        {
            AlarmXMin = -100f,
            AlarmXMax = 100f,
            AlarmYMin = -100f,
            AlarmYMax = 100f,
            AlarmZMin = -60f,
            AlarmZMax = 0f,
            MaxSpeed = 100f,
            AlarmMaxVelocity = 80f, // 도달 가능하게 두어 시작 경고가 끼어들지 않게 한다
        };

        private static (DeviceTable Table, ErrorDetector Detector, List<AlarmData> Alarms) Build(
            DeviceConfig? config = null)
        {
            config ??= Config();
            var table = new DeviceTable(config);
            var detector = new ErrorDetector(table, config);
            var alarms = new List<AlarmData>();
            detector.OnErrorDetected += alarms.Add;
            return (table, detector, alarms);
        }

        // ── 경계선 안 ──

        [Fact]
        public void 경계_안에서는_알람이_없다()
        {
            var (table, detector, alarms) = Build();
            table.SetCurrentAndVelocity(50f, -50f, -10f, 10f, 10f, 10f);

            detector.Evaluate();

            Assert.Empty(alarms);
        }

        [Theory]
        [InlineData(100f)]   // XMax 정확히
        [InlineData(-100f)]  // XMin 정확히
        public void 경계값_자체는_위반이_아니다(float x)
        {
            var (table, detector, alarms) = Build();
            table.SetCurrentAndVelocity(x, 0f, -10f, 0f, 0f, 0f);

            detector.Evaluate();

            Assert.Empty(alarms);
        }

        [Fact]
        public void 경계_안에서는_에러플래그가_내려간다()
        {
            var (table, detector, _) = Build();
            table.SetErrorFlags(true, true, true, true);
            table.SetCurrentAndVelocity(0f, 0f, -10f, 0f, 0f, 0f);

            detector.Evaluate();

            var snap = table.Snapshot();
            Assert.False(snap.ErrorLamp);
            Assert.False(snap.XError);
            Assert.False(snap.YError);
            Assert.False(snap.ZError);
        }

        // ── 경계선 밖 ──

        [Fact]
        public void X축_상한을_넘으면_Error알람과_플래그가_뜬다()
        {
            var (table, detector, alarms) = Build();
            table.SetCurrentAndVelocity(100.1f, 0f, -10f, 0f, 0f, 0f);

            detector.Evaluate();

            var alarm = Assert.Single(alarms);
            Assert.Equal("Error", alarm.Level);
            Assert.Equal("X_AXIS", alarm.Location);
            Assert.Equal("X_LIMIT", alarm.Code);

            var snap = table.Snapshot();
            Assert.True(snap.ErrorLamp);
            Assert.True(snap.XError);
            Assert.False(snap.YError);
        }

        [Fact]
        public void X축_하한을_넘으면_Error알람이_뜬다()
        {
            var (table, detector, alarms) = Build();
            table.SetCurrentAndVelocity(-100.1f, 0f, -10f, 0f, 0f, 0f);

            detector.Evaluate();

            Assert.Single(alarms);
            Assert.True(table.Snapshot().XError);
        }

        [Fact]
        public void Y축_위반은_Y플래그만_올린다()
        {
            var (table, detector, alarms) = Build();
            table.SetCurrentAndVelocity(0f, 150f, -10f, 0f, 0f, 0f);

            detector.Evaluate();

            Assert.Equal("Y_LIMIT", Assert.Single(alarms).Code);

            var snap = table.Snapshot();
            Assert.True(snap.YError);
            Assert.False(snap.XError);
            Assert.False(snap.ZError);
        }

        [Fact]
        public void Z축_상한과_하한_모두_감지된다()
        {
            var (tableHigh, detectorHigh, alarmsHigh) = Build();
            tableHigh.SetCurrentAndVelocity(0f, 0f, 10f, 0f, 0f, 0f); // ZMax=0 초과
            detectorHigh.Evaluate();
            Assert.Equal("Z_LIMIT", Assert.Single(alarmsHigh).Code);

            var (tableLow, detectorLow, alarmsLow) = Build();
            tableLow.SetCurrentAndVelocity(0f, 0f, -70f, 0f, 0f, 0f); // ZMin=-60 미만
            detectorLow.Evaluate();
            Assert.Equal("Z_LIMIT", Assert.Single(alarmsLow).Code);
        }

        [Fact]
        public void 여러축이_동시에_위반하면_각각_알람이_뜬다()
        {
            var (table, detector, alarms) = Build();
            table.SetCurrentAndVelocity(200f, 200f, 50f, 0f, 0f, 0f);

            detector.Evaluate();

            Assert.Equal(3, alarms.Count);
            Assert.Contains(alarms, a => a.Code == "X_LIMIT");
            Assert.Contains(alarms, a => a.Code == "Y_LIMIT");
            Assert.Contains(alarms, a => a.Code == "Z_LIMIT");

            var snap = table.Snapshot();
            Assert.True(snap.XError && snap.YError && snap.ZError && snap.ErrorLamp);
        }

        // ── Z축 안전 높이 ──

        [Fact]
        public void 안전높이_아래에서_XY가_움직이면_경고가_뜬다()
        {
            var (table, detector, alarms) = Build();
            table.SetCurrentAndVelocity(0f, 0f, -40f, 5f, 0f, 0f); // Z < -30, X 이동 중

            detector.Evaluate();

            var alarm = Assert.Single(alarms);
            Assert.Equal("Warning", alarm.Level);
            Assert.Equal("Z_SAFE_HEIGHT", alarm.Code);
        }

        [Fact]
        public void 안전높이_아래라도_XY가_멈춰있으면_경고가_없다()
        {
            var (table, detector, alarms) = Build();
            table.SetCurrentAndVelocity(0f, 0f, -40f, 0.05f, 0.05f, 50f); // XY 정지, Z만 이동

            detector.Evaluate();

            Assert.Empty(alarms);
        }

        // ── 과속 ──

        [Fact]
        public void 임계를_넘는_속도는_축별로_경고가_뜬다()
        {
            var (table, detector, alarms) = Build(); // AlarmMaxVelocity = 80
            table.SetCurrentAndVelocity(0f, 0f, -10f, 95f, -95f, 95f);

            detector.Evaluate();

            Assert.Equal(3, alarms.Count);
            Assert.Contains(alarms, a => a.Code == "X_OVERSPEED");
            Assert.Contains(alarms, a => a.Code == "Y_OVERSPEED");
            Assert.Contains(alarms, a => a.Code == "Z_OVERSPEED");
            Assert.All(alarms, a => Assert.Contains("80.0mm/s", a.Message));
        }

        [Fact]
        public void 임계_이하_속도는_경고가_없다()
        {
            var (table, detector, alarms) = Build();
            table.SetCurrentAndVelocity(0f, 0f, -10f, 79f, -79f, 79f);

            detector.Evaluate();

            Assert.Empty(alarms);
        }

        [Fact]
        public void 과속임계가_최고속도_이상이면_도달불가로_판정한다()
        {
            var config = Config();
            config.MaxSpeed = 100f;
            config.AlarmMaxVelocity = 150f; // 기본값 조합
            var (_, detector, alarms) = Build(config);

            Assert.False(detector.IsOverspeedReachable);

            detector.ValidateConfiguration();

            var alarm = Assert.Single(alarms);
            Assert.Equal("CONFIG_OVERSPEED_UNREACHABLE", alarm.Code);
            Assert.Equal("Warning", alarm.Level);
        }

        [Fact]
        public void 과속임계가_최고속도보다_낮으면_시작경고가_없다()
        {
            var (_, detector, alarms) = Build(); // 100 / 80

            Assert.True(detector.IsOverspeedReachable);

            detector.ValidateConfiguration();

            Assert.Empty(alarms);
        }

        // ── 같은 경보 반복 억제 ──

        [Fact]
        public void 같은_알람은_억제창_안에서_한번만_발생한다()
        {
            var (table, detector, alarms) = Build();
            detector.SetCheckInterval(30.0);
            table.SetCurrentAndVelocity(200f, 0f, -10f, 0f, 0f, 0f);

            for (int i = 0; i < 50; i++)
            {
                detector.Evaluate();
            }

            Assert.Single(alarms);
        }

        [Fact]
        public void 억제창이_지나면_다시_발생한다()
        {
            var (table, detector, alarms) = Build();
            detector.SetCheckInterval(0.1); // 100ms
            table.SetCurrentAndVelocity(200f, 0f, -10f, 0f, 0f, 0f);

            detector.Evaluate();
            Assert.Single(alarms);

            detector.Evaluate(); // 아직 억제창 안
            Assert.Single(alarms);

            Thread.Sleep(150);
            detector.Evaluate();

            Assert.Equal(2, alarms.Count);
        }

        [Fact]
        public void 서로_다른_알람은_서로를_억제하지_않는다()
        {
            var (table, detector, alarms) = Build();
            detector.SetCheckInterval(30.0);

            table.SetCurrentAndVelocity(200f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate();
            Assert.Single(alarms);

            // X는 억제 상태로 두고 Y 위반을 추가
            table.SetCurrentAndVelocity(200f, 200f, -10f, 0f, 0f, 0f);
            detector.Evaluate();

            Assert.Equal(2, alarms.Count);
            Assert.Contains(alarms, a => a.Code == "Y_LIMIT");
        }

        [Fact]
        public void 억제창은_최소_0_1초로_보정된다()
        {
            var (_, detector, _) = Build();

            detector.SetCheckInterval(-5.0);

            Assert.Equal(0.1, detector.GetCheckInterval());
        }

        [Fact]
        public void 억제중에도_에러플래그는_계속_최신값을_유지한다()
        {
            var (table, detector, alarms) = Build();
            detector.SetCheckInterval(30.0);

            table.SetCurrentAndVelocity(200f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate();
            Assert.True(table.Snapshot().XError);

            // 알람은 억제되지만 플래그는 위반이 해소되면 즉시 내려가야 한다.
            table.SetCurrentAndVelocity(0f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate();

            Assert.Single(alarms);
            Assert.False(table.Snapshot().XError);
            Assert.False(table.Snapshot().ErrorLamp);
        }

        [Fact]
        public void 알람_그룹키는_레벨_위치_코드로_만들어진다()
        {
            var (table, detector, alarms) = Build();
            table.SetCurrentAndVelocity(200f, 0f, -10f, 0f, 0f, 0f);

            detector.Evaluate();

            Assert.Equal("Error|X_AXIS|X_LIMIT", Assert.Single(alarms).GetGroupKey());
        }

        // ── 조건 해제 ──
        // 오류 판정의 단일 기준이 대시보드이므로, 조건이 풀렸다는 사실도 여기서만 알릴 수 있다.
        // 이 통보가 없으면 Unity 뷰어는 오류 표시를 영원히 들고 있게 된다.

        [Fact]
        public void 위반이_해소되면_해제가_통보된다()
        {
            var (table, detector, _) = Build();
            var cleared = new List<string>();
            detector.OnErrorCleared += cleared.Add;

            table.SetCurrentAndVelocity(200f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate();
            Assert.Empty(cleared);

            table.SetCurrentAndVelocity(0f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate();

            Assert.Equal("X_LIMIT", Assert.Single(cleared));
        }

        [Fact]
        public void 위반이_계속되는_동안에는_해제가_없다()
        {
            var (table, detector, _) = Build();
            var cleared = new List<string>();
            detector.OnErrorCleared += cleared.Add;

            table.SetCurrentAndVelocity(200f, 0f, -10f, 0f, 0f, 0f);

            for (int i = 0; i < 10; i++)
            {
                detector.Evaluate();
            }

            Assert.Empty(cleared);
        }

        [Fact]
        public void 억제창에_걸려_알람이_안_나가도_해제는_한번_통보된다()
        {
            var (table, detector, alarms) = Build();
            var cleared = new List<string>();
            detector.OnErrorCleared += cleared.Add;
            detector.SetCheckInterval(30.0);

            table.SetCurrentAndVelocity(200f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate();
            detector.Evaluate(); // 억제창 안 — 알람은 안 나가지만 조건은 성립 중
            Assert.Single(alarms);
            Assert.Empty(cleared);

            table.SetCurrentAndVelocity(0f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate();
            detector.Evaluate(); // 이미 해제된 조건을 두 번 통보하지 않는다

            Assert.Equal("X_LIMIT", Assert.Single(cleared));
        }

        [Fact]
        public void 해제된_조건이_재발하면_억제창을_기다리지_않는다()
        {
            var (table, detector, alarms) = Build();
            detector.SetCheckInterval(30.0);

            table.SetCurrentAndVelocity(200f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate();
            Assert.Single(alarms);

            table.SetCurrentAndVelocity(0f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate(); // 해제

            table.SetCurrentAndVelocity(200f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate(); // 새 사건이므로 즉시 다시 알린다

            Assert.Equal(2, alarms.Count);
        }

        [Fact]
        public void 여러_조건은_각각_따로_해제된다()
        {
            var (table, detector, _) = Build();
            var cleared = new List<string>();
            detector.OnErrorCleared += cleared.Add;

            table.SetCurrentAndVelocity(200f, 200f, -10f, 0f, 0f, 0f);
            detector.Evaluate();

            // X만 정상으로 되돌린다
            table.SetCurrentAndVelocity(0f, 200f, -10f, 0f, 0f, 0f);
            detector.Evaluate();
            Assert.Equal("X_LIMIT", Assert.Single(cleared));

            table.SetCurrentAndVelocity(0f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate();

            Assert.Equal(new[] { "X_LIMIT", "Y_LIMIT" }, cleared);
        }

        [Fact]
        public void 설정경고는_조건해제_대상이_아니다()
        {
            var config = Config();
            config.MaxSpeed = 100f;
            config.AlarmMaxVelocity = 150f; // 도달 불가 조합
            var (table, detector, alarms) = Build(config);
            var cleared = new List<string>();
            detector.OnErrorCleared += cleared.Add;

            detector.ValidateConfiguration();
            Assert.Equal("CONFIG_OVERSPEED_UNREACHABLE", Assert.Single(alarms).Code);

            // 일회성 알람이므로 이후 정상 판정에 휩쓸려 해제되면 안 된다.
            table.SetCurrentAndVelocity(0f, 0f, -10f, 0f, 0f, 0f);
            detector.Evaluate();

            Assert.Empty(cleared);
        }

        [Fact]
        public void 런타임에_경계를_좁히면_같은_위치가_위반이_된다()
        {
            var (table, detector, alarms) = Build();
            table.SetCurrentAndVelocity(50f, 0f, -10f, 0f, 0f, 0f);

            detector.Evaluate();
            Assert.Empty(alarms); // XMax=100 이라 정상

            table.SetLimits(-10f, 10f, -100f, 100f, -60f, 0f); // XMax를 현재 위치 아래로
            detector.Evaluate();

            Assert.Equal("X_LIMIT", Assert.Single(alarms).Code);
            Assert.True(table.Snapshot().XError);
        }
    }
}
