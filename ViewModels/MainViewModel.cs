using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DigitalTwin.Dashboard.Models;
using DigitalTwin.Dashboard.Services;

namespace DigitalTwin.Dashboard.ViewModels
{
    internal partial class MainViewModel : ObservableObject, IDisposable
    {
        // Services
        private readonly DeviceTable _deviceTable;
        private readonly VirtualPLC _virtualPLC;
        private readonly UnityIPCService _unityIPC;
        private readonly ErrorDetector _errorDetector;
        private readonly SlmpServer _slmp;
        private readonly OpcUaServer _opcua;

        // Data
        private readonly SystemStatus _systemStatus = new();
        private int _alarmIdCounter = 0;

        // Timers
        private DispatcherTimer _clockTimer;
        private DispatcherTimer _vmUpdateTimer;

        // Cycle Tracking
        private bool _wasAtBottom = false;
        private DateTime _cycleStartTime;
        private readonly List<double> _cycleTimes = new();
        private bool _isHoming = false;

        public DeviceTable DeviceTable => _deviceTable;

        #region Bound Properties

        [ObservableProperty]
        private double _checkInterval = 30.0;

        [ObservableProperty]
        private string _checkIntervalText = "30s";

        [ObservableProperty]
        private string _statusMessage = "준비";

        [ObservableProperty]
        private Brush _statusMessageBrush = new SolidColorBrush(Colors.White);

        [ObservableProperty]
        private string _dateTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        [ObservableProperty]
        private Brush _plcStatusBrush = new SolidColorBrush(Colors.Red);

        [ObservableProperty]
        private Brush _unityStatusBrush = new SolidColorBrush(Colors.Red);

        [ObservableProperty]
        private bool _isStartEnabled = true;

        [ObservableProperty]
        private string _currentXText = "0.0 mm";

        [ObservableProperty]
        private string _currentYText = "0.0 mm";

        [ObservableProperty]
        private string _currentZText = "0.0 mm";

        [ObservableProperty]
        private string _velocityXText = "0.0";

        [ObservableProperty]
        private string _velocityYText = "0.0";

        [ObservableProperty]
        private string _velocityZText = "0.0";

        [ObservableProperty]
        private int _cycleCount = 0;

        [ObservableProperty]
        private string _avgTimeText = "0.0s";

        [ObservableProperty]
        private string _alarmCountText = "0건";

        [ObservableProperty]
        private float _targetX = 0f;

        [ObservableProperty]
        private float _targetY = 0f;

        [ObservableProperty]
        private float _targetZ = 0f;

        [ObservableProperty]
        private bool _isStep01;

        [ObservableProperty]
        private bool _isStep05;

        [ObservableProperty]
        private bool _isStep1 = true;

        [ObservableProperty]
        private bool _isStepCustom;

        [ObservableProperty]
        private string _customStepText = "5.0";

        [ObservableProperty]
        private ObservableCollection<AlarmData> _alarms;

        public bool IsExportEnabled => Alarms.Count > 0;

        #endregion

        public MainViewModel()
        {
            // Load DeviceConfig
            DeviceConfig config = new DeviceConfig();
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var loadedConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<DeviceConfig>(json);
                    if (loadedConfig != null)
                    {
                        config = loadedConfig;
                    }
                }
            }
            catch (Exception ex)
            {
                _statusMessage = $"설정 파일 로드 실패: {ex.Message}";
                _statusMessageBrush = new SolidColorBrush(Colors.Yellow);
            }

            _deviceTable = new DeviceTable(config);

            _errorDetector = new ErrorDetector(_deviceTable);
            _errorDetector.OnErrorDetected += ErrorDetector_OnErrorDetected;
            _errorDetector.SetCheckInterval(_checkInterval);

            _unityIPC = new UnityIPCService(_deviceTable);
            _unityIPC.OnConnected += UnityIPC_OnConnected;
            _unityIPC.OnDisconnected += UnityIPC_OnDisconnected;
            _unityIPC.OnError += UnityIPC_OnError;

            _virtualPLC = new VirtualPLC(_deviceTable, _errorDetector, config);
            _virtualPLC.OnError += VirtualPLC_OnError;
            _virtualPLC.OnDataUpdated += VirtualPLC_OnDataUpdated;

            // SLMP 3E 서버: 기존 주입 뒤에 추가되는 순수 어댑터. 같은 DeviceTable만 경유한다.
            // Named Pipe·UI·30Hz 폴링과 동시에 :5007에서 리슨한다.
            _slmp = new SlmpServer(_deviceTable, 5007);
            _slmp.OnError += SlmpServer_OnError;
            _slmp.Start();

            // OPC UA 서버: SLMP 바로 뒤에 동일 패턴으로 추가되는 북향 게이트웨이.
            // 같은 DeviceTable을 백킹으로 공유한다(단일 진실). opc.tcp://localhost:4840.
            // OPC UA 서버: SLMP 바로 뒤에 동일 패턴으로 추가되는 북향 게이트웨이.
            // 같은 DeviceTable을 백킹으로 공유한다(단일 진실). opc.tcp://localhost:4840.
            _opcua = new OpcUaServer(_deviceTable, 4840);
            _opcua.OnError += OpcUaServer_OnError;
            _opcua.Start();

