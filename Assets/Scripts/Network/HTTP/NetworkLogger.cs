using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Assets.Scripts.Network.Core.Models;
using UnityEngine;

namespace Assets.Scripts.Network.HTTP
{
    /// <summary>
    /// Unified logging system for network requests/responses/errors.
    /// Supports:
    /// - Console logging
    /// - File logging (+ rotation)
    /// - Per-run session header
    /// - Optional overwrite on Editor Play
    /// - Caller source info (Script.Function:Line)
    /// </summary>
    public class NetworkLogger
    {
        // Log levels
        public enum LogLevel
        {
            Debug = 0,
            Info = 1,
            Warning = 2,
            Error = 3,
            None = 4
        }

        public LogLevel MinLogLevel { get; set; } = LogLevel.Info;
        public bool EnableConsoleLogging { get; set; } = true;
        public bool EnableFileLogging { get; set; } = true;

        /// <summary>
        /// If true, the log file will be deleted at logger startup in UNITY_EDITOR,
        /// so each Play session starts with a fresh file.
        /// </summary>
        public bool OverwriteOnStartInEditor { get; set; } = true;

        public int MaxLogFileSizeKB { get; set; } = 1024; // 1MB

        private readonly List<LogEntry> logBuffer = new List<LogEntry>();
        private readonly object lockObject = new object();
        private string logFilePath;

        // Session tagging
        private readonly string sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
        private bool sessionHeaderWritten = false;

        public NetworkLogger()
        {
            // Default log file path
#if UNITY_EDITOR
            logFilePath = Path.Combine(Application.dataPath, "../NetworkLog.txt");

            // Auto enable file logging in Editor
            EnableFileLogging = true;

            // Optional: overwrite file on each play
            if (OverwriteOnStartInEditor)
            {
                TryOverwriteFileOnStart();
            }

            Debug.Log("[NetworkLogger] File logging ENABLED (Editor).");
            Debug.Log("[NetworkLogger] LogFilePath = " + logFilePath);

#elif UNITY_ANDROID
            logFilePath = Path.Combine(Application.persistentDataPath, "NetworkLog.txt");
#elif UNITY_WEBGL
            // WebGL has no filesystem access
            EnableFileLogging = false;
#else
            logFilePath = Path.Combine(Application.persistentDataPath, "NetworkLog.txt");
#endif
        }

        // --------------------------
        // Public API
        // --------------------------

        /// <summary>
        /// Log request (Debug level).
        /// </summary>
        public void LogRequest(
            RequestModel request,
            Dictionary<string, string> finalHeaders,
            [CallerMemberName] string callerMember = "",
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0)
        {
            if (MinLogLevel > LogLevel.Debug)
                return;

            string source = BuildSource(callerFile, callerMember, callerLine);

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = LogLevel.Debug,
                Type = "REQUEST",
                Message = $"[{request.Method}] {request.Url}",
                Details = $"Source: {source} | Headers: {FormatHeaders(finalHeaders)}, Body: {request.Body}"
            };

            AddLogEntry(entry);
        }

        /// <summary>
        /// Log response (Info on success, Warning on failure).
        /// </summary>
        public void LogResponse(
            ResponseModel response,
            string requestId,
            [CallerMemberName] string callerMember = "",
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0)
        {
            LogLevel level = response.IsSuccess ? LogLevel.Info : LogLevel.Warning;
            if (MinLogLevel > level)
                return;

            string source = BuildSource(callerFile, callerMember, callerLine);

            string truncatedData = null;
            if (!string.IsNullOrEmpty(response.RawData))
            {
                int maxLength = Math.Min(500, response.RawData.Length);
                truncatedData = response.RawData.Substring(0, maxLength);
            }

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Type = "RESPONSE",
                Message = $"Status: {response.StatusCode}, Success: {response.IsSuccess}",
                Details = $"Source: {source} | Latency: {response.TotalLatencyMs:F2}ms, RequestId: {requestId}",
                Data = truncatedData
            };

