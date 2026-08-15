namespace DigitalTwin.Dashboard.Tests.Slmp
{
    // float(mm) ↔ short(워드) 변환 검증. ×10 스케일이라 워드로 표현 가능한 범위는
    // -3276.8 ~ +3276.7mm 이고, 그 밖의 값은 잘려야 한다(래핑되면 부호가 뒤집혀 위험).
    public class WordConversionTests
    {
        private const ushort EndOk = 0x0000;

        private static short ReadRawWord(SlmpTestHost host, int device)
        {
            using var conn = host.Connect();
            var (end, data) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, device, 1));
            Assert.Equal(EndOk, end);
            return SlmpConnection.RawWordAt(data, 0);
        }

        [Fact]
        public void 정상범위_값은_10배_스케일로_인코딩된다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(12.3f, 0f, 0f, 0f, 0f, 0f);

            Assert.Equal((short)123, ReadRawWord(host, 0));
        }

        [Fact]
        public void 음수도_부호를_유지한다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(-45.6f, 0f, 0f, 0f, 0f, 0f);

            Assert.Equal((short)-456, ReadRawWord(host, 0));
        }

        [Fact]
        public void 상한_경계값은_잘리지_않는다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(3276.7f, 0f, 0f, 0f, 0f, 0f);

            Assert.Equal(short.MaxValue, ReadRawWord(host, 0));
        }

        [Fact]
        public void 상한을_넘는_값은_래핑되지_않고_MaxValue로_잘린다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(5000f, 0f, 0f, 0f, 0f, 0f);

            short word = ReadRawWord(host, 0);

            Assert.Equal(short.MaxValue, word);
            Assert.True(word > 0, "오버플로 래핑이 일어나면 부호가 뒤집혀 음수가 된다.");
        }

        [Fact]
        public void 하한을_넘는_값은_래핑되지_않고_MinValue로_잘린다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(-5000f, 0f, 0f, 0f, 0f, 0f);

            short word = ReadRawWord(host, 0);

            Assert.Equal(short.MinValue, word);
            Assert.True(word < 0, "오버플로 래핑이 일어나면 부호가 뒤집혀 양수가 된다.");
        }

        [Fact]
        public void 극단적으로_큰_값도_잘려서_처리된다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(float.MaxValue, float.MinValue, 1e10f, 0f, 0f, 0f);

            using var conn = host.Connect();
            var (end, data) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 0, 3));

            Assert.Equal(EndOk, end);
            Assert.Equal(short.MaxValue, SlmpConnection.RawWordAt(data, 0));
            Assert.Equal(short.MinValue, SlmpConnection.RawWordAt(data, 1));
            Assert.Equal(short.MaxValue, SlmpConnection.RawWordAt(data, 2));
        }

        [Fact]
        public void 속도_워드도_같은_규칙으로_잘린다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(0f, 0f, 0f, 99999f, -99999f, 0f);

            using var conn = host.Connect();
            var (end, data) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 3, 2));

            Assert.Equal(EndOk, end);
            Assert.Equal(short.MaxValue, SlmpConnection.RawWordAt(data, 0));
            Assert.Equal(short.MinValue, SlmpConnection.RawWordAt(data, 1));
        }

        [Fact]
        public void 소수점_아래_두번째자리는_반올림된다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(1.24f, 1.26f, 0f, 0f, 0f, 0f);

            using var conn = host.Connect();
            var (end, data) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 0, 2));

            Assert.Equal(EndOk, end);
            Assert.Equal((short)12, SlmpConnection.RawWordAt(data, 0)); // 12.4 → 12
            Assert.Equal((short)13, SlmpConnection.RawWordAt(data, 1)); // 12.6 → 13
        }

        [Fact]
        public void 정확히_중간값이면_짝수쪽으로_반올림된다()
        {
            // MathF.Round 기본값이 MidpointRounding.ToEven 이라 12.5 → 12, 13.5 → 14.
            // 프로토콜상 관측 가능한 동작이므로 명시적으로 고정해 둔다.
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(1.25f, 1.35f, 0f, 0f, 0f, 0f);

            using var conn = host.Connect();
            var (end, data) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 0, 2));

            Assert.Equal(EndOk, end);
            Assert.Equal((short)12, SlmpConnection.RawWordAt(data, 0));
            Assert.Equal((short)14, SlmpConnection.RawWordAt(data, 1));
        }

        [Fact]
        public void 쓰기_워드는_10으로_나뉘어_mm로_해석된다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            var (end, _) = conn.Request(SlmpConnection.WordWrite(SlmpConnection.DevD, 6, 123, -456));

            Assert.Equal(EndOk, end);

            var snap = host.Table.Snapshot();
            Assert.Equal(12.3f, snap.TargetX, 3);
            Assert.Equal(-45.6f, snap.TargetY, 3);
        }

        [Fact]
        public void 워드_최대최소를_써도_해석이_깨지지_않는다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            var (end, _) = conn.Request(SlmpConnection.WordWrite(
                SlmpConnection.DevD, 6, short.MaxValue, short.MinValue));

            Assert.Equal(EndOk, end);

            var snap = host.Table.Snapshot();
            Assert.Equal(3276.7f, snap.TargetX, 2);
            Assert.Equal(-3276.8f, snap.TargetY, 2);
        }

        [Fact]
        public void 쓰고_다시_읽으면_같은_워드가_나온다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            conn.Request(SlmpConnection.WordWrite(SlmpConnection.DevD, 6, 1234));
            var (end, data) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 6, 1));

            Assert.Equal(EndOk, end);
            Assert.Equal((short)1234, SlmpConnection.RawWordAt(data, 0));
        }
    }
}
