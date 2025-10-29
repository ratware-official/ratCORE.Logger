/* ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
 * 
 * Program:                         ratCORE.Logger
 * Description:                     Provides a thread-safe, asynchronous logging system for console and file output.
 * Current Version:                 1.0.9433.1133 (29.10.2025)
 * Company:                         ratware
 * Author:                          Tom V. (ratware)
 * Email:                           info@ratware.de
 * Copyright:                       © 2025 ratware
 * License:                         Creative Commons Attribution 4.0 International (CC BY 4.0)
 * License URL:                     https://creativecommons.org/licenses/by/4.0/
 * Filename:                        cls.ratCORE.Logger.cs
 * Language:                        C# (.NET 8)
 * 
 * You are free to use, share, and adapt this code for any purpose,
 * even commercially, provided that proper credit is given to the author.
 * See the license link above for details.
 *  
 * ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
 * 
 * History:
 * 
 *     - 29.10.2025 - Tom V. (ratware) - Version 1.0.9433.1133
 *       Reviewed and approved
 * 
 * ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
 * 
 */

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ratCORE.Logger
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Critical = 4,
        None = 5
    }

    public sealed class LoggerConfig
    {
        /// <summary>Defines the minimum log level that will be processed. Messages below this level are ignored.</summary>
        public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        /// <summary>Enables or disables writing log messages to the console output.</summary>
        public bool WriteToConsole { get; set; } = false;

        /// <summary>Enables or disables writing log messages to a file.</summary>
        public bool WriteToFile { get; set; } = true;

        /// <summary>Specifies the directory where log files will be stored.</summary>
        public string LogDirectory { get; set; } = "logs";

        /// <summary>Defines the name of the log file (without path).</summary>
        public string LogFileName { get; set; } = "app.log";

        /// <summary>Maximum file size (in bytes) before automatic log file rolling occurs.</summary>
        public long MaxFileBytes { get; set; } = 5 * 1024 * 1024; // 5 MB rolling

        /// <summary>Determines whether to use UTC timestamps instead of local time.</summary>
        public bool UseUtcTimestamps { get; set; } = true;

        /// <summary>When true, log entries are formatted as structured JSON instead of plain text.</summary>
        public bool StructuredJson { get; set; } = false; // textformat by default

        /// <summary>Optional name of the application, written into each log entry.</summary>
        public string? ApplicationName { get; set; } = null;

        /// <summary>Number of log entries processed per batch before flushing to output.</summary>
        public int BackgroundBatchSize { get; set; } = 128; // number of entries per flush

        /// <summary>Maximum time to wait before flushing logs, even if batch size is not reached.</summary>
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(400);

        /// <summary>Maximum number of log entries that can be queued before older entries are dropped.</summary>
        public int QueueBoundedCapacity { get; set; } = 10_000; // protection against out-of-memory
    }

    public interface ILogger : IDisposable
    {
        void Log(LogLevel level, string message, Exception? ex = null, string? scope = null);
        void Debug(string message, Exception? ex = null, string? scope = null);
        void Info(string message, Exception? ex = null, string? scope = null);
        void Warning(string message, Exception? ex = null, string? scope = null);
        void Error(string message, Exception? ex = null, string? scope = null);
        void Critical(string message, Exception? ex = null, string? scope = null);
        void Flush();
    }

    internal sealed class LogEntry
    {
        /// <summary>The exact date and time when the log entry was created.</summary>
        public DateTimeOffset Timestamp { get; init; }

        /// <summary>The severity level assigned to this log entry.</summary>
        public LogLevel Level { get; init; }

        /// <summary>The main message text associated with this log entry.</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>The exception object (if any) related to this log entry.</summary>
        public Exception? Exception { get; init; }

        /// <summary>Optional contextual category or scope name that groups related log entries.</summary>
        public string? Scope { get; init; }

        /// <summary>The name of the application or component that generated the log entry.</summary>
        public string? App { get; init; }

        /// <summary>The managed thread ID from which the log entry originated.</summary>
        public int? ThreadId { get; init; }

        /// <summary>The name of the method or member that issued the log call.</summary>
        public string? SourceMember { get; init; }

        /// <summary>The full source file path of the calling code.</summary>
        public string? SourceFile { get; init; }

        /// <summary>The line number in the source file where the log call was made.</summary>
        public int? SourceLine { get; init; }
    }

    public sealed class Logger : ILogger
    {
        private readonly LoggerConfig _cfg;
        private readonly BlockingCollection<LogEntry> _queue;
        private readonly Thread _worker;
        private readonly string _filePath;
        private volatile bool _disposed;
        private readonly object _fileLock = new();

        /// <summary>Initializes the logger with the specified configuration and starts the background worker thread.</summary>
        /// <param name="config">Optional logger configuration. If null, default settings are used.</param>        
        public Logger(LoggerConfig? config = null)
        {
            _cfg = config ?? new LoggerConfig();
            //if (_cfg.MinimumLevel == LogLevel.None) _cfg.MinimumLevel = LogLevel.Info;

            Directory.CreateDirectory(_cfg.LogDirectory);
            _filePath = Path.Combine(_cfg.LogDirectory, _cfg.LogFileName);

            // bounded queue protects against memory blow-up
            _queue = new BlockingCollection<LogEntry>(new ConcurrentQueue<LogEntry>(), _cfg.QueueBoundedCapacity);

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ratCORE.Logger.Worker"
            };
            _worker.Start();
        }

        /// <summary>Core logging method used internally by all other level-specific methods.</summary>
        /// <param name="level">Specifies the severity level of the log entry (e.g., Debug, Info, Error).</param>
        /// <param name="message">The text message to be recorded in the log.</param>
        /// <param name="ex">Optional exception object providing stack trace and error details.</param>
        /// <param name="scope">Optional contextual category or tag for grouping related log entries.</param>
        /// <param name="member">The calling member name, automatically supplied by the compiler.</param>
        /// <param name="file">The full source file path of the caller, automatically supplied by the compiler.</param>
        /// <param name="line">The line number within the source file where the log call occurred, automatically supplied by the compiler.</param>
        public void Log(
            LogLevel level,
            string message,
            Exception? ex = null,
            string? scope = null,
            [CallerMemberName] string? member = null,
            [CallerFilePath] string? file = null,
            [CallerLineNumber] int line = 0)
        {
            if (_disposed) return;
            if (level < _cfg.MinimumLevel) return;

            var now = _cfg.UseUtcTimestamps ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
            var entry = new LogEntry
            {
                Timestamp = now,
                Level = level,
                Message = message,
                Exception = ex,
                Scope = scope,
                App = _cfg.ApplicationName,
                ThreadId = Environment.CurrentManagedThreadId,
                SourceMember = member,
                SourceFile = file,
                SourceLine = line
            };

            // if queue is full, discard Debug/Info first (backpressure strategy)
            if (!_queue.TryAdd(entry))
            {
                if (level >= LogLevel.Warning)
                {
                    // last attempt: add blocking for Warn and higher
                    try { _queue.Add(entry); } catch { /* give up */ }
                }
            }
        }

        /// <summary>Logs a message with the specified log level and optional exception or scope.</summary>
        /// <param name="level">The severity level of the log entry.</param>
        /// <param name="message">The text message to log.</param>
        /// <param name="ex">Optional exception to include in the log entry.</param>
        /// <param name="scope">Optional contextual scope or category for grouping log entries.</param>
        public void Log(LogLevel level, string message, Exception? ex = null, string? scope = null) => Log(level, message, ex, scope, null, null, 0);

        /// <summary>Writes a debug-level log message, typically used for detailed diagnostic information.</summary>
        /// <param name="message">The text message to log.</param>
        /// <param name="ex">Optional exception to include in the log entry.</param>
        /// <param name="scope">Optional contextual scope or category for grouping log entries.</param>        
        public void Debug(string message, Exception? ex = null, string? scope = null) => Log(LogLevel.Debug, message, ex, scope);

        /// <summary>Writes an informational log message, used for general application events.</summary>
        /// <param name="message">The text message to log.</param>
        /// <param name="ex">Optional exception to include in the log entry.</param>
        /// <param name="scope">Optional contextual scope or category for grouping log entries.</param>
        public void Info(string message, Exception? ex = null, string? scope = null) => Log(LogLevel.Info, message, ex, scope);

        /// <summary>Writes a warning log message, used for potential issues or unexpected behavior that does not stop execution.</summary>
        /// <param name="message">The text message to log.</param>
        /// <param name="ex">Optional exception to include in the log entry.</param>
        /// <param name="scope">Optional contextual scope or category for grouping log entries.</param>
        public void Warning(string message, Exception? ex = null, string? scope = null) => Log(LogLevel.Warning, message, ex, scope);

        /// <summary>Writes an error log message, typically used when an operation fails but the application can continue running.</summary>
        /// <param name="message">The text message to log.</param>
        /// <param name="ex">Optional exception to include in the log entry.</param>
        /// <param name="scope">Optional contextual scope or category for grouping log entries.</param>
        public void Error(string message, Exception? ex = null, string? scope = null) => Log(LogLevel.Error, message, ex, scope);

        /// <summary>Writes a critical-level log message, used for fatal errors that may cause the application to terminate.</summary>
        /// <param name="message">The text message to log.</param>
        /// <param name="ex">Optional exception to include in the log entry.</param>
        /// <param name="scope">Optional contextual scope or category for grouping log entries.</param>
        public void Critical(string message, Exception? ex = null, string? scope = null) => Log(LogLevel.Critical, message, ex, scope);

        /// <summary>Background loop that continuously processes queued log entries until the logger is disposed.</summary>
        private void WorkerLoop()
        {
            var buffer = new List<LogEntry>(_cfg.BackgroundBatchSize);
            var lastFlush = Stopwatch.StartNew();

            while (!_disposed || _queue.Count > 0)
            {
                try
                {
                    if (_queue.TryTake(out var item, millisecondsTimeout: (int)_cfg.FlushInterval.TotalMilliseconds))
                    {
                        buffer.Add(item);
                        if (buffer.Count >= _cfg.BackgroundBatchSize)
                        {
                            FlushBuffer(buffer);
                            buffer.Clear();
                            lastFlush.Restart();
                        }
                    }
                    else if (buffer.Count > 0 && lastFlush.Elapsed >= _cfg.FlushInterval)
                    {
                        FlushBuffer(buffer);
                        buffer.Clear();
                        lastFlush.Restart();
                    }
                }
                catch
                {
                    // swallow; logging must never crash the app
                }
            }

            if (buffer.Count > 0) FlushBuffer(buffer);
        }

        /// <summary>Writes a batch of log entries to the configured outputs (console and/or file).</summary>
        /// <param name="batch">List of log entries to be flushed.</param>
        private void FlushBuffer(List<LogEntry> batch)
        {
            if (batch.Count == 0) return;

            // console output
            if (_cfg.WriteToConsole)
            {
                foreach (var e in batch)
                {
                    var line = _cfg.StructuredJson ? FormatJson(e) : FormatText(e);
                    try { Console.WriteLine(line); } catch { /* ignore */ }
                }
            }

            // file output
            if (_cfg.WriteToFile)
            {
                try
                {
                    var sb = new StringBuilder(batch.Count * 128);
                    foreach (var e in batch)
                        sb.AppendLine(_cfg.StructuredJson ? FormatJson(e) : FormatText(e));

                    var payload = sb.ToString();
                    lock (_fileLock)
                    {
                        RollIfNeeded();
                        File.AppendAllText(_filePath, payload, Encoding.UTF8);
                    }
                }
                catch { /* ignore */ }
            }
        }

        /// <summary>Checks if the log file exceeds the configured size limit and performs file rolling when necessary.</summary>
        private void RollIfNeeded()
        {
            try
            {
                var fi = new FileInfo(_filePath);
                if (fi.Exists && fi.Length >= _cfg.MaxFileBytes)
                {
                    string ts = (_cfg.UseUtcTimestamps ? DateTime.UtcNow : DateTime.Now).ToString("yyyyMMdd_HHmmss");
                    string rolled = Path.Combine(_cfg.LogDirectory, Path.GetFileNameWithoutExtension(_cfg.LogFileName) + $".{ts}.log");
                    File.Move(_filePath, rolled, overwrite: false);
                }
            }
            catch { /* ignore rolling errors */ }
        }

        /// <summary>Formats a log entry into the plain-text line representation used for human-readable logs.</summary>
        /// <param name="e">The log entry to format.</param>
        /// <returns>A string representing the formatted log entry in text mode.</returns>
        private string FormatText(LogEntry e)
        {
            // Format:
            // DD.MM.JJJJ hh:mm:ss,ffff | LogLevel | App | ThreadID | Scope | src | Message
            // Multi-line Messages: every line with prefix

            string ts = (_cfg.UseUtcTimestamps ? e.Timestamp.UtcDateTime : e.Timestamp.LocalDateTime).ToString("dd.MM.yyyy HH:mm:ss,ffff");
            string lvl = e.Level.ToString().PadRight(8).ToUpperInvariant();
            string app = string.IsNullOrWhiteSpace(e.App) ? "-" : e.App!;
            string tid = e.ThreadId?.ToString() ?? "-";
            string scope = string.IsNullOrWhiteSpace(e.Scope) ? "-" : e.Scope!;
            string src = (e.SourceMember != null) ? $"{Path.GetFileName(e.SourceFile ?? "?")}:{e.SourceLine} {e.SourceMember}()" : "-";

            // build prefix
            string prefix = $"{ts} | {lvl} | {app} | {tid} | {scope} | {src} | ";

            // shorten message if necessary, then output line by line
            string msg = e.Message ?? string.Empty;
            if (msg.Length > 10_000) msg = msg.Substring(0, 10_000) + " …";

            var lines = msg.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sb = new StringBuilder(lines.Length * (prefix.Length + 32));
            foreach (var line in lines)
            {
                sb.Append(prefix);
                sb.AppendLine(line);
            }

            // exception (compact) also append line by line with prefix
            if (e.Exception is not null)
            {
                var exStr = CompactException(e.Exception);
                var exLines = exStr.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var el in exLines)
                {
                    sb.Append(prefix);
                    sb.AppendLine(el);
                }
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>Converts an exception (including inner exceptions) into a compact, single-line textual representation.</summary>
        /// <param name="ex">The exception to format.</param>
        /// <returns>A compact string representation of the exception details.</returns>
        private static string CompactException(Exception ex)
        {
            // compact, single-line representation (stack trace remains multi-line)
            var sb = new StringBuilder();
            sb.Append($"EX: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                sb.Append($" | Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            {
                sb.AppendLine();
                // optional: compress stack
                var compact = Regex.Replace(ex.StackTrace, @"\s+", " ");
                sb.Append(compact);
            }
            return sb.ToString();
        }

        /// <summary>Serializes the given log entry as structured JSON for machine-readable log processing.</summary>
        /// <param name="e">The log entry to convert.</param>
        /// <returns>A JSON string representing the log entry.</returns>
        private string FormatJson(LogEntry e)
        {
            JsonSerializerOptions jsonOpts = new JsonSerializerOptions()
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var obj = new
            {
                ts = e.Timestamp,
                level = e.Level.ToString(),
                app = e.App,
                tid = e.ThreadId,
                scope = e.Scope,
                src = new { file = e.SourceFile, line = e.SourceLine, member = e.SourceMember },
                message = e.Message,
                exception = e.Exception == null ? null : new
                {
                    type = e.Exception.GetType().Name,
                    msg = e.Exception.Message,
                    inner = e.Exception.InnerException == null ? null : new
                    {
                        type = e.Exception.InnerException.GetType().Name,
                        msg = e.Exception.InnerException.Message
                    },
                    stack = e.Exception.StackTrace
                }
            };
            return JsonSerializer.Serialize(obj, jsonOpts);
        }

        /// <summary>Forces any pending log entries in the queue to be flushed to their targets (file, console, etc.).</summary>
        public void Flush()
        {
            // wait until queue is empty and worker has flushed
            while (_queue.Count > 0)
                Thread.Sleep(10);

            // micro-delay to allow the worker to complete the last round
            Thread.Sleep(_cfg.FlushInterval);
        }

        /// <summary>Disposes the logger, signaling the worker thread to stop and flushing remaining entries.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _queue.CompleteAdding();
                _worker.Join(TimeSpan.FromSeconds(2));
            }
            catch { /* ignore */ }

            // prevent finalization; this class has no finalizer.
            GC.SuppressFinalize(this);
        }
    }
}
