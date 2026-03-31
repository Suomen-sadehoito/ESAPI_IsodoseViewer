using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace EQD2Viewer.Core.Logging
{
    /// <summary>
    /// Lightweight logger for ESAPI scripts. Writes to Debug output and optionally to file.
    /// Replaces silent catch blocks with traceable diagnostics.
    /// Thread-safe via lock.
    /// </summary>
    public static class SimpleLogger
    {
        private static readonly object _lock = new object();
        private static string _logFilePath;
        private static bool _fileLoggingEnabled;

        /// <summary>
        /// Enables file logging to the user's desktop. Call once at startup.
        /// </summary>
        public static void EnableFileLogging(string fileName = "EQD2Viewer.log")
        {
            lock (_lock)
            {
                try
                {
                    _logFilePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);
                    _fileLoggingEnabled = true;
                    Info("File logging enabled: " + _logFilePath);
                }
                catch
                {
                    _fileLoggingEnabled = false;
                }
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warning(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message, Exception ex = null)
        {
            string full = ex != null ? $"{message} | {ex.GetType().Name}: {ex.Message}" : message;
            Write("ERROR", full);
            if (ex != null)
                Debug.WriteLine($"[EQD2Viewer STACK] {ex.StackTrace}");
        }

        private static void Write(string level, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
            Debug.WriteLine(line);

            if (_fileLoggingEnabled)
            {
                lock (_lock)
                {
                    try
                    {
                        File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
                    }
                    catch
                    {
                        // File logging failed — don't recurse
                    }
                }
            }
        }
    }
}