            _alarms = new ObservableCollection<AlarmData>();
            _alarms.CollectionChanged += (s, e) => OnPropertyChanged(nameof(IsExportEnabled));

            InitializeTimers();
        }

        private void InitializeTimers()
        {
            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += (s, e) =>
            {
                DateTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            };
            _clockTimer.Start();

            _vmUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _vmUpdateTimer.Tick += VmUpdateTimer_Tick;
            _vmUpdateTimer.Start();
        }

        #region Timer Tick Handlers

        private void VmUpdateTimer_Tick(object? sender, EventArgs e)
        {
            var s = _deviceTable.Snapshot();

            CurrentXText = $"{s.CurrentX:F1} mm";
            CurrentYText = $"{s.CurrentY:F1} mm";
            CurrentZText = $"{s.CurrentZ:F1} mm";

            VelocityXText = $"{s.VelocityX:F1}";
            VelocityYText = $"{s.VelocityY:F1}";
            VelocityZText = $"{s.VelocityZ:F1}";

            if (_isHoming)
            {
                const float HOME_THRESHOLD = 0.5f;
                if (Math.Abs(s.CurrentX) < HOME_THRESHOLD &&
                    Math.Abs(s.CurrentY) < HOME_THRESHOLD &&
                    Math.Abs(s.CurrentZ) < HOME_THRESHOLD)
                {
                    _isHoming = false;
                    UpdateStatus("원점 복귀 완료", Colors.LimeGreen);
                }
            }

            CheckCycleCompletion(s);
        }

        private void CheckCycleCompletion(DeviceSnapshot s)
        {
            const float Z_BOTTOM_THRESHOLD = -55f;
            const float Z_TOP_THRESHOLD = -5f;

            if (s.CurrentZ <= Z_BOTTOM_THRESHOLD && !_wasAtBottom)
            {
                _wasAtBottom = true;
                _cycleStartTime = DateTime.Now;
            }

            if (s.CurrentZ >= Z_TOP_THRESHOLD && _wasAtBottom)
            {
                _wasAtBottom = false;
                CycleCount++;

                double cycleTime = (DateTime.Now - _cycleStartTime).TotalSeconds;
                _cycleTimes.Add(cycleTime);

                double avg = _cycleTimes.Average();
                AvgTimeText = $"{avg:F1}s";
            }
        }

        #endregion

        #region Command Implementations

        [RelayCommand]
        private async Task Start()
        {
            try
            {
                IsStartEnabled = false;
                UpdateStatus("시스템 시작 중...", Colors.Yellow);

                _ = _unityIPC.Start();
                await _virtualPLC.Start();

                _systemStatus.IsRunning = true;
                _systemStatus.IsPlcConnected = true;
                UpdatePlcStatus(true);

                UpdateStatus("시스템 가동 중", Colors.LimeGreen);
            }
            catch (Exception ex)
            {
                UpdateStatus($"시작 실패: {ex.Message}", Colors.Red);
                System.Windows.MessageBox.Show($"시스템 시작 오류:\n{ex.Message}", "오류",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsStartEnabled = true;
            }
        }

        [RelayCommand]
        private void Stop()
        {
            _virtualPLC.Stop();
            _systemStatus.IsRunning = false;
            UpdateStatus("시스템 정지됨", Colors.Orange);
        }

        [RelayCommand]
        private void Reset()
        {
            Alarms.Clear();
            _alarmIdCounter = 0;
            _systemStatus.TodayAlarmCount = 0;
            AlarmCountText = "0건";

            _unityIPC.SendClearError();
            UpdateStatus("알람 클리어 완료", Colors.LimeGreen);
        }

        [RelayCommand]
        private void Home()
        {
            _deviceTable.SetTarget(0f, 0f, 0f);
            TargetX = 0f;
            TargetY = 0f;
            TargetZ = 0f;
            _isHoming = true;
            UpdateStatus("원점 복귀 중...", Colors.Yellow);
        }

        [RelayCommand]
        private void ExportAlarms()
        {
            try
            {
                string exportDir = "exports";
                if (!Directory.Exists(exportDir))
                {
                    Directory.CreateDirectory(exportDir);
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string fileName = $"alarms_{timestamp}.csv";
                string filePath = Path.Combine(exportDir, fileName);

                using (var writer = new StreamWriter(filePath, false, System.Text.Encoding.UTF8))
                {
                    writer.WriteLine("ID,발생시간,레벨,위치,메시지,발생횟수,최초발생,최근발생");
                    foreach (var alarm in Alarms)
                    {
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

                UpdateStatus($"알람 {Alarms.Count}건 내보내기 완료", Colors.LimeGreen);
                System.Windows.MessageBox.Show($"알람 기록이 저장되었습니다.\n\n" +
                              $"파일: {filePath}\n" +
                              $"알람 수: {Alarms.Count}건",
                              "내보내기 완료",
                              System.Windows.MessageBoxButton.OK,
                              System.Windows.MessageBoxImage.Information);

                System.Diagnostics.Process.Start("explorer.exe", Path.GetFullPath(exportDir));
            }
            catch (Exception ex)
            {
                UpdateStatus($"내보내기 실패: {ex.Message}", Colors.Red);
                System.Windows.MessageBox.Show($"알람 내보내기 오류:\n{ex.Message}",
                              "오류",
                              System.Windows.MessageBoxButton.OK,
                              System.Windows.MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void MoveAxis(string axisDirection)
        {
            if (string.IsNullOrEmpty(axisDirection) || axisDirection.Length < 2) return;
            char axis = axisDirection[0];
            int direction = axisDirection[1] == '+' ? 1 : -1;

            float step = GetStepSize() * direction;

            var snap = _deviceTable.Snapshot();
            float nx = snap.TargetX;
            float ny = snap.TargetY;
            float nz = snap.TargetZ;

            switch (axis)
            {
                case 'X':
                    nx += step;
                    TargetX = nx;
                    break;
                case 'Y':
                    ny += step;
                    TargetY = ny;
                    break;
                case 'Z':
                    nz += step;
                    TargetZ = nz;
                    break;
            }

            _deviceTable.SetTarget(nx, ny, nz);
        }

        #endregion

        #region Helper Methods

        private float GetStepSize()
        {
            if (IsStep01) return 0.1f;
            if (IsStep05) return 0.5f;
            if (IsStep1) return 1.0f;
            if (float.TryParse(CustomStepText, out float step) && step is > 0 and <= 5)
            {
                return step;
            }
            return 1.0f;
        }

        private string EscapeCsvField(string field)
        {
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            return field;
        }

        private void UpdatePlcStatus(bool connected)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                PlcStatusBrush = connected
                    ? new SolidColorBrush(Colors.LimeGreen)
                    : new SolidColorBrush(Colors.Red);
            });
        }

        private void UpdateUnityStatus(bool connected)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                UnityStatusBrush = connected
                    ? new SolidColorBrush(Colors.LimeGreen)
                    : new SolidColorBrush(Colors.Red);
            });
        }

        private void UpdateStatus(string message, Color color)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                StatusMessage = message;
                StatusMessageBrush = new SolidColorBrush(color);
            });
        }

        #endregion

        #region Service Event Handlers

        private void VirtualPLC_OnDataUpdated(AxisData data)
        {
            _unityIPC.SendAxisData(data);
        }

        private void VirtualPLC_OnError(string errorMessage)
        {
            UpdateStatus($"PLC 오류: {errorMessage}", Colors.Red);
        }

        private void SlmpServer_OnError(string errorMessage)
        {
            UpdateStatus($"SLMP 오류: {errorMessage}", Colors.Red);
        }

        private void OpcUaServer_OnError(string errorMessage)
        {
            UpdateStatus($"OPC UA 오류: {errorMessage}", Colors.Red);
        }

        private void UnityIPC_OnConnected()
        {
            UpdateUnityStatus(true);
            UpdateStatus("Unity 연결됨", Colors.LimeGreen);
        }

        private void UnityIPC_OnDisconnected()
        {
            UpdateUnityStatus(false);
            UpdateStatus("Unity 연결 끊김", Colors.Orange);
        }

        private void UnityIPC_OnError(string errorMessage)
        {
            UpdateStatus($"Unity IPC 오류: {errorMessage}", Colors.Red);
        }

        private void ErrorDetector_OnErrorDetected(AlarmData alarm)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var groupKey = alarm.GetGroupKey();
                var existingAlarm = Alarms.FirstOrDefault(a => a.GetGroupKey() == groupKey);

                if (existingAlarm != null)
                {
                    existingAlarm.Count++;
                    existingAlarm.LastTime = DateTime.Now;
                    existingAlarm.OccurrenceTimes.Add(DateTime.Now);

                    Alarms.Remove(existingAlarm);
                    Alarms.Insert(0, existingAlarm);
                }
                else
                {
                    alarm.Id = ++_alarmIdCounter;
                    Alarms.Insert(0, alarm);
                }

                _systemStatus.TodayAlarmCount++;
                AlarmCountText = $"{_systemStatus.TodayAlarmCount}건";

                var color = alarm.Level == "Error" ? Colors.Red : Colors.Yellow;
                UpdateStatus($"[{alarm.Level}] {alarm.Message}", color);
            });

            _unityIPC.SendError(alarm);
        }

        #endregion

        #region Slider Trigger

        partial void OnCheckIntervalChanged(double value)
        {
            _errorDetector?.SetCheckInterval(value);
            CheckIntervalText = $"{value:F0}s";
        }

        #endregion

        public void Dispose()
        {
            _clockTimer?.Stop();
            _vmUpdateTimer?.Stop();

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

            if (_slmp != null)
            {
                _slmp.OnError -= SlmpServer_OnError;
                _slmp.Stop();
            }

            if (_opcua != null)
            {
                _opcua.OnError -= OpcUaServer_OnError;
                _opcua.Stop();
            }
        }
    }
}
