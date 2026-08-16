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

        [Fact]
        public async Task 루프는_명령한_속도로_움직이고_목표에서_멈춘다()
        {
            // 고정 dt(1/100초)를 가정하면 실제 루프는 ~64Hz라 이동이 1.5배 느려진다.
            // 실측 경과 시간을 쓰는지 확인하는 테스트다.
            //
            // 계측을 테스트 스레드에서 하면 안 된다. 총 소요 시간으로 재면 루프가 늦게 깨어난
            // 만큼(부하 걸린 CI 러너) 시간이 늘어 고정 dt 버그와 구분되지 않고, 표본을 찍어
            // 재면 위치를 읽은 시점과 시각을 찍은 시점이 벌어져 속도가 되레 부풀려진다.
            // 그래서 위치를 계산한 자리에서 루프가 직접 찍어 보내는 (Timestamp, X)만 쓴다.
            var config = new DeviceConfig { MaxSpeed = 100f, AlarmMaxVelocity = 1000f };
            var table = new DeviceTable(config);
            var detector = new ErrorDetector(table, config);
            var plc = new VirtualPLC(table, detector, config);

            var ticks = new System.Collections.Concurrent.ConcurrentQueue<(DateTime Time, float X)>();
            plc.OnDataUpdated += data => ticks.Enqueue((data.Timestamp, data.X));

            await plc.Start();
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                table.SetTarget(100f, 0f, 0f); // 100mm @ 100mm/s = 이론 1.00초

                bool reached = false;

                while (clock.Elapsed.TotalSeconds < 5.0)
                {
                    float x = table.Snapshot().CurrentX;

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

                // 이론 100mm/s. 고정 dt였다면 회차마다 ~64mm/s가 나온다.
                Assert.InRange(MedianTickSpeed(ticks.ToArray()), 85.0, 115.0);
            }
            finally
            {
                plc.Stop();
            }
        }

        // 루프 회차 사이의 이동 속도들 중 중앙값.
        // 스레드가 굶어 dt 상한에 걸린 회차는 느리게, 그 직후 회차는 빠르게 나오는데
        // 둘 다 소수라서 중앙값은 흔들리지 않는다. 평균이나 최댓값은 이 양쪽에 끌려간다.
        private static double MedianTickSpeed(IReadOnlyList<(DateTime Time, float X)> ticks)
        {
            var speeds = new List<double>();

            for (int i = 1; i < ticks.Count; i++)
            {
                double seconds = (ticks[i].Time - ticks[i - 1].Time).TotalSeconds;
                double moved = ticks[i].X - ticks[i - 1].X;

                if (seconds <= 0 || moved <= 0)
                {
                    continue; // 아직 안 움직였거나 이미 목표에 선 회차
                }

                speeds.Add(moved / seconds);
            }

            Assert.NotEmpty(speeds);
            speeds.Sort();

            return speeds[speeds.Count / 2];
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
