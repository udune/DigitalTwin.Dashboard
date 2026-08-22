using System.Diagnostics;
using DigitalTwin.Dashboard.Helpers;
using DigitalTwin.Dashboard.Models;

namespace DigitalTwin.Dashboard.Services
{
    internal class VirtualPLC
    {
        private const float MaxAccel = 500.0f;

        // 목표 주기(Hz). 실제 주기는 OS 타이머 해상도(Windows 기본 ~15.6ms)에 눌려
        // 이보다 느려질 수 있으므로, 계산에는 이 값이 아니라 실측 경과 시간을 쓴다.
        private const int UpdateRate = 100;

        // deltaTime 상한(초). 디버거 정지·스레드 기아로 루프가 오래 멈춘 뒤
        // 한 프레임에 축이 크게 순간이동하는 것을 막는다.
        private const float MaxDeltaTime = 0.1f;

        private bool isRunning = false;
        private CancellationTokenSource cts;

        public event Action<AxisData> OnDataUpdated;
        public event Action<string> OnError;

        // 물리적 이동 한계 = travel clamp (알람 경계와는 별개, P1).
        private readonly float _xLimit;
        private readonly float _yLimit;
        private readonly float _zMin;
        private readonly float _zMax;

        // 보간 최대 속도 = 설정값(mm/s). 산출 속도의 상한을 결정하므로
        // 과속 경보 임계(DeviceConfig.AlarmMaxVelocity)와 짝을 이룬다.
        private readonly float _maxSpeed;

        private readonly DeviceTable _deviceTable;
        private readonly ErrorDetector _errorDetector;

        public bool IsRunning => isRunning;

        public VirtualPLC(DeviceTable deviceTable, ErrorDetector errorDetector, DeviceConfig config)
        {
            _deviceTable = deviceTable;
            _errorDetector = errorDetector;
            _xLimit = config.XLimit;
            _yLimit = config.YLimit;
            _zMin = config.ZMin;
            _zMax = config.ZMax;
            // 0 이하면 축이 영원히 멈추므로 기본값으로 되돌린다.
            _maxSpeed = config.MaxSpeed > 0f ? config.MaxSpeed : 100f;
        }

        public float MaxSpeed => _maxSpeed;

        public Task Start()
        {
            if (isRunning)
            {
                return Task.CompletedTask;
            }

            isRunning = true;
            cts = new CancellationTokenSource();

            _ = Task.Run(() => UpdateLoop(cts.Token));

            return Task.CompletedTask;
        }

        public void Stop()
        {
            isRunning = false;
            cts?.Cancel();
        }

        private async Task UpdateLoop(CancellationToken token)
        {
            // 가정한 주기(1/UpdateRate)가 아니라 실제로 흐른 시간을 재서 쓴다.
            // Task.Delay(10ms)는 OS 타이머 해상도 때문에 실제로는 ~15.6ms 걸리므로,
            // 가정값을 쓰면 이동량과 산출 속도가 실제보다 ~1.5배 부풀려진다.
            var clock = Stopwatch.StartNew();
            double lastElapsed = clock.Elapsed.TotalSeconds;
            const int periodMs = 1000 / UpdateRate;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    double frameStart = clock.Elapsed.TotalSeconds;
                    float deltaTime = (float)(frameStart - lastElapsed);
                    lastElapsed = frameStart;

                    if (deltaTime <= 0f)
                    {
                        // 첫 반복(경과 0). 0으로 나누면 속도가 무한대가 되므로 목표 주기로 대체.
                        deltaTime = 1f / UpdateRate;
                    }
                    else if (deltaTime > MaxDeltaTime)
                    {
                        deltaTime = MaxDeltaTime;
                    }

                    // ① DeviceTable에서 target / 이전 current 읽기
                    var snap = _deviceTable.Snapshot();

                    // ② 기존 보간 로직으로 새 current 계산 (travel clamp는 여기 유지 = P1)
                    float targetX = Math.Clamp(snap.TargetX, -_xLimit, _xLimit);
                    float targetY = Math.Clamp(snap.TargetY, -_yLimit, _yLimit);
                    float targetZ = Math.Clamp(snap.TargetZ, _zMin, _zMax);

                    float currentX = MoveTowards(snap.CurrentX, targetX, _maxSpeed * deltaTime);
                    float currentY = MoveTowards(snap.CurrentY, targetY, _maxSpeed * deltaTime);
                    float currentZ = MoveTowards(snap.CurrentZ, targetZ, _maxSpeed * deltaTime);

                    // 속도 계산 (실제 이동한 거리 / 실측 경과 시간)
                    float velX = (currentX - snap.CurrentX) / deltaTime;
                    float velY = (currentY - snap.CurrentY) / deltaTime;
                    float velZ = (currentZ - snap.CurrentZ) / deltaTime;

                    // ③ DeviceTable에 current/velocity 기록
                    _deviceTable.SetCurrentAndVelocity(currentX, currentY, currentZ, velX, velY, velZ);

                    // 경계 판정 (current 기록 직후, 루프 매 회차 결정적 판정, UI 스레드 밖)
                    _errorDetector.Evaluate();

                    // Unity 송신 트리거 (cadence 기존 그대로 유지 = 루프 1회당 1건·백그라운드, T6)
                    OnDataUpdated?.Invoke(new AxisData
                    {
                        X = currentX,
                        Y = currentY,
                        Z = currentZ,
                        VelocityX = velX,
                        VelocityY = velY,
                        VelocityZ = velZ,
                        Timestamp = DateTime.Now
                    });

                    // 이번 프레임의 작업 시간만큼 빼고 쉰다(드리프트 보상).
                    // OS 타이머 해상도가 하한이라 목표 100Hz에 늘 닿지는 않지만,
                    // 남는 오차는 다음 프레임의 deltaTime이 그대로 흡수한다.
                    int workMs = (int)((clock.Elapsed.TotalSeconds - frameStart) * 1000.0);
                    await Task.Delay(Math.Max(1, periodMs - workMs), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    AppLog.Error("PLC", $"UpdateLoop 오류: {e.Message}", e);
                    OnError?.Invoke($"UpdateLoop 전송 오류: {e.Message}");
                }
            }
        }

        // 인스턴스 상태를 쓰지 않는 순수 함수. 단위 테스트에서 직접 호출할 수 있게 internal static.
        internal static float MoveTowards(float current, float target, float maxDelta)
        {
            if (Math.Abs(target - current) <= maxDelta)
            {
                return target;
            }

            return current + Math.Sign(target - current) * maxDelta;
        }
    }
}
