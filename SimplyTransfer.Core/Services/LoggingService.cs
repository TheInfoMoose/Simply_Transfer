using System;
using System.IO;
using System.Threading;

namespace SimplyTransfer.Core.Services
{
    /// <summary>
    /// Thread-safe persistent file logging service that writes operational and error logs
    /// to local AppData storage with daily rolling log files.
    /// </summary>
    public class LoggingService
    {
        private readonly object _lock = new();
        private readonly string _logDirectory;

        /// <summary>
        /// Gets the directory path where log files are stored.
        /// </summary>
        public string LogDirectory => _logDirectory;

        /// <summary>
        /// Gets the current daily log file path.
        /// </summary>
        public string LogFilePath => Path.Combine(_logDirectory, $"simplytransfer_{DateTime.Now:yyyyMMdd}.log");

        /// <summary>
        /// Initializes a new instance of the <see cref="LoggingService"/> class.
        /// Ensures the log directory exists in AppData.
        /// </summary>
        public LoggingService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _logDirectory = Path.Combine(appData, "SimplyTransfer", "logs");

            try
            {
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }
            }
            catch
            {
                // Fallback to temp if AppData is inaccessible
                _logDirectory = Path.Combine(Path.GetTempPath(), "SimplyTransfer", "logs");
                Directory.CreateDirectory(_logDirectory);
            }
        }

        /// <summary>
        /// Writes an informational message to the log file.
        /// </summary>
        public void LogInfo(string message) => Log(message, "INFO");

        /// <summary>
        /// Writes a warning message to the log file.
        /// </summary>
        public void LogWarn(string message) => Log(message, "WARN");

        /// <summary>
        /// Writes a success message to the log file.
        /// </summary>
        public void LogSuccess(string message) => Log(message, "SUCCESS");

        /// <summary>
        /// Writes an error message and optional exception details to the log file.
        /// </summary>
        public void LogError(string message, Exception? ex = null) => Log(message, "ERROR", ex);

        /// <summary>
        /// Writes a formatted log entry to disk.
        /// </summary>
        /// <param name="message">The message to log.</param>
        /// <param name="level">The severity level (INFO, WARN, ERROR, SUCCESS).</param>
        /// <param name="ex">Optional exception to include with stack trace.</param>
        public void Log(string message, string level = "INFO", Exception? ex = null)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var threadId = Environment.CurrentManagedThreadId;
                var logLine = $"[{timestamp}] [{level.ToUpperInvariant()}] [Thread-{threadId:D2}] {message}";

                if (ex != null)
                {
                    logLine += Environment.NewLine + $"Exception: {ex.GetType().FullName}: {ex.Message}" +
                               Environment.NewLine + $"Stack Trace:" + Environment.NewLine + ex.StackTrace;
                    if (ex.InnerException != null)
                    {
                        logLine += Environment.NewLine + $"Inner Exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}" +
                                   Environment.NewLine + $"Inner Stack Trace:" + Environment.NewLine + ex.InnerException.StackTrace;
                    }
                }

                lock (_lock)
                {
                    File.AppendAllText(LogFilePath, logLine + Environment.NewLine);
                }
            }
            catch
            {
                // File logging should never crash the application
            }
        }
    }
}
