using System.Net;
using System.Net.Sockets;
using DigitalTwin.Dashboard.Models;
using DigitalTwin.Dashboard.Tests.Slmp;
using DigitalTwin.Dashboard.ViewModels;

namespace DigitalTwin.Dashboard.Tests
{
    // MainViewModel 생성자가 부수효과 없이 끝나는지 확인한다.
    // 종전에는 생성만으로 appsettings.json을 읽고 SLMP·OPC UA 포트를 잡았기 때문에
    // 뷰모델을 테스트에서 만들 수 없었다. 부수효과는 Initialize()로 옮겼다.
    public class MainViewModelTests
    {
        [Fact]
        public void Constructor_DoesNotOpenServerPorts()
        {
            int slmpPort = FindFreePort();
            int opcUaPort = FindFreePort();
            var config = new DeviceConfig { SlmpPort = slmpPort, OpcUaPort = opcUaPort };

            using var vm = new MainViewModel(config);

            // Initialize()를 부르지 않았으므로 두 포트는 여전히 비어 있어야 한다.
            AssertPortFree(slmpPort);
            AssertPortFree(opcUaPort);
        }

        [Fact]
        public void PortProbe_FailsWhenServerIsListening()
        {
            // 위 테스트의 검사가 헛돌지 않음을 보장한다 — 실제로 리슨 중이면 바인드는 실패한다.
            using var host = new SlmpTestHost();

            Assert.ThrowsAny<SocketException>(() => AssertPortFree(host.Port));
        }

        [Fact]
        public void Constructor_SurfacesConfigWarningInStatus()
        {
            using var vm = new MainViewModel(new DeviceConfig(), "설정 파일 로드 실패: 테스트");

            Assert.Equal("설정 파일 로드 실패: 테스트", vm.StatusMessage);
        }

        [Fact]
        public void Constructor_WithoutWarning_KeepsDefaultStatus()
        {
            using var vm = new MainViewModel(new DeviceConfig());

            Assert.Equal("준비", vm.StatusMessage);
        }

        private static void AssertPortFree(int port)
        {
            var probe = new TcpListener(IPAddress.Any, port);
            probe.Start();
            probe.Stop();
        }

        private static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