            AddLogEntry(entry);
        }

        /// <summary>
        /// Log error (Error level).
        /// </summary>
        public void LogError(
            NetworkError error,
            string requestId = null,
            [CallerMemberName] string callerMember = "",
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0)
        {
            if (MinLogLevel > LogLevel.Error)
                return;

            string source = BuildSource(callerFile, callerMember, callerLine);

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = LogLevel.Error,
                Type = "ERROR",
                Message = error?.Message ?? "Unknown error",
                Details = $"Source: {source} | Code: {error?.Code} | RequestId: {requestId ?? error?.RequestId} | Details: {error?.Details}",
                Exception = error?.OriginalException
            };

            AddLogEntry(entry);
        }

        /// <summary>
        /// Log warning (Warning level).
        /// </summary>
        public void LogWarning(
            string message,
            string details = null,
            [CallerMemberName] string callerMember = "",
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0)
        {
            if (MinLogLevel > LogLevel.Warning)
                return;

            string source = BuildSource(callerFile, callerMember, callerLine);

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = LogLevel.Warning,
                Type = "WARNING",
                Message = message,
                Details = string.IsNullOrEmpty(details) ? $"Source: {source}" : $"Source: {source} | {details}"
            };

            AddLogEntry(entry);
        }

        /// <summary>
        /// Log info (Info level).
        /// </summary>
        public void LogInfo(
            string message,
            string details = null,
            [CallerMemberName] string callerMember = "",
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0)
        {
            if (MinLogLevel > LogLevel.Info)
                return;

            string source = BuildSource(callerFile, callerMember, callerLine);

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = LogLevel.Info,
                Type = "INFO",
                Message = message,
                Details = string.IsNullOrEmpty(details) ? $"Source: {source}" : $"Source: {source} | {details}"
            };

            AddLogEntry(entry);
        }

        /// <summary>
        /// Generic log event with custom type and level.
        /// </summary>
        public void LogEvent(
            string type,
            string message,
            string details = null,
            LogLevel level = LogLevel.Info,
            [CallerMemberName] string callerMember = "",
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0)
        {
            if (MinLogLevel > level)
                return;

            string source = BuildSource(callerFile, callerMember, callerLine);

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Type = type,
                Message = message,
                Details = string.IsNullOrEmpty(details) ? $"Source: {source}" : $"Source: {source} | {details}"
            };

            AddLogEntry(entry);
        }

        // --------------------------
        // Internals
        // --------------------------

        private void AddLogEntry(LogEntry entry)
        {
            lock (lockObject)
            {
                logBuffer.Add(entry);

                // Console output
                if (EnableConsoleLogging)
                {
                    string consoleMessage = FormatLogEntry(entry);

                    switch (entry.Level)
                    {
                        case LogLevel.Debug:
                            Debug.Log(consoleMessage);
                            break;
                        case LogLevel.Info:
                            Debug.Log(consoleMessage);
                            break;
                        case LogLevel.Warning:
                            Debug.LogWarning(consoleMessage);
                            break;
                        case LogLevel.Error:
                            Debug.LogError(consoleMessage);
                            break;
                    }
                }

                // File output
                if (EnableFileLogging)
                {
                    EnsureSessionHeader();
                    WriteToFile(entry);
                }
            }
        }

        private string FormatLogEntry(LogEntry entry)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append($"[{entry.Timestamp:HH:mm:ss.fff}] ");
            sb.Append($"[SID:{sessionId}] ");
            sb.Append($"[{entry.Level}] ");
            sb.Append($"[{entry.Type}] ");
            sb.Append(entry.Message);

            if (!string.IsNullOrEmpty(entry.Details))
                sb.Append($" | {entry.Details}");

            if (!string.IsNullOrEmpty(entry.Data))
                sb.Append($" | Data: {entry.Data.Substring(0, Math.Min(100, entry.Data.Length))}...");

            return sb.ToString();
        }

        private string FormatHeaders(Dictionary<string, string> headers)
        {
            if (headers == null || headers.Count == 0)
                return "{}";

            // Hide token for safety
            var safeHeaders = new Dictionary<string, string>(headers);
            if (safeHeaders.ContainsKey("Authorization"))
                safeHeaders["Authorization"] = "Bearer ***";

            return $"{{{string.Join(", ", safeHeaders.Keys)}}}";
        }

        private void WriteToFile(LogEntry entry)
        {
            try
            {
                // Rotate if file is too large
                if (File.Exists(logFilePath) && new FileInfo(logFilePath).Length > (MaxLogFileSizeKB * 1024))
                {
                    string backupPath = logFilePath.Replace(".txt", $"_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    File.Move(logFilePath, backupPath);

                    // After rotation we want a fresh session header for the new file
                    sessionHeaderWritten = false;
                }

                string logLine = FormatLogEntry(entry) + Environment.NewLine;
                File.AppendAllText(logFilePath, logLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkLogger] Failed to write log file: {ex.Message}");
            }
        }

        private void EnsureSessionHeader()
        {
            if (sessionHeaderWritten) return;
            sessionHeaderWritten = true;

            if (!EnableFileLogging || string.IsNullOrEmpty(logFilePath))
                return;

            try
            {
                string header =
                    Environment.NewLine +
                    "==================================================" + Environment.NewLine +
                    $"===== NEW LOG SESSION | SID={sessionId} | UTC={DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} =====" + Environment.NewLine +
                    $"===== Unity={Application.unityVersion} | Platform={Application.platform} | AppVersion={Application.version} =====" + Environment.NewLine +
                    "==================================================" + Environment.NewLine;

                File.AppendAllText(logFilePath, header, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkLogger] Failed to write session header: {ex.Message}");
            }
        }

        private void TryOverwriteFileOnStart()
        {
            try
            {
                if (string.IsNullOrEmpty(logFilePath))
                    return;

                // Ensure directory exists (usually yes)
                var dir = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Delete file if exists
                if (File.Exists(logFilePath))
                    File.Delete(logFilePath);

                sessionHeaderWritten = false;

                Debug.Log("[NetworkLogger] OverwriteOnStartInEditor enabled -> log file cleared.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkLogger] Failed to overwrite log file on start: {ex.Message}");
            }
        }

        private string BuildSource(string callerFile, string callerMember, int callerLine)
        {
            string file = string.IsNullOrEmpty(callerFile) ? "UnknownFile" : Path.GetFileNameWithoutExtension(callerFile);
            string member = string.IsNullOrEmpty(callerMember) ? "UnknownMember" : callerMember;
            return $"{file}.{member}:{callerLine}";
        }

        // --------------------------
        // Buffer utilities
        // --------------------------

        public void ClearLogBuffer()
        {
            lock (lockObject)
            {
                logBuffer.Clear();
            }
        }

        public List<LogEntry> GetLogBuffer()
        {
            lock (lockObject)
            {
                return new List<LogEntry>(logBuffer);
            }
        }

        public Dictionary<LogLevel, int> GetLogStatistics()
        {
            var stats = new Dictionary<LogLevel, int>
            {
                { LogLevel.Debug, 0 },
                { LogLevel.Info, 0 },
                { LogLevel.Warning, 0 },
                { LogLevel.Error, 0 }
            };

            lock (lockObject)
            {
                foreach (var entry in logBuffer)
                {
                    if (stats.ContainsKey(entry.Level))
                        stats[entry.Level]++;
                }
            }

            return stats;
        }

        // --------------------------
        // Entry model
        // --------------------------

        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public LogLevel Level { get; set; }
            public string Type { get; set; }
            public string Message { get; set; }
            public string Details { get; set; }
            public string Data { get; set; }
            public Exception Exception { get; set; }
        }
    }
}
