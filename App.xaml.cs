using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DigitalTwin.Dashboard.Helpers;

namespace DigitalTwin.Dashboard
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // 로그는 창이 뜨기 전에 켠다 — MainWindow 생성자(설정 로드·서버 기동)에서
        // 터지는 예외까지 파일에 남겨야 하기 때문이다.
        protected override void OnStartup(StartupEventArgs e)
        {
            string? logDir = AppLog.Initialize();

            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            AppLog.Info("App", $"애플리케이션 시작 (v{version}, pid {Environment.ProcessId})");
            AppLog.Info("App", $"로그 폴더: {logDir ?? "(초기화 실패 — 이 줄도 남지 않는다)"}");

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLog.Info("App", $"애플리케이션 종료 (code {e.ApplicationExitCode})");
            AppLog.Shutdown();

            base.OnExit(e);
        }

        // e.Handled는 건드리지 않는다. 지금까지처럼 앱은 그대로 죽되,
        // 죽은 이유가 파일에 남는다는 점만 달라진다.
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Fatal("App", $"UI 스레드 미처리 예외: {e.Exception.Message}", e.Exception);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            AppLog.Fatal("App", $"미처리 예외 (terminating={e.IsTerminating}): {ex?.Message}", ex);
            AppLog.Shutdown();   // 프로세스가 곧 사라지므로 여기서 파일을 닫는다
        }

        // 백그라운드 Task에서 삼켜지던 예외. 관측만 하고 프로세스는 살려 둔다.
        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppLog.Error("App", $"관측되지 않은 Task 예외: {e.Exception.Message}", e.Exception);
            e.SetObserved();
        }
    }
}
