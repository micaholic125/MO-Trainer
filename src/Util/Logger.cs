using System;
using System.IO;
using System.Globalization;
using System.Text;

namespace MoTrainer.Util
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    /// <summary>一条日志记录，供界面订阅后着色渲染。</summary>
    public sealed class LogEntry
    {
        private readonly DateTime _time;
        private readonly LogLevel _level;
        private readonly string _source;
        private readonly string _message;

        public LogEntry(DateTime time, LogLevel level, string source, string message)
        {
            _time = time;
            _level = level;
            _source = source;
            _message = message;
        }

        public DateTime Time { get { return _time; } }
        public LogLevel Level { get { return _level; } }
        public string Source { get { return _source; } }
        public string Message { get { return _message; } }

        public string Tag
        {
            get
            {
                switch (_level)
                {
                    case LogLevel.Debug: return "DBG";
                    case LogLevel.Info: return "INF";
                    case LogLevel.Warn: return "WRN";
                    default: return "ERR";
                }
            }
        }

        public string Format()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} [{1}] [{2}] {3}", _time, Tag, _source, _message);
        }
    }

    /// <summary>
    /// 分级日志。文件按天切分写入 &lt;exe目录&gt;\logs\，保留最近 7 份。
    /// 不写注册表、不写 C 盘、不记录与项目无关的个人信息。
    /// </summary>
    public static class Logger
    {
        private static readonly object Gate = new object();
        private static string _logDir;
        private static string _currentFile;
        private static StreamWriter _writer;
        private static DateTime _currentDate = DateTime.MinValue;
        private static LogLevel _minLevel = LogLevel.Info;
        private static int _keepDays = 7;
        private static bool _broken;

        /// <summary>日志写入回调。可能来自任意线程，订阅方需自行切回 UI 线程。</summary>
        public static event Action<LogEntry> OnLog;

        public static string LogDirectory { get { return _logDir; } }
        public static string CurrentFile { get { return _currentFile; } }

        public static void SetMinimumLevel(LogLevel level)
        {
            _minLevel = level;
        }

        public static void Init(string baseDirectory)
        {
            if (string.IsNullOrEmpty(baseDirectory))
            {
                baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }
            lock (Gate)
            {
                _logDir = Path.Combine(baseDirectory, "logs");
                try
                {
                    Directory.CreateDirectory(_logDir);
                    OpenWriter(DateTime.Now);
                    RemoveOldLogs();
                }
                catch (Exception ex)
                {
                    // 日志系统本身不可用时不抛异常，仅置故障位，避免影响主流程。
                    _broken = true;
                    _currentFile = "(log unavailable: " + ex.Message + ")";
                }
            }
            Info("Logger", "===== log started =====");
            Info("Logger", "base directory: " + baseDirectory);
        }

        public static void Debug(string source, string message) { Write(LogLevel.Debug, source, message); }
        public static void Info(string source, string message) { Write(LogLevel.Info, source, message); }
        public static void Warn(string source, string message) { Write(LogLevel.Warn, source, message); }
        public static void Error(string source, string message) { Write(LogLevel.Error, source, message); }

        public static void Error(string source, string message, Exception ex)
        {
            if (ex == null)
            {
                Write(LogLevel.Error, source, message);
                return;
            }
            Write(LogLevel.Error, source, message + " | " + ex.GetType().Name + ": " + ex.Message);
        }

        public static void Write(LogLevel level, string source, string message)
        {
            if (level < _minLevel) { return; }

            LogEntry entry;
            lock (Gate)
            {
                DateTime now = DateTime.Now;
                entry = new LogEntry(now, level, source, message);

                if (!_broken)
                {
                    try
                    {
                        if (_writer == null || _currentDate != now.Date)
                        {
                            OpenWriter(now);
                            RemoveOldLogs();
                        }
                        _writer.WriteLine(entry.Format());
                    }
                    catch
                    {
                        _broken = true;
                    }
                }
            }

            Action<LogEntry> handler = OnLog;
            if (handler != null)
            {
                try { handler(entry); }
                catch { /* 订阅方异常不得影响日志主流程 */ }
            }
        }

        private static void OpenWriter(DateTime now)
        {
            if (_writer != null)
            {
                try { _writer.Dispose(); }
                catch { }
                _writer = null;
            }

            _currentDate = now.Date;
            _currentFile = Path.Combine(_logDir,
                "trainer-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

            // 带 BOM：方便用户直接用记事本打开时正确显示中文
            FileStream fs = new FileStream(_currentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, new UTF8Encoding(true));
            _writer.AutoFlush = true;
        }

        private static void RemoveOldLogs()
        {
            try
            {
                string[] files = Directory.GetFiles(_logDir, "trainer-*.log");
                if (files.Length <= _keepDays) { return; }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                int removeCount = files.Length - _keepDays;
                for (int i = 0; i < removeCount; i++)
                {
                    try { File.Delete(files[i]); }
                    catch { }
                }
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }
    }
}
