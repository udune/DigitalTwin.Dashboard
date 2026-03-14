using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DigitalTwin.Dashboard.Models;
using DigitalTwin.Dashboard.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.Defaults;
using SkiaSharp;

namespace DigitalTwin.Dashboard
{
    public partial class MainWindow : Window
    {
        // Services
        private VirtualPLC _virtualPLC;
        private UnityIPCService _unityIPC;
        private ErrorDetector _errorDetector;

        // Data
        private ObservableCollection<AlarmData> _alarmList;
        private SystemStatus _systemStatus;
        private int _alarmIdCounter = 0;

        // Timers
        private DispatcherTimer _clockTimer;

        // Manual Control
        private float _currentStepSize = 1.0f;
        private float _targetX = 0f;
        private float _targetY = 0f;
        private float _targetZ = 0f;
        private bool _isHoming = false;  // 원점 복귀 중 플래그

        // Cycle Tracking
        private bool _wasAtBottom = false;  // Z축이 하단에 있었는지
        private DateTime _cycleStartTime;
        private List<double> _cycleTimes = new List<double>();

        // Chart Data
        private const int MaxDataPoints = 100;
        private ObservableCollection<ObservableValue> _xValues;
        private ObservableCollection<ObservableValue> _yValues;
        private ObservableCollection<ObservableValue> _zValues;

        public MainWindow()
        {
            InitializeComponent();

            InitializeData();
            InitializeServices();
            InitializeTimers();
            InitializeChart();

            Closing += MainWindow_Closing;
        }

        #region Initialization

        private void InitializeData()
        {
            _alarmList = new ObservableCollection<AlarmData>();
            AlarmDataGrid.ItemsSource = _alarmList;

            _systemStatus = new SystemStatus();
        }

        private void InitializeServices()
        {
            // VirtualPLC 초기화
            _virtualPLC = new VirtualPLC();
            _virtualPLC.OnDataUpdated += VirtualPLC_OnDataUpdated;
            _virtualPLC.OnError += VirtualPLC_OnError;

            // ErrorDetector 초기화
            _errorDetector = new ErrorDetector();
            _errorDetector.OnErrorDetected += ErrorDetector_OnErrorDetected;
            _errorDetector.SetCheckInterval(30.0); // 기본값 30초

            // UnityIPCService 초기화
            _unityIPC = new UnityIPCService();
            _unityIPC.OnConnected += UnityIPC_OnConnected;
            _unityIPC.OnDisconnected += UnityIPC_OnDisconnected;
            _unityIPC.OnError += UnityIPC_OnError;
            _unityIPC.OnAxisDataReceived += UnityIPC_OnAxisDataReceived;
        }

