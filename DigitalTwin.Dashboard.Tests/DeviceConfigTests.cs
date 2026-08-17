using System.IO;
using DigitalTwin.Dashboard.Models;

namespace DigitalTwin.Dashboard.Tests
{
    // 설정 로드는 MainViewModel 생성자에서 DeviceConfig.Load로 빠져나왔다.
    // 파일이 어떻든 예외를 던지지 않고, 실패는 warning으로만 알리는 것이 계약이다.
    public class DeviceConfigTests
    {
        [Fact]
        public void Load_MissingFile_ReturnsDefaultsWithoutWarning()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dt_missing_{Guid.NewGuid():N}.json");

            var config = DeviceConfig.Load(path, out string? warning);

            Assert.Null(warning);
            Assert.Equal(5007, config.SlmpPort);
            Assert.Equal(4840, config.OpcUaPort);
            Assert.Equal(500f, config.XLimit);
        }

        [Fact]
        public void Load_ReadsPortsFromFile()
        {
            using var file = new TempJson("{ \"SlmpPort\": 15007, \"OpcUaPort\": 14840 }");

            var config = DeviceConfig.Load(file.Path, out string? warning);

            Assert.Null(warning);
            Assert.Equal(15007, config.SlmpPort);
            Assert.Equal(14840, config.OpcUaPort);
            // 파일에 없는 항목은 기본값을 유지한다.
            Assert.Equal(500f, config.XLimit);
        }

        [Fact]
        public void Load_BrokenJson_ReturnsDefaultsWithWarning()
        {
            using var file = new TempJson("{ this is not json");

            var config = DeviceConfig.Load(file.Path, out string? warning);

            Assert.NotNull(warning);
            Assert.Contains("설정 파일 로드 실패", warning);
            Assert.Equal(5007, config.SlmpPort);
        }

        [Fact]
        public void Load_EmptyJson_ReturnsDefaultsWithWarning()
        {
            using var file = new TempJson("null");

            var config = DeviceConfig.Load(file.Path, out string? warning);

            Assert.NotNull(warning);
            Assert.Equal(4840, config.OpcUaPort);
        }

        [Fact]
        public void ShippedAppSettings_ParsesAndKeepsDefaultPorts()
        {
            // 실행 폴더로 복사되는 실제 설정 파일이 깨져 있지 않은지 확인한다.
            var config = DeviceConfig.Load(DeviceConfig.DefaultPath, out string? warning);

            Assert.Null(warning);
            Assert.Equal(5007, config.SlmpPort);
            Assert.Equal(4840, config.OpcUaPort);
        }

        private sealed class TempJson : IDisposable
        {
            public string Path { get; }

            public TempJson(string content)
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), $"dt_config_{Guid.NewGuid():N}.json");
                File.WriteAllText(Path, content);
            }

            public void Dispose()
            {
                try { File.Delete(Path); } catch { }
            }
        }
    }
}
