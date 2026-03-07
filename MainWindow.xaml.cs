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

            UpdateStatus("알람 초기화 완료", Colors.LimeGreen);
        }

        private void BtnHome_Click(object sender, RoutedEventArgs e)
        {
            _virtualPLC.HomeAll();

            // 슬라이더도 원점으로
            SliderX.Value = 0;
            SliderY.Value = 0;
            SliderZ.Value = 0;

            UpdateStatus("원점 복귀 중...", Colors.Yellow);
        }

        #endregion

        #region Slider Events

        private void SliderX_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_virtualPLC == null) return;

            float value = (float)e.NewValue;
            _virtualPLC.MoveX(value);
            TxtXValue.Text = $"{value:F1}";
        }

        private void SliderY_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_virtualPLC == null) return;

            float value = (float)e.NewValue;
            _virtualPLC.MoveY(value);
            TxtYValue.Text = $"{value:F1}";
        }

        private void SliderZ_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_virtualPLC == null) return;

            float value = (float)e.NewValue;
            _virtualPLC.MoveZ(value);
            TxtZValue.Text = $"{value:F1}";
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

                // 차트 업데이트
                UpdateChart(data);
            });

            // 오류 검사
            _errorDetector.CheckAxisData(data);

            // Unity로 데이터 전송
            _unityIPC.SendAxisData(data);
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
                // 알람 ID 부여
                alarm.Id = ++_alarmIdCounter;

                // 알람 리스트에 추가 (최신이 위로)
                _alarmList.Insert(0, alarm);

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
                // Unity에서 받은 데이터를 슬라이더에 반영
                // ValueChanged 이벤트가 다시 발생하지 않도록 VirtualPLC 직접 업데이트
                SliderX.Value = data.X;
                SliderY.Value = data.Y;
                SliderZ.Value = data.Z;

                // 슬라이더 아래 텍스트 업데이트
                TxtXValue.Text = $"{data.X:F1}";
                TxtYValue.Text = $"{data.Y:F1}";
                TxtZValue.Text = $"{data.Z:F1}";

                // 현재 위치 표시 업데이트
                TxtCurrentX.Text = $"{data.X:F1} mm";
                TxtCurrentY.Text = $"{data.Y:F1} mm";
                TxtCurrentZ.Text = $"{data.Z:F1} mm";

                // VirtualPLC 위치 동기화 (슬라이더 이벤트 없이)
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
