using DigitalTwin.Dashboard.Models;

namespace DigitalTwin.Dashboard.Tests.Slmp
{
    // SLMP 3E 프레임 해석기 검증: 정상 요청 / 쪼개져 온 데이터 / 잘못된 주소 요청.
    public class SlmpProtocolTests
    {
        private const ushort EndOk = 0x0000;
        private const ushort EndUnsupported = 0xC059;
        private const ushort EndCountRange = 0xC051;

        // ── 정상 요청 ──

        [Fact]
        public void WordRead_현재위치_스냅샷값을_돌려준다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(12.3f, -45.6f, -7.8f, 1f, 2f, 3f);

            using var conn = host.Connect();
            var (end, data) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 0, 3));

            Assert.Equal(EndOk, end);
            Assert.Equal(6, data.Length);
            Assert.Equal(12.3f, SlmpConnection.WordAt(data, 0), 3);
            Assert.Equal(-45.6f, SlmpConnection.WordAt(data, 1), 3);
            Assert.Equal(-7.8f, SlmpConnection.WordAt(data, 2), 3);
        }

        [Fact]
        public void WordRead_속도영역_D3부터_읽힌다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(0f, 0f, 0f, 10.5f, -20.5f, 30.5f);

            using var conn = host.Connect();
            var (end, data) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 3, 3));

            Assert.Equal(EndOk, end);
            Assert.Equal(10.5f, SlmpConnection.WordAt(data, 0), 3);
            Assert.Equal(-20.5f, SlmpConnection.WordAt(data, 1), 3);
            Assert.Equal(30.5f, SlmpConnection.WordAt(data, 2), 3);
        }

        [Fact]
        public void WordWrite_타겟을_쓰면_DeviceTable에_반영된다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            var (end, _) = conn.Request(SlmpConnection.WordWrite(
                SlmpConnection.DevD, 6,
                SlmpConnection.ToWord(30f), SlmpConnection.ToWord(-15f), SlmpConnection.ToWord(-5f)));

            Assert.Equal(EndOk, end);

            var snap = host.Table.Snapshot();
            Assert.Equal(30f, snap.TargetX, 3);
            Assert.Equal(-15f, snap.TargetY, 3);
            Assert.Equal(-5f, snap.TargetZ, 3);
        }

        [Fact]
        public void WordWrite_단일워드만_보내도_나머지_타겟은_유지된다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetTarget(1f, 2f, 3f);

            using var conn = host.Connect();
            var (end, _) = conn.Request(SlmpConnection.WordWrite(
                SlmpConnection.DevD, 7, SlmpConnection.ToWord(99f))); // TargetY만

            Assert.Equal(EndOk, end);

            var snap = host.Table.Snapshot();
            Assert.Equal(1f, snap.TargetX, 3);   // 유지
            Assert.Equal(99f, snap.TargetY, 3);  // 변경
            Assert.Equal(3f, snap.TargetZ, 3);   // 유지
        }

        [Fact]
        public void WordWrite_알람경계를_바꾸면_Limits에_반영된다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            var (end, _) = conn.Request(SlmpConnection.WordWrite(
                SlmpConnection.DevD, 10,
                SlmpConnection.ToWord(-50f), SlmpConnection.ToWord(50f)));

            Assert.Equal(EndOk, end);

            var snap = host.Table.Snapshot();
            Assert.Equal(-50f, snap.XMin, 3);
            Assert.Equal(50f, snap.XMax, 3);
        }

        [Fact]
        public void BitRead_에러플래그를_니블로_돌려준다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetErrorFlags(errorLamp: true, xError: false, yError: true, zError: false);

            using var conn = host.Connect();
            var (end, data) = conn.Request(SlmpConnection.BitRead(SlmpConnection.DevM, 100, 4));

            Assert.Equal(EndOk, end);
            Assert.Equal(2, data.Length); // 4점 = 2바이트
            Assert.True(SlmpConnection.BitAt(data, 0));   // M100 ErrorLamp
            Assert.False(SlmpConnection.BitAt(data, 1));  // M101 XError
            Assert.True(SlmpConnection.BitAt(data, 2));   // M102 YError
            Assert.False(SlmpConnection.BitAt(data, 3));  // M103 ZError
        }

        [Fact]
        public void BitRead_홀수개_요청시_마지막_하위니블은_0이다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetErrorFlags(true, true, true, true);

            using var conn = host.Connect();
            var (end, data) = conn.Request(SlmpConnection.BitRead(SlmpConnection.DevM, 100, 3));

            Assert.Equal(EndOk, end);
            Assert.Equal(2, data.Length);        // 3점 → (3+1)/2 = 2바이트
            Assert.Equal(0x00, data[1] & 0x0F);  // 남는 하위 니블은 0
        }

        // ── 쪼개져서 온 데이터 ──

        [Fact]
        public void 헤더와_본문이_쪼개져_와도_정상_파싱한다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(11.1f, 22.2f, 0f, 0f, 0f, 0f);

            using var conn = host.Connect();
            byte[] frame = SlmpConnection.WordRead(SlmpConnection.DevD, 0, 2);

            // 고정 헤더 9바이트 경계에서 자른다.
            var (end, data) = conn.RequestSplit(frame, 9);

            Assert.Equal(EndOk, end);
            Assert.Equal(11.1f, SlmpConnection.WordAt(data, 0), 3);
            Assert.Equal(22.2f, SlmpConnection.WordAt(data, 1), 3);
        }

        [Fact]
        public void 헤더_도중에_쪼개져_와도_정상_파싱한다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(5.5f, 0f, 0f, 0f, 0f, 0f);

            using var conn = host.Connect();
            byte[] frame = SlmpConnection.WordRead(SlmpConnection.DevD, 0, 1);

            // 헤더 한복판(3)과 본문 한복판(14)에서 두 번 자른다.
            var (end, data) = conn.RequestSplit(frame, 3, 14);

            Assert.Equal(EndOk, end);
            Assert.Equal(5.5f, SlmpConnection.WordAt(data, 0), 3);
        }

        [Fact]
        public void 바이트단위로_잘게_쪼개_보내도_정상_파싱한다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(7.7f, 0f, 0f, 0f, 0f, 0f);

            using var conn = host.Connect();
            byte[] frame = SlmpConnection.WordRead(SlmpConnection.DevD, 0, 1);

            int[] cuts = Enumerable.Range(1, frame.Length - 1).ToArray();
            var (end, data) = conn.RequestSplit(frame, cuts);

            Assert.Equal(EndOk, end);
            Assert.Equal(7.7f, SlmpConnection.WordAt(data, 0), 3);
        }

        [Fact]
        public void 한_연결에서_연속_요청을_처리한다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            for (int i = 1; i <= 5; i++)
            {
                host.Table.SetCurrentAndVelocity(i, 0f, 0f, 0f, 0f, 0f);
                var (end, data) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 0, 1));

                Assert.Equal(EndOk, end);
                Assert.Equal(i, SlmpConnection.WordAt(data, 0), 3);
            }
        }

        // ── 잘못된 주소 / 잘못된 요청 ──

        [Fact]
        public void 맵_밖_주소_읽기는_거부된다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            // D9는 맵에 없다(D8 다음이 D10).
            var (end, _) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 9, 1));

            Assert.Equal(EndUnsupported, end);
        }

        [Fact]
        public void 맵_끝을_넘어가는_범위_읽기는_거부된다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            // D14부터 4개면 D16, D17이 맵 밖이다.
            var (end, _) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 14, 4));

            Assert.Equal(EndUnsupported, end);
        }

        [Fact]
        public void 읽기전용_주소_쓰기는_거부되고_상태가_변하지_않는다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetCurrentAndVelocity(1.0f, 0f, 0f, 0f, 0f, 0f);

            using var conn = host.Connect();
            var (end, _) = conn.Request(SlmpConnection.WordWrite(
                SlmpConnection.DevD, 0, SlmpConnection.ToWord(999f))); // D0 = CurrentX, 읽기 전용

            Assert.Equal(EndUnsupported, end);
            Assert.Equal(1.0f, host.Table.Snapshot().CurrentX, 3);
        }

        [Fact]
        public void 쓰기범위에_읽기전용이_하나라도_섞이면_전부_거부된다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetTarget(1f, 2f, 3f);

            using var conn = host.Connect();
            // D5(읽기전용 VelocityZ) ~ D8. 하나라도 불가면 부분 적용 없이 전체 거부여야 한다.
            var (end, _) = conn.Request(SlmpConnection.WordWrite(
                SlmpConnection.DevD, 5,
                SlmpConnection.ToWord(0f), SlmpConnection.ToWord(0f),
                SlmpConnection.ToWord(77f), SlmpConnection.ToWord(88f)));

            Assert.Equal(EndUnsupported, end);

            var snap = host.Table.Snapshot();
            Assert.Equal(1f, snap.TargetX, 3); // 부분 적용 없음
            Assert.Equal(2f, snap.TargetY, 3);
            Assert.Equal(3f, snap.TargetZ, 3);
        }

        [Fact]
        public void D9를_가로지르는_쓰기는_거부된다()
        {
            using var host = new SlmpTestHost();
            host.Table.SetTarget(1f, 2f, 3f);

            using var conn = host.Connect();
            // D8(쓰기가능) → D9(맵 밖) → D10(쓰기가능)
            var (end, _) = conn.Request(SlmpConnection.WordWrite(
                SlmpConnection.DevD, 8,
                SlmpConnection.ToWord(1f), SlmpConnection.ToWord(2f), SlmpConnection.ToWord(3f)));

            Assert.Equal(EndUnsupported, end);
            Assert.Equal(3f, host.Table.Snapshot().TargetZ, 3);
        }

        [Fact]
        public void 잘못된_디바이스_코드는_거부된다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            // Word Read에 M 디바이스를 지정
            var (endWord, _) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevM, 0, 1));
            Assert.Equal(EndUnsupported, endWord);

            // Bit Read에 D 디바이스를 지정
            var (endBit, _) = conn.Request(SlmpConnection.BitRead(SlmpConnection.DevD, 100, 1));
            Assert.Equal(EndUnsupported, endBit);
        }

        [Fact]
        public void 맵_밖_M비트_읽기는_거부된다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            var (end, _) = conn.Request(SlmpConnection.BitRead(SlmpConnection.DevM, 200, 1));

            Assert.Equal(EndUnsupported, end);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(961)]
        [InlineData(1000)]
        public void 점수가_범위를_벗어나면_C051을_돌려준다(int count)
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            var (end, _) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 0, count));

            Assert.Equal(EndCountRange, end);
        }

        [Fact]
        public void 선언한_개수보다_쓰기데이터가_모자라면_C051을_돌려준다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            // count=3이라고 선언하고 데이터는 2워드(4바이트)만 보낸다.
            byte[] frame = SlmpConnection.Frame(
                0x1401, 0x0000, SlmpConnection.DevD, 6, 3,
                new byte[] { 0x01, 0x00, 0x02, 0x00 });

            var (end, _) = conn.Request(frame);

            Assert.Equal(EndCountRange, end);
        }

        [Fact]
        public void BitWrite는_미지원으로_거부된다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            // M100~M103은 전부 ErrorDetector 출력 플래그라 쓸 수 있는 비트가 없다.
            var (end, _) = conn.Request(SlmpConnection.BitWrite(SlmpConnection.DevM, 100, 1));

            Assert.Equal(EndUnsupported, end);
        }

        [Fact]
        public void 알려지지_않은_명령은_C059를_돌려준다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            byte[] frame = SlmpConnection.Frame(0x0403, 0x0000, SlmpConnection.DevD, 0, 1, null);
            var (end, _) = conn.Request(frame);

            Assert.Equal(EndUnsupported, end);
        }

        [Fact]
        public void 서브헤더가_틀린_프레임은_응답하지_않는다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            byte[] frame = SlmpConnection.WordRead(SlmpConnection.DevD, 0, 1);
            frame[0] = 0x51; // 잘못된 subheader

            Assert.True(conn.ExpectNoResponse(frame));
        }

        [Fact]
        public void 최소길이에_못미치는_프레임은_응답하지_않는다()
        {
            using var host = new SlmpTestHost();
            using var conn = host.Connect();

            // 헤더 9바이트 + 본문 2바이트 = 11바이트(최소 21 미달)
            byte[] frame = new byte[] { 0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x10, 0x00 };

            Assert.True(conn.ExpectNoResponse(frame));
        }

        [Fact]
        public void 런타임에_바뀐_알람경계가_읽기에_그대로_보인다()
        {
            var config = new DeviceConfig { AlarmXMin = -100f, AlarmXMax = 100f };
            using var host = new SlmpTestHost(config);
            using var conn = host.Connect();

            var (endBefore, before) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 10, 2));
            Assert.Equal(EndOk, endBefore);
            Assert.Equal(-100f, SlmpConnection.WordAt(before, 0), 3);
            Assert.Equal(100f, SlmpConnection.WordAt(before, 1), 3);

            conn.Request(SlmpConnection.WordWrite(
                SlmpConnection.DevD, 10,
                SlmpConnection.ToWord(-20f), SlmpConnection.ToWord(20f)));

            var (endAfter, after) = conn.Request(SlmpConnection.WordRead(SlmpConnection.DevD, 10, 2));
            Assert.Equal(EndOk, endAfter);
            Assert.Equal(-20f, SlmpConnection.WordAt(after, 0), 3);
            Assert.Equal(20f, SlmpConnection.WordAt(after, 1), 3);
        }
    }
}
