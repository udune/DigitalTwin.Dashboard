using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
        private DeviceTable _deviceTable;
        private VirtualPLC _virtualPLC;
        private UnityIPCService _unityIPC;
        private ErrorDetector _errorDetector;

        // Data
        private ObservableCollection<AlarmData> _alarmList;
        private SystemStatus _systemStatus;
        private int _alarmIdCounter = 0;

        // Timers
        private DispatcherTimer _clockTimer;
        private DispatcherTimer _uiTimer;  // ~30Hz Snapshot 폴링 (P5)

        // Manual Control
        private float _currentStepSize = 1.0f;
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
            _alarmList.CollectionChanged += AlarmList_CollectionChanged;
            AlarmDataGrid.ItemsSource = _alarmList;

            _systemStatus = new SystemStatus();
        }

        private void AlarmList_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // 알람이 있을 때만 내보내기 버튼 활성화
            BtnExportAlarms.IsEnabled = _alarmList.Count > 0;
        }

        private void InitializeServices()
        {
            // DeviceTable - 단일 진실. 모든 서비스가 이걸 공유한다.
            _deviceTable = new DeviceTable();

            // ErrorDetector 초기화 (DeviceTable Limits를 읽어 경계 판정)
            _errorDetector = new ErrorDetector(_deviceTable);
            _errorDetector.OnErrorDetected += ErrorDetector_OnErrorDetected;
            _errorDetector.SetCheckInterval(30.0); // 기본값 30초

            // UnityIPCService 초기화 (수신부는 DeviceTable에 직접 기록)
            _unityIPC = new UnityIPCService(_deviceTable);
            _unityIPC.OnConnected += UnityIPC_OnConnected;
            _unityIPC.OnDisconnected += UnityIPC_OnDisconnected;
            _unityIPC.OnError += UnityIPC_OnError;

            // VirtualPLC 초기화 (100Hz 루프가 DeviceTable 기반 보간·판정·송신)
            _virtualPLC = new VirtualPLC(_deviceTable, _errorDetector);
            _virtualPLC.OnError += VirtualPLC_OnError;
            // OnDataUpdated는 Unity 송신 트리거 용도로만 구독(UI는 30Hz 폴링으로 분리, P5/T6)
            _virtualPLC.OnDataUpdated += VirtualPLC_OnDataUpdated;
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

            // ~30Hz UI 폴링 타이머: DeviceTable Snapshot을 읽어 UI/차트 갱신 (P5, 보고서 F-3 해소)
            _uiTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
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
            // 타겟을 원점으로 (DeviceTable 단일 target)
            _deviceTable.SetTarget(0f, 0f, 0f);

            // 표시값 업데이트
            TxtXValue.Text = "0.0";
            TxtYValue.Text = "0.0";
            TxtZValue.Text = "0.0";

            // 원점 복귀 플래그 설정
            _isHoming = true;
            UpdateStatus("원점 복귀 중...", Colors.Yellow);
        }

        private void BtnExportAlarms_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // exports 디렉토리 생성
                string exportDir = "exports";
                if (!Directory.Exists(exportDir))
                {
                    Directory.CreateDirectory(exportDir);
                }

                // 파일명 생성 (타임스탬프 포함)
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string fileName = $"alarms_{timestamp}.csv";
                string filePath = Path.Combine(exportDir, fileName);

                // CSV 파일 작성
                using (var writer = new StreamWriter(filePath, false, System.Text.Encoding.UTF8))
                {
                    // CSV 헤더 작성
                    writer.WriteLine("ID,발생시간,레벨,위치,메시지,발생횟수,최초발생,최근발생");

                    // 알람 데이터 작성
                    foreach (var alarm in _alarmList)
                    {
                        // CSV 이스케이프 처리 (쉼표, 따옴표 포함된 필드는 따옴표로 감싸기)
                        string message = EscapeCsvField(alarm.Message);

                        writer.WriteLine($"{alarm.Id}," +
                                       $"{alarm.Time:yyyy-MM-dd HH:mm:ss}," +
                                       $"{alarm.Level}," +
                                       $"{alarm.Location}," +
                                       $"{message}," +
                                       $"{alarm.Count}," +
                                       $"{alarm.FirstTime:yyyy-MM-dd HH:mm:ss}," +
                                       $"{alarm.LastTime:yyyy-MM-dd HH:mm:ss}");
                    }
                }

                // 성공 메시지 표시
                UpdateStatus($"알람 {_alarmList.Count}건 내보내기 완료", Colors.LimeGreen);
                MessageBox.Show($"알람 기록이 저장되었습니다.\n\n" +
                              $"파일: {filePath}\n" +
                              $"알람 수: {_alarmList.Count}건",
                              "내보내기 완료",
                              MessageBoxButton.OK,
                              MessageBoxImage.Information);

                // 파일 탐색기로 폴더 열기
                System.Diagnostics.Process.Start("explorer.exe", Path.GetFullPath(exportDir));
            }
            catch (Exception ex)
            {
                UpdateStatus($"내보내기 실패: {ex.Message}", Colors.Red);
                MessageBox.Show($"알람 내보내기 오류:\n{ex.Message}",
                              "오류",
                              MessageBoxButton.OK,
                              MessageBoxImage.Error);
            }
        }

        private string EscapeCsvField(string field)
        {
            // CSV 필드에 쉼표, 따옴표, 줄바꿈이 있으면 따옴표로 감싸기
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                // 따옴표를 두 개로 이스케이프
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            return field;
        }

        #endregion

        #region Manual Control Events

        private void StepSize_Changed(object sender, RoutedEventArgs e)
        {
            // 프리셋 스텝 사이즈 매핑
            _currentStepSize = (RadioStep01?.IsChecked, RadioStep05?.IsChecked, RadioStep1?.IsChecked) switch
            {
                (true, _, _) => 0.1f,
                (_, true, _) => 0.5f,
                (_, _, true) => 1.0f,
                _ => TryParseCustomStep()
            };

            // 커스텀 입력 필드 활성화 여부
            if (TxtCustomStep != null)
                TxtCustomStep.IsEnabled = RadioStepCustom?.IsChecked == true;
        }

        private float TryParseCustomStep()
        {
            if (TxtCustomStep != null &&
                float.TryParse(TxtCustomStep.Text, out float step) &&
                step is > 0 and <= 5)
            {
                return step;
            }
            return 1.0f;
        }

        private float GetCurrentStepSize()
        {
            // 커스텀 모드일 때는 실시간으로 텍스트 박스 값 파싱
            if (RadioStepCustom?.IsChecked == true)
                return TryParseCustomStep();

            return _currentStepSize;
        }

        private void MoveAxis(char axis, int direction)
        {
            if (_deviceTable == null) return;

            float step = GetCurrentStepSize() * direction;

            // 현재 target을 Snapshot에서 읽어 증분 후 DeviceTable에 기록 (P4)
            var snap = _deviceTable.Snapshot();
            float nx = snap.TargetX;
            float ny = snap.TargetY;
            float nz = snap.TargetZ;

            switch (axis)
            {
                case 'X':
                    nx += step;
                    TxtXValue.Text = $"{nx:F1}";
                    break;
                case 'Y':
                    ny += step;
                    TxtYValue.Text = $"{ny:F1}";
                    break;
                case 'Z':
                    nz += step;
                    TxtZValue.Text = $"{nz:F1}";
                    break;
            }

            _deviceTable.SetTarget(nx, ny, nz);
        }

        private void BtnXMinus_Click(object sender, RoutedEventArgs e) => MoveAxis('X', -1);
        private void BtnXPlus_Click(object sender, RoutedEventArgs e) => MoveAxis('X', +1);
        private void BtnYMinus_Click(object sender, RoutedEventArgs e) => MoveAxis('Y', -1);
        private void BtnYPlus_Click(object sender, RoutedEventArgs e) => MoveAxis('Y', +1);
        private void BtnZMinus_Click(object sender, RoutedEventArgs e) => MoveAxis('Z', -1);
        private void BtnZPlus_Click(object sender, RoutedEventArgs e) => MoveAxis('Z', +1);

        #endregion

        #region VirtualPLC Event Handlers

        // 100Hz 백그라운드 루프가 호출 — Unity 송신 트리거 전용(cadence 보존, T6).
        // UI 갱신은 하지 않는다(30Hz 폴링이 담당).
        private void VirtualPLC_OnDataUpdated(AxisData data)
        {
            _unityIPC.SendAxisData(data);
        }

        // ~30Hz UI 폴링: DeviceTable Snapshot을 읽어 텍스트·차트·사이클을 갱신 (P5)
        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            var s = _deviceTable.Snapshot();

            // UI 업데이트 - 축 위치
            TxtCurrentX.Text = $"{s.CurrentX:F1} mm";
            TxtCurrentY.Text = $"{s.CurrentY:F1} mm";
            TxtCurrentZ.Text = $"{s.CurrentZ:F1} mm";

            // UI 업데이트 - 축 속도
            TxtVelocityX.Text = $"{s.VelocityX:F1}";
            TxtVelocityY.Text = $"{s.VelocityY:F1}";
            TxtVelocityZ.Text = $"{s.VelocityZ:F1}";

            // 원점 복귀 완료 체크
            if (_isHoming)
            {
                const float HOME_THRESHOLD = 0.5f;  // 0.5mm 이내면 원점 도달로 판단
                if (Math.Abs(s.CurrentX) < HOME_THRESHOLD &&
                    Math.Abs(s.CurrentY) < HOME_THRESHOLD &&
                    Math.Abs(s.CurrentZ) < HOME_THRESHOLD)
                {
                    _isHoming = false;
                    UpdateStatus("원점 복귀 완료", Colors.LimeGreen);
                }
            }

            // 차트 업데이트
            UpdateChart(s);

            // 사이클 카운트 (Z축 기준: 하단 도달 후 상단 복귀 시 1사이클)
            CheckCycleCompletion(s);
        }

        private void CheckCycleCompletion(DeviceSnapshot s)
        {
            const float Z_BOTTOM_THRESHOLD = -55f;  // 하단 근처
            const float Z_TOP_THRESHOLD = -5f;      // 상단 근처

            // Z축이 하단에 도달했는지 확인
            if (s.CurrentZ <= Z_BOTTOM_THRESHOLD && !_wasAtBottom)
            {
                _wasAtBottom = true;
                _cycleStartTime = DateTime.Now;
            }

            // Z축이 하단에서 상단으로 복귀하면 1사이클 완료
            if (s.CurrentZ >= Z_TOP_THRESHOLD && _wasAtBottom)
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

        private void UpdateChart(DeviceSnapshot s)
        {
            // 새 데이터 추가
            _xValues.Add(new ObservableValue(s.CurrentX));
            _yValues.Add(new ObservableValue(s.CurrentY));
            _zValues.Add(new ObservableValue(s.CurrentZ));

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

        // Unity 수신은 이제 UnityIPCService가 DeviceTable.SetTarget으로 직접 처리한다(T4).
        // 화면 갱신은 30Hz 폴링(UiTimer_Tick)이 Snapshot을 읽어 담당.

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
            _uiTimer?.Stop();

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
                _unityIPC.Stop();
            }
        }

        #endregion
    }
}
