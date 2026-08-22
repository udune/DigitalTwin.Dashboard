using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace DigitalTwin.Dashboard.Helpers
{
    /// <summary>
    /// 파일 로그 진입점. 이 앱은 WinExe(콘솔 창 없음)라 Console.WriteLine으로 적은 내용은
    /// 어디에도 남지 않는다 — 진단 기록은 전부 여기를 거쳐 파일로 간다.
    /// </summary>
    internal static class AppLog
    {
        private static readonly object _gate = new();

        // Initialize() 전에는 아무 데도 쓰지 않는 무음 로거. 테스트가 서비스를 그냥
        // 생성해도(파일을 만들지 않고) 로그 호출이 안전하게 통과한다.
        private static ILogger _logger = Logger.None;
        private static Logger? _fileLogger;

        // 실행 폴더 아래 logs/. appsettings.json과 같은 기준(BaseDirectory)을 쓴다.
        public static string DefaultDirectory =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        // Initialize()가 실제로 쓰고 있는 폴더. 초기화 전에는 null.
        public static string? CurrentDirectory { get; private set; }

        public static bool IsInitialized => CurrentDirectory != null;

        /// <summary>
        /// 파일 싱크를 붙인다. 두 번 불러도 첫 설정을 유지한다(폴더를 바꾸려면 Shutdown 먼저).
        /// 로그 폴더를 못 만들면 예외를 삼키고 무음으로 남는다 — 로그 때문에 앱이 죽지 않게 한다.
        /// </summary>
        /// <returns>실제로 쓰는 로그 폴더 경로. 초기화에 실패하면 null.</returns>
        public static string? Initialize(string? directory = null)
        {
            lock (_gate)
            {
                if (CurrentDirectory != null)
                {
                    return CurrentDirectory;
                }

                string dir = directory ?? DefaultDirectory;

                try
                {
                    Directory.CreateDirectory(dir);

                    _fileLogger = new LoggerConfiguration()
                        .MinimumLevel.Debug()
                        .WriteTo.File(
                            Path.Combine(dir, "dashboard-.log"),
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 14,
                            // buffered:false(기본)라 매 줄 flush 된다 — 앱이 죽어도 직전 줄이 남는다.
                            outputTemplate:
                                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{Source}] {Message:lj}{NewLine}{Exception}")
                        .CreateLogger();

                    _logger = _fileLogger;
                    CurrentDirectory = dir;
                    return dir;
                }
                catch (Exception)
                {
                    _logger = Logger.None;
                    _fileLogger = null;
                    CurrentDirectory = null;
                    return null;
                }
            }
        }

        /// <summary>남은 내용을 비우고 파일 핸들을 놓는다.</summary>
        public static void Shutdown()
        {
            lock (_gate)
            {
                _logger = Logger.None;
                _fileLogger?.Dispose();
                _fileLogger = null;
                CurrentDirectory = null;
            }
        }

        public static void Debug(string source, string message) =>
            Write(LogEventLevel.Debug, source, message, null);

        public static void Info(string source, string message) =>
            Write(LogEventLevel.Information, source, message, null);

        public static void Warn(string source, string message, Exception? ex = null) =>
            Write(LogEventLevel.Warning, source, message, ex);

        public static void Error(string source, string message, Exception? ex = null) =>
            Write(LogEventLevel.Error, source, message, ex);

        public static void Fatal(string source, string message, Exception? ex = null) =>
            Write(LogEventLevel.Fatal, source, message, ex);

        // 메시지 본문은 항상 파라미터로 넘긴다. 알람 메시지·예외 문구에 '{'가 섞여 있어도
        // Serilog가 그것을 템플릿 구멍으로 오해하지 않는다.
        private static void Write(LogEventLevel level, string source, string message, Exception? ex)
        {
            ILogger logger = _logger;
            logger.ForContext("Source", source).Write(level, ex, "{LogMessage:l}", message);
        }
    }
}
