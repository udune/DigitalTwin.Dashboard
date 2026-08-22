using System;
using System.IO;
using System.Linq;
using DigitalTwin.Dashboard.Helpers;

namespace DigitalTwin.Dashboard.Tests
{
    // AppLog는 프로세스 전역 정적 상태를 쓰므로 한 컬렉션(=직렬)으로 묶어 둔다.
    [Collection("AppLog")]
    public class AppLogTests : IDisposable
    {
        private readonly string _dir;

        public AppLogTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "dtlog_" + Guid.NewGuid().ToString("N"));
            AppLog.Shutdown();   // 앞선 테스트가 남긴 설정 제거
        }

        public void Dispose()
        {
            AppLog.Shutdown();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string ReadLog()
        {
            // 파일 핸들을 놓은 뒤 읽는다(싱크가 쥐고 있는 동안에도 공유 읽기는 되지만 확실하게).
            AppLog.Shutdown();
            string file = Directory.GetFiles(_dir, "dashboard-*.log").Single();
            return File.ReadAllText(file);
        }

        [Fact]
        public void Initialize_로그폴더와_파일을_만든다()
        {
            string? resolved = AppLog.Initialize(_dir);

            Assert.Equal(_dir, resolved);
            Assert.True(AppLog.IsInitialized);
            Assert.True(Directory.Exists(_dir));

            AppLog.Info("TEST", "기록 한 줄");

            Assert.Contains("기록 한 줄", ReadLog());
        }

        [Fact]
        public void 레벨과_소스가_줄에_남는다()
        {
            AppLog.Initialize(_dir);

            AppLog.Info("SLMP", "서버 리슨 시작");
            AppLog.Warn("ALARM", "X축 리미트 초과");
            AppLog.Error("IPC", "전송 오류", new InvalidOperationException("파이프 끊김"));

            string text = ReadLog();

            Assert.Contains("[INF] [SLMP] 서버 리슨 시작", text);
            Assert.Contains("[WRN] [ALARM] X축 리미트 초과", text);
            Assert.Contains("[ERR] [IPC] 전송 오류", text);
            // 예외는 메시지 뒤에 스택과 함께 붙는다.
            Assert.Contains("파이프 끊김", text);
        }

        [Fact]
        public void 중괄호가_섞인_메시지도_그대로_남는다()
        {
            AppLog.Initialize(_dir);

            // 알람 메시지·JSON 조각에 '{'가 들어와도 Serilog 템플릿 구멍으로 해석되면 안 된다.
            AppLog.Warn("IPC", "메시지 파싱 오류: {\"type\":\"axis_data\"}");

            Assert.Contains("{\"type\":\"axis_data\"}", ReadLog());
        }

        [Fact]
        public void Initialize를_두_번_불러도_첫_설정을_유지한다()
        {
            string other = _dir + "_other";

            AppLog.Initialize(_dir);
            string? second = AppLog.Initialize(other);

            Assert.Equal(_dir, second);
            Assert.False(Directory.Exists(other));
        }

        [Fact]
        public void 초기화_전_호출은_예외를_던지지_않는다()
        {
            // 테스트가 서비스를 그냥 생성해 쓰는 경로 — 로그는 무음으로 버려진다.
            AppLog.Shutdown();

            AppLog.Info("TEST", "아무 데도 안 남는 줄");
            AppLog.Error("TEST", "이것도", new Exception("x"));

            Assert.False(AppLog.IsInitialized);
        }
    }
}
