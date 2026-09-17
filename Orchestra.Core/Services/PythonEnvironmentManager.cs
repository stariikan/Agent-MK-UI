using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Orchestra.Core.Contracts;

namespace Orchestra.Core.Services
{
    /// <summary>
    /// Ensures a usable Python 3.10+ interpreter exists, then creates a
    /// dedicated virtual environment under AI_Runtime\.venv and installs
    /// the packages the agent runtime needs. Everything is idempotent: if
    /// the venv already has the right interpreter, steps are skipped.
    /// </summary>
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

        /// <summary>
        /// Finds a system Python 3.10+ on PATH (tries "py", "python", "python3"
        /// in that order, since the Windows launcher "py" is the most reliable
        /// way to find a real install rather than the Microsoft Store stub).
        /// </summary>
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

            // ARCHITECTURAL FIX:
            // Changed '--id Python.Python.3.12' to '--id Python.Python'.
            // This instructs winget to pull the highest non-prerelease version available.
            var result = await ProcessRunner.RunAsync(
                "winget",
                "install -e --id Python.Python --silent --accept-package-agreements --accept-source-agreements",
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

        /// <summary>Runs `--version` against a known-good interpreter path and returns the raw output (used for the pre-flight scan report).</summary>
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

        // --- ARCHITECTURAL FIX: Strong Validation ---
        public bool VenvExists(string venvRootDir)
        {
            string exePath = GetVenvPythonPath(venvRootDir);
            string cfgPath = Path.Combine(venvRootDir, ".venv", "pyvenv.cfg");

            // Environment is only valid if BOTH the executable and the configuration map exist
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

            // --- ARCHITECTURAL FIX: State Healing ---
            // If the folder exists but VenvExists() is false, the environment is corrupted/zombied.
            // We must forcibly delete it before asking the Python CLI to build a new one.
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

        /// <summary>Quick check for the pre-flight scan: does the venv already have the required packages, without re-running pip.</summary>
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

        /// <summary>
        /// requirementsFilePath is passed explicitly (rather than derived
        /// from venvRootDir) because the venv now lives in a stable
        /// AppData location, separate from AI_Runtime's script/requirements
        /// folder under the build output -- see SetupOrchestrator.
        /// </summary>
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
