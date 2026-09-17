using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Orchestra.Core.Contracts;
using Orchestra.Core.Models;

namespace Orchestra.Core.Services
{
    public class PythonEngine : IDisposable
    {
        private readonly IAgentLogger _logger;
        private Process? _pythonProcess;

        /// <summary>True only while the process is actually alive. Callers (App.xaml.cs) should check this before opening the main window instead of assuming StartProcess succeeded.</summary>
        public bool IsRunning => _pythonProcess != null && !_pythonProcess.HasExited;
        private StreamWriter? _stdin;
        private StreamReader? _stdout;
        private bool _isDisposed;

        // The Python side is a simple one-line-in, one-line-out loop with
        // no request IDs, so two overlapping calls would interleave their
        // writes/reads on the same pipe and corrupt both. Now that the
        // protocol has grown multiple action types (chat, list_chats,
        // create_chat, etc.) that the UI can plausibly fire close together,
        // this needs to be a strict one-at-a-time queue, not "usually fine".
        private readonly SemaphoreSlim _ipcLock = new(1, 1);

        public PythonEngine(IAgentLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void StartProcess(string pythonExecutablePath, string scriptPath)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonExecutablePath,
                    Arguments = $"\"{scriptPath}\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                _pythonProcess = new Process { StartInfo = startInfo };
                _pythonProcess.Start();

                _stdin = _pythonProcess.StandardInput;
                _stdout = _pythonProcess.StandardOutput;

                // Fire-and-forget background monitoring for Python crash logs
                _ = Task.Run(MonitorErrorsAsync);

                _logger.LogInfo("Python Engine (Pillar 5) started successfully in headless mode.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start Python process: {ex.Message}");
                throw;
            }
        }

        public async Task<AgentResponse?> SendRequestAsync(AgentRequest request)
        {
            if (_stdin == null || _stdout == null)
            {
                throw new InvalidOperationException("Python process is not running.");
            }

            await _ipcLock.WaitAsync();
            try
            {
                string jsonRequest = JsonSerializer.Serialize(request);

                await _stdin.WriteLineAsync(jsonRequest);
                await _stdin.FlushAsync();

                string? jsonResponse = await _stdout.ReadLineAsync();

                if (string.IsNullOrWhiteSpace(jsonResponse))
                {
                    _logger.LogError("Received empty response from Python Engine.");
                    return new AgentResponse
                    {
                        IsError = true,
                        ErrorMessage = "Empty process response.",
                    };
                }

                return JsonSerializer.Deserialize<AgentResponse>(jsonResponse);
            }
            catch (Exception ex)
            {
                _logger.LogError($"IPC Communication error: {ex.Message}");
                return new AgentResponse { IsError = true, ErrorMessage = ex.Message };
            }
            finally
            {
                _ipcLock.Release();
            }
        }

        private async Task MonitorErrorsAsync()
        {
            if (_pythonProcess == null)
                return;

            using var stderr = _pythonProcess.StandardError;
            while (!stderr.EndOfStream)
            {
                string? errorLine = await stderr.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(errorLine))
                {
                    _logger.LogError($"[PYTHON STDERR]: {errorLine}");
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;
            _isDisposed = true;

            try
            {
                if (_pythonProcess != null && !_pythonProcess.HasExited)
                {
                    _pythonProcess.Kill();
                    _pythonProcess.WaitForExit(1000);
                }

                _stdin?.Dispose();
                _stdout?.Dispose();
                _pythonProcess?.Dispose();
                _ipcLock.Dispose();

                _logger.LogInfo("Python Engine gracefully terminated.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during Python Engine disposal: {ex.Message}");
            }
        }
    }
}
