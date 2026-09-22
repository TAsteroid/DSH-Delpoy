using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DSHDeploy.Core
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public sealed class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }

        public override string ToString()
        {
            return Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                   + " [" + Level.ToString().ToUpperInvariant() + "] "
                   + Message;
        }
    }

    public interface IUi
    {
        /// <summary>Append one log line to the UI.</summary>
        void Log(LogEntry entry);

        /// <summary>Current step headline, e.g. "正在安装 Node.js".</summary>
        void SetStatus(string text);

        /// <summary>Overall progress 0..100, or -1 for indeterminate.</summary>
        void SetProgress(int percent);

        /// <summary>Ask a yes/no question. Returns false when the user declines.</summary>
        bool Consent(string title, string message);

        /// <summary>Ask for a single string value. Returns null when cancelled.</summary>
        string AskString(string title, string message, bool secret);

        /// <summary>Report the final outcome.</summary>
        void Finish(Outcome outcome);
    }

    public sealed class Outcome
    {
        public bool Success { get; set; }
        public string Summary { get; set; }
        public string LogPath { get; set; }
        public string Url { get; set; }
        public readonly List<string> Actions = new List<string>();
    }

    /// <summary>Console-only UI used by --headless and --self-test.</summary>
    public sealed class ConsoleUi : IUi
    {
        private readonly bool _assumeYes;

        public ConsoleUi(bool assumeYes)
        {
            _assumeYes = assumeYes;
        }

        public void Log(LogEntry entry)
        {
            var colour = Console.ForegroundColor;
            if (entry.Level == LogLevel.Error) Console.ForegroundColor = ConsoleColor.Red;
            else if (entry.Level == LogLevel.Warn) Console.ForegroundColor = ConsoleColor.Yellow;
            else if (entry.Level == LogLevel.Debug) Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(entry.ToString());
            Console.ForegroundColor = colour;
        }

        public void SetStatus(string text)
        {
            Console.WriteLine("==> " + text);
        }

        public void SetProgress(int percent)
        {
            // Progress is noise in a log file; the status line carries the same information.
        }

        public bool Consent(string title, string message)
        {
            Console.WriteLine();
            Console.WriteLine("[" + title + "]");
            Console.WriteLine(message);
            if (_assumeYes)
            {
                Console.WriteLine("--yes 生效: 自动同意");
                return true;
            }
            Console.Write("同意吗? [y/N] ");
            string answer = Console.ReadLine();
            return answer != null && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                                   || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
        }

        public string AskString(string title, string message, bool secret)
        {
            Console.WriteLine();
            Console.WriteLine("[" + title + "]");
            Console.WriteLine(message);
            if (_assumeYes)
            {
                return null; // never invent a credential
            }
            Console.Write("输入 (留空跳过): ");
            if (!secret)
            {
                string plain = Console.ReadLine();
                return string.IsNullOrWhiteSpace(plain) ? null : plain.Trim();
            }
            var sb = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
                if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        public void Finish(Outcome outcome) { }
    }

    /// <summary>File + UI logger. Every message is masked before it reaches either sink.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static IUi _ui;
        private static StreamWriter _writer;
        private static string _path;

        public static string Path { get { return _path; } }

        public static void Init(IUi ui, string logPath)
        {
            lock (Gate)
            {
                _ui = ui;
                _path = logPath;
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    _writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                    _writer.AutoFlush = true;
                }
                catch
                {
                    _writer = null; // logging must never break deployment
                }
            }
        }

        /// <summary>
        /// Point the UI sink at a live window. Called from the form's Shown handler, because the
        /// window cannot be handed to <see cref="Init"/> at startup — the logger is initialized
        /// before any form exists, when the only available sink discards entries.
        /// </summary>
        public static void Attach(IUi ui)
        {
            lock (Gate) { _ui = ui; }
        }

        public static void Close()
        {
            lock (Gate)
            {
                if (_writer != null)
                {
                    try { _writer.Flush(); _writer.Dispose(); } catch { }
                    _writer = null;
                }
            }
        }

        public static void Debug(string format, params object[] args) { Write(LogLevel.Debug, format, args); }
        public static void Info(string format, params object[] args) { Write(LogLevel.Info, format, args); }
        public static void Warn(string format, params object[] args) { Write(LogLevel.Warn, format, args); }
        public static void Error(string format, params object[] args) { Write(LogLevel.Error, format, args); }

        private static void Write(LogLevel level, string format, object[] args)
        {
            string message = args == null || args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args);
            message = Mask(message);
            var entry = new LogEntry { Timestamp = DateTime.Now, Level = level, Message = message };
            lock (Gate)
            {
                if (_writer != null)
                {
                    try { _writer.WriteLine(entry.ToString()); } catch { }
                }
            }
            IUi ui = _ui;
            if (ui != null)
            {
                try { ui.Log(entry); } catch { }
            }
        }

        /// <summary>
        /// Replace anything that looks like a secret. Applied to <em>every</em> log message so a
        /// key can never reach the log file even through an unexpected code path.
        /// </summary>
        public static string Mask(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string masked = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"sk-[A-Za-z0-9_\-]{4,}",
                "sk-****",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return masked;
        }
    }
}
