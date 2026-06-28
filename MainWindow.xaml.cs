using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using DigitalTwin.Dashboard.Models;
using DigitalTwin.Dashboard.Services;
using DigitalTwin.Dashboard.ViewModels;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.Defaults;
using SkiaSharp;
using System.Windows.Threading;

namespace DigitalTwin.Dashboard
{
    public partial class MainWindow : Window
    {
        // View Model
        private readonly MainViewModel _viewModel;

        // Timers for View-only updates (Chart)
        private DispatcherTimer _uiTimer;  // ~30Hz Snapshot 폴링 (P5)

        // Chart Data
        private const int MaxDataPoints = 100;
        private ObservableCollection<ObservableValue> _xValues;
        private ObservableCollection<ObservableValue> _yValues;
        private ObservableCollection<ObservableValue> _zValues;

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            InitializeChart();
            InitializeTimers();

            Closing += MainWindow_Closing;
        }

        #region Initialization

        private void InitializeTimers()
        {
            // ~30Hz UI 폴링 타이머: DeviceTable Snapshot을 읽어 차트 갱신 (P5)
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

        #region UI Tick (Chart Only)

        // ~30Hz UI 폴링: DeviceTable Snapshot을 읽어 차트 갱신
        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            var s = _viewModel.DeviceTable.Snapshot();
            UpdateChart(s);
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

        #endregion

        #region Alarm Double Click Handler

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

        #region Window Events

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            // 타이머 정지
            _uiTimer?.Stop();

            // View Model 리소스 해제
            _viewModel?.Dispose();
        }

        #endregion
    }
}
