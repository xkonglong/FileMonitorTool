using System;
using System.IO;
using System.Text;

namespace FileMonitorTool
{
    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static string _logPath;
        private const long MaxSize = 1024 * 1024;

        public static void Initialize(string path)
        {
            lock (Sync)
            {
                _logPath = string.IsNullOrWhiteSpace(path) ? null : path;
                if (_logPath != null)
                {
                    string dir = Path.GetDirectoryName(_logPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                }
            }
        }

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }
        public static void Error(string msg, Exception ex) { Write("ERROR", msg + " | " + ex); }

        private static void Write(string level, string message)
        {
            lock (Sync)
            {
                if (_logPath == null) return;
                try
                {
                    RotateIfNeeded();
                    string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                        DateTime.Now, level, message, Environment.NewLine);
                    File.AppendAllText(_logPath, line, Encoding.UTF8);
                }
                catch { /* 日志失败不能影响主流程 */ }
            }
        }

        private static void RotateIfNeeded()
        {
            var fi = new FileInfo(_logPath);
            if (!fi.Exists || fi.Length <= MaxSize) return;

            string bak = _logPath + ".1";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(_logPath, bak);
        }
    }
}