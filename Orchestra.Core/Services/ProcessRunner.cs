using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestra.Core.Services
{
    public record ProcessResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// Small helper for running external commands (winget, python, ollama)
    /// asynchronously while streaming their output line-by-line to a
    /// progress callback, with a timeout and cancellation support.
    /// Shared by <see cref="PythonEnvironmentManager"/> and
    /// <see cref="OllamaManager"/> to avoid duplicating Process plumbing.
    /// </summary>
    public static class ProcessRunner
    {
        // Compiled Regex to strip ANSI terminal control sequences (progress bars, spinners, color codes)
        // Catches standard color codes, private terminal modes (with ?), carriage returns, and spinners.
        private static readonly Regex AnsiRegex = new(@"(\x1b\[[0-9;?]*[a-zA-Z]|\r|[\u2800-\u28FF])", RegexOptions.Compiled);

        public static async Task<ProcessResult> RunAsync(
            string fileName,
            string arguments,
            IProgress<string>? progress = null,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Architectural Fix: Force explicit UTF-8 encoding to prevent Windows system codepage unicode corruption
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                psi.WorkingDirectory = workingDirectory;
            }

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var stdOutBuffer = new StringBuilder();
            var stdErrBuffer = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;

                // Fixed: Added string.Empty as the second argument for Regex.Replace
                string cleanLine = AnsiRegex.Replace(e.Data, string.Empty);
                if (!string.IsNullOrWhiteSpace(cleanLine))
                {
                    stdOutBuffer.AppendLine(cleanLine);
                    progress?.Report(cleanLine);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;

                // Fixed: Added string.Empty as the second argument for Regex.Replace
                string cleanLine = AnsiRegex.Replace(e.Data, string.Empty);
                if (!string.IsNullOrWhiteSpace(cleanLine))
                {
                    stdErrBuffer.AppendLine(cleanLine);
                    progress?.Report(cleanLine);
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process: {fileName} {arguments}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new TimeoutException($"Process timed out after {timeout}: {fileName} {arguments}");
            }

            return new ProcessResult(process.ExitCode, stdOutBuffer.ToString(), stdErrBuffer.ToString());
        }

        public static bool CommandExists(string commandName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "where" : "which",
                    Arguments = commandName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null) return false;
                process.WaitForExit(3000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public static void RefreshSessionPath()
        {
            if (!OperatingSystem.IsWindows()) return;

            string? machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
            string? user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("Path", $"{machine};{user}");
        }
    }
}