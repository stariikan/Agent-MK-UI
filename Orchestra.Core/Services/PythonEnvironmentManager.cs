using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Orchestra.Core.Contracts;

namespace Orchestra.Core.Services
{
    public class PythonEnvironmentManager
    {
        private readonly IAgentLogger _logger;
        private static readonly Regex VersionRegex = new(
            @"Python (\d+)\.(\d+)",
            RegexOptions.Compiled
        );

        public PythonEnvironmentManager(IAgentLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string? FindSystemPython()
        {
            foreach (var candidate in new[] { "py", "python", "python3" })
            {
                if (!ProcessRunner.CommandExists(candidate))
                    continue;

                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc == null)
                        continue;

                    string combined =
                        proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                    proc.WaitForExit(5000);

                    var match = VersionRegex.Match(combined);
                    if (match.Success)
                    {
                        int major = int.Parse(match.Groups[1].Value);
                        int minor = int.Parse(match.Groups[2].Value);
                        if (major > 3 || (major == 3 && minor >= 10))
                        {
                            return candidate;
                        }
                    }
                }
                catch
                {
                    // try next candidate
                }
            }

            return null;
        }

        public async Task<bool> InstallPythonViaWingetAsync(
            IProgress<string> progress,
            CancellationToken ct = default
        )
        {
            if (!ProcessRunner.CommandExists("winget"))
            {
                progress.Report(
                    "winget is not available. Install Python 3.10+ manually from "
                        + "https://python.org/downloads/ and restart the app."
                );
                return false;
            }

            progress.Report("Installing the latest Python via winget (this can take a minute)...");

            // UPDATED: Fixed winget APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER
            var result = await ProcessRunner.RunAsync(
                "winget",
                "install -e --id Python.Python.3.12 --silent --accept-package-agreements --accept-source-agreements",
                progress,
                timeout: TimeSpan.FromMinutes(10),
                cancellationToken: ct
            );

            ProcessRunner.RefreshSessionPath();

            if (result.ExitCode != 0)
            {
                _logger.LogWarning($"winget python install exited with code {result.ExitCode}");
            }

            return FindSystemPython() != null;
        }

        public string? GetPythonVersionString(string pythonExe)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null)
                    return null;

                string combined = (
                    proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd()
                ).Trim();
                proc.WaitForExit(5000);
                return combined.Length > 0 ? combined : null;
            }
            catch
            {
                return null;
            }
        }

        public string GetVenvPythonPath(string venvRootDir) =>
            Path.Combine(venvRootDir, ".venv", "Scripts", "python.exe");

        public bool VenvExists(string venvRootDir)
        {
            string exePath = GetVenvPythonPath(venvRootDir);
            string cfgPath = Path.Combine(venvRootDir, ".venv", "pyvenv.cfg");

            return File.Exists(exePath) && File.Exists(cfgPath);
        }

        public async Task<bool> CreateVenvAsync(
            string systemPython,
            string venvRootDir,
            IProgress<string> progress,
            CancellationToken ct = default
        )
        {
            string venvDir = Path.Combine(venvRootDir, ".venv");

            if (VenvExists(venvRootDir))
            {
                progress.Report(
                    "Virtual environment already exists and is valid, skipping creation."
                );
                return true;
            }

            if (Directory.Exists(venvDir))
            {
                progress.Report("Found corrupted virtual environment. Cleaning up...");
                try
                {
                    Directory.Delete(venvDir, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to delete corrupted venv directory: {ex.Message}");
                    progress.Report(
                        "Failed to clean up corrupted environment. Please restart the app or manually delete the AgentMK venv folder."
                    );
                    return false;
                }
            }

            Directory.CreateDirectory(venvRootDir);

            progress.Report($"Creating virtual environment at {venvDir}...");
            var result = await ProcessRunner.RunAsync(
                systemPython,
                $"-m venv \"{venvDir}\"",
                progress,
                timeout: TimeSpan.FromMinutes(3),
                cancellationToken: ct
            );

            if (result.ExitCode != 0 || !VenvExists(venvRootDir))
            {
                _logger.LogError($"venv creation failed: {result.StdErr}");
                return false;
            }

            return true;
        }

        public bool AreDependenciesInstalled(string venvRootDir)
        {
            string venvPython = GetVenvPythonPath(venvRootDir);
            if (!File.Exists(venvPython))
                return false;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = venvPython,
                    Arguments = "-c \"import psutil, openai, anthropic\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null)
                    return false;
                proc.WaitForExit(10000);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> InstallDependenciesAsync(
            string venvRootDir,
            string requirementsFilePath,
            IProgress<string> progress,
            CancellationToken ct = default
        )
        {
            string venvPython = GetVenvPythonPath(venvRootDir);

            if (!File.Exists(venvPython))
            {
                progress.Report(
                    "Cannot install dependencies: virtual environment Python not found."
                );
                return false;
            }

            if (!File.Exists(requirementsFilePath))
            {
                progress.Report(
                    $"No requirements.txt found at {requirementsFilePath}, skipping dependency install."
                );
                return true;
            }

            progress.Report("Upgrading pip...");
            await ProcessRunner.RunAsync(
                venvPython,
                "-m pip install --upgrade pip --quiet",
                progress,
                timeout: TimeSpan.FromMinutes(3),
                cancellationToken: ct
            );

            progress.Report(
                "Installing Python dependencies (openai, anthropic, google-generativeai, psutil)..."
            );
            var result = await ProcessRunner.RunAsync(
                venvPython,
                $"-m pip install -r \"{requirementsFilePath}\" --quiet",
                progress,
                timeout: TimeSpan.FromMinutes(10),
                cancellationToken: ct
            );

            if (result.ExitCode != 0)
            {
                _logger.LogError($"pip install failed: {result.StdErr}");
                return false;
            }

            return true;
        }
    }
}