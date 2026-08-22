using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using DigitalTwin.Dashboard.Helpers;
using DigitalTwin.Dashboard.Models;
using DigitalTwin.Dashboard.Services;
using DigitalTwin.Dashboard.ViewModels;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.Defaults;
using SkiaSharp;

namespace DigitalTwin.Dashboard
{
    public partial class MainWindow : Window
    {
        // View Model
        private readonly MainViewModel _viewModel;

        // Chart Data
        private const int MaxDataPoints = 100;
        private ObservableCollection<ObservableValue> _xValues;
        private ObservableCollection<ObservableValue> _yValues;
        private ObservableCollection<ObservableValue> _zValues;

        public MainWindow()
        {
            InitializeComponent();

            // 설정 읽기는 뷰모델 밖에서 한다. 실패해도 기본값으로 뜨고 사유만 상태줄에 남는다.
            var config = DeviceConfig.Load(null, out string? configWarning);
            _viewModel = new MainViewModel(config, configWarning);
            DataContext = _viewModel;

            InitializeChart();

            // 차트는 뷰모델의 ~30Hz 타이머가 읽은 스냅샷을 그대로 받는다(타이머를 따로 두지 않는다).
            // Initialize()가 타이머를 켜기 전에 붙여야 첫 틱부터 그린다.
            _viewModel.SnapshotUpdated += ViewModel_SnapshotUpdated;

            // 서버·타이머 기동. 생성자에서 분리했을 뿐 시점은 종전과 같다(창이 뜰 때 자동).
            _viewModel.Initialize();

            AppLog.Info("UI", "메인 창 준비 완료");

            Closing += MainWindow_Closing;
        }

        #region Initialization

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

        // 뷰모델의 ~30Hz 타이머가 읽은 스냅샷으로 차트를 갱신한다(UI 스레드에서 호출됨).
        private void ViewModel_SnapshotUpdated(DeviceSnapshot s)
        {
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
            // 차트 구독 해제 (타이머 정지는 뷰모델 Dispose가 한다)
            if (_viewModel != null)
            {
                _viewModel.SnapshotUpdated -= ViewModel_SnapshotUpdated;
            }

            // View Model 리소스 해제
            _viewModel?.Dispose();

            AppLog.Info("UI", "메인 창 닫힘");
        }

        #endregion
    }
}