        private void InitializeTimers()
        {
            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += (s, e) =>
            {
                TxtDateTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            };
            _clockTimer.Start();

            // 초기 시간 표시
            TxtDateTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void InitializeChart()
        {
            // 데이터 컬렉션 초기화
            _xValues = new ObservableCollection<ObservableValue>();
            _yValues = new ObservableCollection<ObservableValue>();
            _zValues = new ObservableCollection<ObservableValue>();

            // 한글 폰트 설정
            var koreanTypeface = SKTypeface.FromFamilyName("Malgun Gothic");

            // 차트 시리즈 설정
            AxisChart.Series = new ISeries[]
            {
                new LineSeries<ObservableValue>
                {
                    Name = "X축",
                    Values = _xValues,
                    Stroke = new SolidColorPaint(SKColor.Parse("#00D9FF"), 2),
                    Fill = null,
                    GeometrySize = 0,
                    LineSmoothness = 0.5
                },
                new LineSeries<ObservableValue>
                {
                    Name = "Y축",
                    Values = _yValues,
                    Stroke = new SolidColorPaint(SKColor.Parse("#00FF90"), 2),
                    Fill = null,
                    GeometrySize = 0,
                    LineSmoothness = 0.5
                },
                new LineSeries<ObservableValue>
                {
                    Name = "Z축",
                    Values = _zValues,
                    Stroke = new SolidColorPaint(SKColor.Parse("#FF9900"), 2),
                    Fill = null,
                    GeometrySize = 0,
                    LineSmoothness = 0.5
                }
            };

            // X축 설정 (시간 축)
            AxisChart.XAxes = new Axis[]
            {
                new Axis
                {
                    Name = "시간",
                    NamePaint = new SolidColorPaint(SKColors.White)
                    {
                        SKTypeface = koreanTypeface
                    },
                    LabelsPaint = new SolidColorPaint(SKColors.LightGray)
                    {
                        SKTypeface = koreanTypeface
                    },
                    TextSize = 12,
                    SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#3E3E42")),
                    MinLimit = 0,
                    MaxLimit = MaxDataPoints
                }
            };

            // Y축 설정 (위치 값)
            AxisChart.YAxes = new Axis[]
            {
                new Axis
                {
                    Name = "위치 (mm)",
                    NamePaint = new SolidColorPaint(SKColors.White)
                    {
                        SKTypeface = koreanTypeface
                    },
                    LabelsPaint = new SolidColorPaint(SKColors.LightGray)
                    {
                        SKTypeface = koreanTypeface
                    },
                    TextSize = 12,
                    SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#3E3E42")),
                    MinLimit = -450,
                    MaxLimit = 450
                }
            };

            // 차트 배경색
            AxisChart.DrawMarginFrame = new DrawMarginFrame
            {
                Stroke = new SolidColorPaint(SKColor.Parse("#3E3E42"), 1)
            };

            // 범례 폰트 설정
            AxisChart.LegendTextPaint = new SolidColorPaint(SKColors.White)
            {
                SKTypeface = koreanTypeface
            };

            // 툴팁 폰트 설정
            AxisChart.TooltipTextPaint = new SolidColorPaint(SKColors.White)
            {
                SKTypeface = koreanTypeface
            };
        }

        #endregion

        #region Button Events

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnStart.IsEnabled = false;
                UpdateStatus("시스템 시작 중...", Colors.Yellow);

                // Unity IPC 시작
                _ = _unityIPC.Start();

                // VirtualPLC 시작
                await _virtualPLC.Start();

                _systemStatus.IsRunning = true;
                _systemStatus.IsPlcConnected = true;
                UpdatePlcStatus(true);

                UpdateStatus("시스템 가동 중", Colors.LimeGreen);
            }
            catch (Exception ex)
            {
                UpdateStatus($"시작 실패: {ex.Message}", Colors.Red);
                MessageBox.Show($"시스템 시작 오류:\n{ex.Message}", "오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnStart.IsEnabled = true;
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _virtualPLC.Stop();
            _systemStatus.IsRunning = false;

            UpdateStatus("시스템 정지됨", Colors.Orange);
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            // 알람 리스트 초기화
            _alarmList.Clear();
            _alarmIdCounter = 0;
            _systemStatus.TodayAlarmCount = 0;
            TxtAlarmCount.Text = "0건";

            // Unity에 오류 해제 전송
            _unityIPC.SendClearError();

            UpdateStatus("알람 클리어 완료", Colors.LimeGreen);
        }

        private void BtnHome_Click(object sender, RoutedEventArgs e)
        {
            _virtualPLC.HomeAll();

            // 타겟 값 초기화
            _targetX = 0f;
            _targetY = 0f;
            _targetZ = 0f;

            // 표시값 업데이트
            TxtXValue.Text = "0.0";
            TxtYValue.Text = "0.0";
            TxtZValue.Text = "0.0";

            // 원점 복귀 플래그 설정
            _isHoming = true;
            UpdateStatus("원점 복귀 중...", Colors.Yellow);
        }

        #endregion

        #region Manual Control Events

        private void StepSize_Changed(object sender, RoutedEventArgs e)
        {
            if (RadioStep01?.IsChecked == true)
            {
                _currentStepSize = 0.1f;
                if (TxtCustomStep != null) TxtCustomStep.IsEnabled = false;
            }
            else if (RadioStep05?.IsChecked == true)
            {
                _currentStepSize = 0.5f;
                if (TxtCustomStep != null) TxtCustomStep.IsEnabled = false;
            }
            else if (RadioStep1?.IsChecked == true)
            {
                _currentStepSize = 1.0f;
                if (TxtCustomStep != null) TxtCustomStep.IsEnabled = false;
            }
            else if (RadioStepCustom?.IsChecked == true)
            {
                if (TxtCustomStep != null)
                {
                    TxtCustomStep.IsEnabled = true;
                    if (float.TryParse(TxtCustomStep.Text, out float customStep) && customStep > 0 && customStep <= 5)
                    {
                        _currentStepSize = customStep;
                    }
                }
            }
        }

        private float GetCurrentStepSize()
        {
            if (RadioStepCustom?.IsChecked == true && TxtCustomStep != null)
            {
                if (float.TryParse(TxtCustomStep.Text, out float customStep) && customStep > 0 && customStep <= 5)
                {
                    return customStep;
                }
                return 1.0f;
            }
            return _currentStepSize;
        }

        private void BtnXMinus_Click(object sender, RoutedEventArgs e)
        {
            if (_virtualPLC == null) return;
            _targetX -= GetCurrentStepSize();
            _virtualPLC.MoveX(_targetX);
            TxtXValue.Text = $"{_targetX:F1}";
        }

        private void BtnXPlus_Click(object sender, RoutedEventArgs e)
        {
            if (_virtualPLC == null) return;
            _targetX += GetCurrentStepSize();
            _virtualPLC.MoveX(_targetX);
            TxtXValue.Text = $"{_targetX:F1}";
        }

        private void BtnYMinus_Click(object sender, RoutedEventArgs e)
        {
            if (_virtualPLC == null) return;
            _targetY -= GetCurrentStepSize();
            _virtualPLC.MoveY(_targetY);
            TxtYValue.Text = $"{_targetY:F1}";
        }

        private void BtnYPlus_Click(object sender, RoutedEventArgs e)
        {
            if (_virtualPLC == null) return;
            _targetY += GetCurrentStepSize();
            _virtualPLC.MoveY(_targetY);
            TxtYValue.Text = $"{_targetY:F1}";
        }

        private void BtnZMinus_Click(object sender, RoutedEventArgs e)
        {
            if (_virtualPLC == null) return;
            _targetZ -= GetCurrentStepSize();
            _virtualPLC.MoveZ(_targetZ);
            TxtZValue.Text = $"{_targetZ:F1}";
        }

        private void BtnZPlus_Click(object sender, RoutedEventArgs e)
        {
            if (_virtualPLC == null) return;
            _targetZ += GetCurrentStepSize();
            _virtualPLC.MoveZ(_targetZ);
            TxtZValue.Text = $"{_targetZ:F1}";
        }

        #endregion

        #region VirtualPLC Event Handlers

        private void VirtualPLC_OnDataUpdated(AxisData data)
        {
            Dispatcher.Invoke(() =>
            {
                // UI 업데이트 - 축 위치
                TxtCurrentX.Text = $"{data.X:F1} mm";
                TxtCurrentY.Text = $"{data.Y:F1} mm";
                TxtCurrentZ.Text = $"{data.Z:F1} mm";

                // UI 업데이트 - 축 속도
                TxtVelocityX.Text = $"{data.VelocityX:F1}";
                TxtVelocityY.Text = $"{data.VelocityY:F1}";
                TxtVelocityZ.Text = $"{data.VelocityZ:F1}";

                // 원점 복귀 완료 체크
                if (_isHoming)
                {
                    const float HOME_THRESHOLD = 0.5f;  // 0.5mm 이내면 원점 도달로 판단
                    if (Math.Abs(data.X) < HOME_THRESHOLD &&
                        Math.Abs(data.Y) < HOME_THRESHOLD &&
                        Math.Abs(data.Z) < HOME_THRESHOLD)
                    {
                        _isHoming = false;
                        UpdateStatus("원점 복귀 완료", Colors.LimeGreen);
                    }
                }

                // 차트 업데이트
                UpdateChart(data);

                // 사이클 카운트 (Z축 기준: 하단 도달 후 상단 복귀 시 1사이클)
                CheckCycleCompletion(data);
            });

            // 오류 검사
            _errorDetector.CheckAxisData(data);

            // Unity로 데이터 전송
            _unityIPC.SendAxisData(data);
        }

        private void CheckCycleCompletion(AxisData data)
        {
            const float Z_BOTTOM_THRESHOLD = -55f;  // 하단 근처
            const float Z_TOP_THRESHOLD = -5f;      // 상단 근처

            // Z축이 하단에 도달했는지 확인
            if (data.Z <= Z_BOTTOM_THRESHOLD && !_wasAtBottom)
            {
                _wasAtBottom = true;
                _cycleStartTime = DateTime.Now;
            }

            // Z축이 하단에서 상단으로 복귀하면 1사이클 완료
            if (data.Z >= Z_TOP_THRESHOLD && _wasAtBottom)
            {
                _wasAtBottom = false;

                // 사이클 카운트 증가
                _systemStatus.TodayCycleCount++;
                TxtCycleCount.Text = _systemStatus.TodayCycleCount.ToString();

                // 사이클 시간 계산
                double cycleTime = (DateTime.Now - _cycleStartTime).TotalSeconds;
                _cycleTimes.Add(cycleTime);

                // 평균 사이클 시간 계산
                _systemStatus.AverageCycleTime = _cycleTimes.Average();
                TxtAvgTime.Text = $"{_systemStatus.AverageCycleTime:F1}s";
            }
        }

        private void UpdateChart(AxisData data)
        {
            // 새 데이터 추가
            _xValues.Add(new ObservableValue(data.X));
            _yValues.Add(new ObservableValue(data.Y));
            _zValues.Add(new ObservableValue(data.Z));

            // 최대 데이터 포인트 유지
            if (_xValues.Count > MaxDataPoints)
            {
                _xValues.RemoveAt(0);
                _yValues.RemoveAt(0);
                _zValues.RemoveAt(0);
            }
        }

        private void VirtualPLC_OnError(string errorMessage)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatus($"PLC 오류: {errorMessage}", Colors.Red);
            });
        }

        #endregion

        #region ErrorDetector Event Handlers

        private void ErrorDetector_OnErrorDetected(AlarmData alarm)
        {
            Dispatcher.Invoke(() =>
            {
                // 같은 알람이 이미 있는지 확인
                var groupKey = alarm.GetGroupKey();
                var existingAlarm = _alarmList.FirstOrDefault(a => a.GetGroupKey() == groupKey);

                if (existingAlarm != null)
                {
                    // 기존 알람의 카운트 증가
                    existingAlarm.Count++;
                    existingAlarm.LastTime = DateTime.Now;
                    existingAlarm.OccurrenceTimes.Add(DateTime.Now);

                    // DataGrid 업데이트를 위해 항목 제거 후 재추가
                    _alarmList.Remove(existingAlarm);
                    _alarmList.Insert(0, existingAlarm);
                }
                else
                {
                    // 새로운 알람 추가
                    alarm.Id = ++_alarmIdCounter;
                    _alarmList.Insert(0, alarm);
                }

                // 통계 업데이트
                _systemStatus.TodayAlarmCount++;
                TxtAlarmCount.Text = $"{_systemStatus.TodayAlarmCount}건";

                // 상태바 업데이트
                var color = alarm.Level == "Error" ? Colors.Red : Colors.Yellow;
                UpdateStatus($"[{alarm.Level}] {alarm.Message}", color);
            });

            // Unity로 오류 전송
            _unityIPC.SendError(alarm);
        }

        #endregion

        #region UnityIPC Event Handlers

        private void UnityIPC_OnConnected()
        {
            Dispatcher.Invoke(() =>
            {
                _systemStatus.IsUnityConnected = true;
                UpdateUnityStatus(true);
                UpdateStatus("Unity 연결됨", Colors.LimeGreen);
            });
        }

        private void UnityIPC_OnDisconnected()
        {
            Dispatcher.Invoke(() =>
            {
                _systemStatus.IsUnityConnected = false;
                UpdateUnityStatus(false);
                UpdateStatus("Unity 연결 끊김", Colors.Orange);
            });
        }

        private void UnityIPC_OnError(string errorMessage)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatus($"Unity IPC 오류: {errorMessage}", Colors.Red);
            });
        }

        private void UnityIPC_OnAxisDataReceived(AxisData data)
        {
            Dispatcher.Invoke(() =>
            {
                // Unity에서 받은 데이터를 타겟 변수에 반영
                _targetX = data.X;
                _targetY = data.Y;
                _targetZ = data.Z;

                // 제어 패널 텍스트 업데이트
                TxtXValue.Text = $"{data.X:F1}";
                TxtYValue.Text = $"{data.Y:F1}";
                TxtZValue.Text = $"{data.Z:F1}";

                // 현재 위치 표시 업데이트
                TxtCurrentX.Text = $"{data.X:F1} mm";
                TxtCurrentY.Text = $"{data.Y:F1} mm";
                TxtCurrentZ.Text = $"{data.Z:F1} mm";

                // VirtualPLC 위치 동기화
                _virtualPLC.SetPosition(data.X, data.Y, data.Z);
            });
        }

        #endregion

        #region UI Helpers

        private void UpdateStatus(string message, Color color)
        {
            TxtStatusMessage.Text = message;
            TxtStatusMessage.Foreground = new SolidColorBrush(color);
        }

        private void UpdatePlcStatus(bool connected)
        {
            PlcStatusIndicator.Fill = connected
                ? new SolidColorBrush(Colors.LimeGreen)
                : new SolidColorBrush(Colors.Red);
        }

        private void UpdateUnityStatus(bool connected)
        {
            UnityStatusIndicator.Fill = connected
                ? new SolidColorBrush(Colors.LimeGreen)
                : new SolidColorBrush(Colors.Red);
        }

        #endregion

        #region Alarm Events

        private void AlarmDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // 선택된 알람 가져오기
            if (AlarmDataGrid.SelectedItem is not AlarmData selectedAlarm)
                return;

            // 그룹화된 알람인 경우에만 상세 정보 표시
            if (selectedAlarm.Count > 1)
            {
                // 발생 시간 목록 생성
                var timesList = string.Join("\n",
                    selectedAlarm.OccurrenceTimes.Select(t => t.ToString("HH:mm:ss")));

                // 상세 메시지 구성
                var detailMessage = $"【알람 정보】\n" +
                                  $"레벨: {selectedAlarm.Level}\n" +
                                  $"위치: {selectedAlarm.Location}\n" +
                                  $"메시지: {selectedAlarm.Message}\n\n" +
                                  $"【발생 통계】\n" +
                                  $"총 발생 횟수: {selectedAlarm.Count}회\n" +
                                  $"최초 발생: {selectedAlarm.FirstTime:yyyy-MM-dd HH:mm:ss}\n" +
                                  $"최근 발생: {selectedAlarm.LastTime:yyyy-MM-dd HH:mm:ss}\n\n" +
                                  $"【전체 발생 시간】\n" +
                                  $"{timesList}";

                MessageBox.Show(detailMessage, "알람 상세 정보",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // 단일 알람인 경우
                var singleMessage = $"【알람 정보】\n" +
                                  $"레벨: {selectedAlarm.Level}\n" +
                                  $"위치: {selectedAlarm.Location}\n" +
                                  $"메시지: {selectedAlarm.Message}\n" +
                                  $"발생 시간: {selectedAlarm.Time:yyyy-MM-dd HH:mm:ss}";

                MessageBox.Show(singleMessage, "알람 정보",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        #endregion

        #region Settings Events

        private void SliderCheckInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_errorDetector == null || TxtCheckInterval == null) return;

            double interval = e.NewValue;
            _errorDetector.SetCheckInterval(interval);
            TxtCheckInterval.Text = $"{interval:F0}s";
        }

        #endregion

        #region Window Events

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            // 타이머 정지
            _clockTimer?.Stop();

            // 이벤트 구독 해제
            if (_virtualPLC != null)
            {
                _virtualPLC.OnDataUpdated -= VirtualPLC_OnDataUpdated;
                _virtualPLC.OnError -= VirtualPLC_OnError;
                _virtualPLC.Stop();
            }

            if (_errorDetector != null)
            {
                _errorDetector.OnErrorDetected -= ErrorDetector_OnErrorDetected;
            }

            if (_unityIPC != null)
            {
                _unityIPC.OnConnected -= UnityIPC_OnConnected;
                _unityIPC.OnDisconnected -= UnityIPC_OnDisconnected;
                _unityIPC.OnError -= UnityIPC_OnError;
                _unityIPC.OnAxisDataReceived -= UnityIPC_OnAxisDataReceived;
                _unityIPC.Stop();
            }
        }

        #endregion
    }
}
