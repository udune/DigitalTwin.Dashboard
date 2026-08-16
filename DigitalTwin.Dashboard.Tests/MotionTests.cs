using DigitalTwin.Dashboard.Models;
using DigitalTwin.Dashboard.Services;

namespace DigitalTwin.Dashboard.Tests
{
    // 위치 계산 검증 — 핵심은 "목표를 지나치지 않는가"(오버슈트 금지).
    public class MotionTests
    {
        [Theory]
        [InlineData(0f, 10f, 3f, 3f)]      // 한 걸음 전진
        [InlineData(0f, -10f, 3f, -3f)]    // 음의 방향
        [InlineData(10f, 0f, 3f, 7f)]      // 되돌아오기
        [InlineData(-5f, 5f, 2f, -3f)]     // 부호를 가로질러
        public void 남은거리가_한걸음보다_크면_한걸음만_간다(
            float current, float target, float maxDelta, float expected)
        {
            Assert.Equal(expected, VirtualPLC.MoveTowards(current, target, maxDelta), 4);
        }

        [Theory]
        [InlineData(0f, 10f, 100f)]        // 한 걸음이 남은거리보다 큼
        [InlineData(0f, 10f, 10f)]         // 정확히 남은거리만큼
        [InlineData(0f, -10f, 100f)]
        [InlineData(-7.5f, 3.25f, 1000f)]
        public void 한걸음이_남은거리_이상이면_목표에_정확히_멈춘다(
            float current, float target, float maxDelta)
        {
            Assert.Equal(target, VirtualPLC.MoveTowards(current, target, maxDelta), 4);
        }

        [Fact]
        public void 이미_목표에_있으면_움직이지_않는다()
        {
            Assert.Equal(5f, VirtualPLC.MoveTowards(5f, 5f, 1f), 4);
            Assert.Equal(5f, VirtualPLC.MoveTowards(5f, 5f, 0f), 4);
        }

        [Fact]
        public void 반복_적용해도_목표를_지나치지_않는다()
        {
            const float target = 10f;
            const float step = 3f;
            float pos = 0f;

            for (int i = 0; i < 100; i++)
            {
                pos = VirtualPLC.MoveTowards(pos, target, step);
                Assert.True(pos <= target, $"{i}번째 반복에서 목표를 지나쳤습니다: {pos}");
            }

            Assert.Equal(target, pos, 4);
        }

        [Fact]
        public void 음의_방향으로도_목표를_지나치지_않는다()
        {
            const float target = -10f;
            const float step = 3f;
            float pos = 0f;

            for (int i = 0; i < 100; i++)
            {
                pos = VirtualPLC.MoveTowards(pos, target, step);
                Assert.True(pos >= target, $"{i}번째 반복에서 목표를 지나쳤습니다: {pos}");
            }

            Assert.Equal(target, pos, 4);
        }

        [Fact]
        public void 목표에_도달하면_그_자리에_머문다()
        {
            float pos = VirtualPLC.MoveTowards(0f, 5f, 100f);
            Assert.Equal(5f, pos, 4);

            for (int i = 0; i < 10; i++)
            {
                pos = VirtualPLC.MoveTowards(pos, 5f, 100f);
                Assert.Equal(5f, pos, 4);
            }
        }

        [Fact]
        public void 걸음이_0이면_제자리에_머문다()
        {
            Assert.Equal(0f, VirtualPLC.MoveTowards(0f, 10f, 0f), 4);
        }

        // ── 설정 주입 ──

        [Fact]
        public void MaxSpeed는_설정값을_따른다()
        {
            var config = new DeviceConfig { MaxSpeed = 250f };
            var plc = new VirtualPLC(new DeviceTable(config), new ErrorDetector(new DeviceTable(config), config), config);

            Assert.Equal(250f, plc.MaxSpeed);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-50f)]
        public void MaxSpeed가_0이하면_기본값_100으로_되돌린다(float configured)
        {
            var config = new DeviceConfig { MaxSpeed = configured };
            var plc = new VirtualPLC(new DeviceTable(config), new ErrorDetector(new DeviceTable(config), config), config);

            Assert.Equal(100f, plc.MaxSpeed);
        }

        // ── 루프 통합: 실제로 명령한 속도만큼 움직이는가 ──

