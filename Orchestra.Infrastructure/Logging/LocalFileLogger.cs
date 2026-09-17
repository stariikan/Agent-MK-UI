using System;
using System.IO;
using Orchestra.Core.Contracts;

namespace Orchestra.Infrastructure.Logging;

/// <summary>
/// A thread-safe logging service that writes directly to the user's AppData directory.
/// </summary>
public class LocalFileLogger : IAgentLogger
{
    private readonly string _logFilePath;
    private static readonly object _lockObj = new object();

    public LocalFileLogger()
    {
        // Resolves to: C:\Users\<User>\AppData\Roaming\AgentMK\Logs
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appDataPath, "AgentMK", "Logs");

        if (!Directory.Exists(appFolder))
        {
            Directory.CreateDirectory(appFolder);
        }

        // Creates a rolling daily log file to prevent one massive, unreadable file
        _logFilePath = Path.Combine(appFolder, $"AgentMK_Log_{DateTime.Now:yyyyMMdd}.txt");

        LogInfo("========================================");
        LogInfo("Logger Initialized. Application Starting.");
        LogInfo("========================================");
    }

    public void LogInfo(string message) => WriteLog("INFO", message);

    public void LogWarning(string message) => WriteLog("WARN", message);

    public void LogError(string message) => WriteLog("ERROR", message);

    private void WriteLog(string level, string message)
    {
        // High-precision timestamp for debugging multithreaded AI operations
        string logEntry =
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";

        // Critical: Ensure thread safety when the UI and AI background tasks log simultaneously
        lock (_lockObj)
        {
            File.AppendAllText(_logFilePath, logEntry);
        }
    }
}