        // 벽시계로 실제 이동 속도를 재는 구간 길이. 루프 한 회차(~15.5ms)의 양자화 오차가
        // 이 창 안에서 6mm/s 수준으로 희석되므로, 정상(~100)과 고정 dt 버그(~64)가 확실히 갈린다.
        private const double SpeedWindowSeconds = 0.25;

        [Fact]
        public async Task 루프는_명령한_속도로_움직이고_목표에서_멈춘다()
        {
            // 고정 dt(1/100초)를 가정하면 실제 루프는 ~64Hz라 이동이 1.5배 느려진다.
            // 실측 경과 시간을 쓰는지 벽시계로 확인한다.
            //
            // 총 소요 시간으로 재면 안 된다. 루프 스레드가 늦게 깨어나면(부하가 걸린 CI 러너)
            // dt 상한(100ms)에 걸려 이동이 벽시계보다 뒤처지는데, 이건 고정 dt 버그와 방향이
            // 같아서 둘을 구분할 수 없다. 그래서 '가장 잘 돈 구간의 속도'를 본다.
            var config = new DeviceConfig { MaxSpeed = 100f, AlarmMaxVelocity = 1000f };
            var table = new DeviceTable(config);
            var detector = new ErrorDetector(table, config);
            var plc = new VirtualPLC(table, detector, config);

            await plc.Start();
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                table.SetTarget(100f, 0f, 0f); // 100mm @ 100mm/s = 이론 1.00초

                var samples = new List<(double Time, float X)>();
                bool reached = false;

                while (clock.Elapsed.TotalSeconds < 5.0)
                {
                    float x = table.Snapshot().CurrentX;
                    samples.Add((clock.Elapsed.TotalSeconds, x));

                    Assert.True(x <= 100f + 0.001f, $"목표를 지나쳤습니다: {x}");

                    if (Math.Abs(x - 100f) < 0.001f)
                    {
                        reached = true;
                        break;
                    }
                    await Task.Delay(5);
                }

                Assert.True(reached, $"5초 안에 목표에 도달하지 못했습니다: {table.Snapshot().CurrentX}mm");
                Assert.Equal(100f, table.Snapshot().CurrentX, 2);

                // 이론 100mm/s. 고정 dt였다면 어느 구간을 봐도 ~64mm/s를 넘지 못한다.
                Assert.InRange(PeakSpeed(samples), 85.0, 115.0);
            }
            finally
            {
                plc.Stop();
            }
        }

        // SpeedWindowSeconds 이상 떨어진 두 표본으로 잰 구간 속도 중 최댓값.
        // 스레드가 굶은 구간은 느리게 나오지만, 정상적으로 돈 구간이 하나라도 있으면 잡힌다.
        private static double PeakSpeed(List<(double Time, float X)> samples)
        {
            double peak = 0.0;

            for (int i = 0; i < samples.Count; i++)
            {
                for (int j = i + 1; j < samples.Count; j++)
                {
                    double span = samples[j].Time - samples[i].Time;
                    if (span < SpeedWindowSeconds)
                    {
                        continue;
                    }

                    peak = Math.Max(peak, (samples[j].X - samples[i].X) / span);
                }
            }

            return peak;
        }

        [Fact]
        public async Task 루프는_물리적_이동한계를_넘어서_가지_않는다()
        {
            var config = new DeviceConfig { XLimit = 20f, MaxSpeed = 500f, AlarmMaxVelocity = 10000f };
            var table = new DeviceTable(config);
            var detector = new ErrorDetector(table, config);
            var plc = new VirtualPLC(table, detector, config);

            await plc.Start();
            try
            {
                table.SetTarget(9999f, 0f, 0f); // 한계를 한참 넘는 목표

                // 고정 대기 대신 도달할 때까지 기다린다. 500mm/s면 20mm는 40ms거리지만,
                // 부하가 걸린 러너에서는 루프가 그만큼 늦게 깨어날 수 있다.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < 5.0
                       && Math.Abs(table.Snapshot().CurrentX - 20f) > 0.001f)
                {
                    Assert.True(table.Snapshot().CurrentX <= 20f + 0.001f,
                        $"이동 한계를 넘었습니다: {table.Snapshot().CurrentX}");

                    await Task.Delay(5);
                }

                Assert.Equal(20f, table.Snapshot().CurrentX, 2);
            }
            finally
            {
                plc.Stop();
            }
        }
    }
}
