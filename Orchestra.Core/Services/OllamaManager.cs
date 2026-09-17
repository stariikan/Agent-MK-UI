using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Orchestra.Core.Contracts;

namespace Orchestra.Core.Services
{
    public sealed record OllamaProcessSummary(bool IsOnline, string Text);

    /// <summary>
    /// Ensures Ollama is installed, running, and has the requested model
    /// pulled. Installation prefers winget; if that's unavailable or fails,
    /// falls back to downloading the official Windows installer and
    /// running it with a silent-install flag. The Ollama installer's
    /// silent-mode support isn't officially documented, so if the silent
    /// attempt doesn't take, the installer's normal UI appears instead of
    /// silently failing.
    /// </summary>
    public class OllamaManager
    {
        private const string OllamaApiBase = "http://127.0.0.1:11434";
        private readonly IAgentLogger _logger;
        private readonly HttpClient _httpClient;

        public OllamaManager(IAgentLogger logger, HttpClient httpClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public bool IsInstalled() => ProcessRunner.CommandExists("ollama");

        public async Task<bool> IsReachableAsync(CancellationToken ct = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                var response = await _httpClient.GetAsync(
                    $"{OllamaApiBase}/api/version",
                    cts.Token
                );
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> InstallAsync(
            IProgress<string> progress,
            CancellationToken ct = default
        )
        {
            if (IsInstalled())
            {
                progress.Report("Ollama already installed.");
                return true;
            }

            if (ProcessRunner.CommandExists("winget"))
            {
                progress.Report("Installing Ollama via winget...");
                var result = await ProcessRunner.RunAsync(
                    "winget",
                    "install -e --id Ollama.Ollama --silent --accept-package-agreements --accept-source-agreements",
                    progress,
                    timeout: TimeSpan.FromMinutes(10),
                    cancellationToken: ct
                );

                ProcessRunner.RefreshSessionPath();

                if (result.ExitCode == 0 && IsInstalled())
                {
                    return true;
                }

                progress.Report("winget install did not succeed, falling back to direct download.");
            }
            else
            {
                progress.Report("winget not available, downloading Ollama installer directly.");
            }

            string installerPath = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");

            progress.Report(
                "Downloading Ollama installer from https://ollama.com/download/OllamaSetup.exe ..."
            );
            using (
                var response = await _httpClient.GetAsync(
                    "https://ollama.com/download/OllamaSetup.exe",
                    ct
                )
            )
            {
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(installerPath);
                await response.Content.CopyToAsync(fs, ct);
            }

            progress.Report("Running Ollama installer (attempting silent install)...");
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
            };

            using (var proc = Process.Start(psi))
            {
                if (proc != null)
                {
                    await proc.WaitForExitAsync(ct);
                }
            }

            ProcessRunner.RefreshSessionPath();
            return IsInstalled();
        }

        public async Task<bool> EnsureRunningAsync(
            IProgress<string> progress,
            CancellationToken ct = default
        )
        {
            if (await IsReachableAsync(ct))
            {
                progress.Report("Ollama is already running.");
                return true;
            }

            progress.Report("Starting Ollama server...");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ollama",
                    Arguments = "serve",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start 'ollama serve': {ex.Message}");
                return false;
            }

            for (int i = 0; i < 20; i++)
            {
                if (await IsReachableAsync(ct))
                {
                    progress.Report("Ollama is up.");
                    return true;
                }
                await Task.Delay(2000, ct);
            }

            progress.Report("Ollama did not become reachable within 40 seconds.");
            return false;
        }

        public async Task<bool> PullModelAsync(
            string modelTag,
            IProgress<string> progress,
            CancellationToken ct = default
        )
        {
            progress.Report(
                $"Pulling model '{modelTag}' (this may take a while depending on size and connection speed)..."
            );

            var result = await ProcessRunner.RunAsync(
                "ollama",
                $"pull {modelTag}",
                progress,
                timeout: TimeSpan.FromHours(2),
                cancellationToken: ct
            );

            if (result.ExitCode != 0)
            {
                _logger.LogError($"ollama pull {modelTag} failed: {result.StdErr}");
                return false;
            }

            return true;
        }

        public async Task<bool> DeleteModelAsync(
            string modelTag,
            IProgress<string>? progress = null,
            CancellationToken ct = default
        )
        {
            progress?.Report($"Deleting model '{modelTag}'...");

            var result = await ProcessRunner.RunAsync(
                "ollama",
                $"rm {modelTag}",
                progress,
                timeout: TimeSpan.FromSeconds(30),
                cancellationToken: ct
            );

            if (result.ExitCode != 0)
            {
                _logger.LogError($"ollama rm {modelTag} failed: {result.StdErr}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns a compact, human-readable snapshot of the currently loaded
        /// Ollama processes, equivalent to the useful part of `ollama ps`.
        /// This is intentionally best-effort: the chat UI should remain usable
        /// when Ollama is stopped or the CLI output changes slightly.
        /// </summary>
        public async Task<OllamaProcessSummary> GetProcessSummaryAsync(
            CancellationToken ct = default
        )
        {
            if (!IsInstalled() || !await IsReachableAsync(ct))
                return new OllamaProcessSummary(false, string.Empty);

            try
            {
                var result = await ProcessRunner.RunAsync(
                    "ollama",
                    "ps",
                    timeout: TimeSpan.FromSeconds(5),
                    cancellationToken: ct
                );

                if (result.ExitCode != 0)
                    return new OllamaProcessSummary(true, "idle");

                var rows = result
                    .StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();

                if (rows.Count <= 1)
                    return new OllamaProcessSummary(true, "idle");

                // Drop the table header and omit the model name from the UI.
                // The remaining fields are the useful runtime health signals:
                // size, processor placement, context and expiry.
                rows.RemoveAt(0);
                var compactRows = rows.Select(row =>
                {
                    var columns = Regex
                        .Split(row, @"\s{2,}")
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .ToArray();
                    return columns.Length > 1 ? string.Join(" • ", columns.Skip(1)) : "active";
                });

                string text = string.Join("  |  ", compactRows);
                if (text.Length > 260)
                    text = text[..257] + "...";

                return new OllamaProcessSummary(true, text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Unable to read Ollama process stats: {ex.Message}");
                return new OllamaProcessSummary(true, "stats unavailable");
            }
        }

        /// <summary>
        /// Lists models already pulled locally (`ollama list`), so the
        /// setup scan can tell the user "you already have X" instead of
        /// re-downloading something that's already on disk.
        /// </summary>
        public async Task<List<string>> ListInstalledModelsAsync(CancellationToken ct = default)
        {
            if (!IsInstalled() || !await IsReachableAsync(ct))
            {
                return new List<string>();
            }

            try
            {
                var result = await ProcessRunner.RunAsync(
                    "ollama",
                    "list",
                    timeout: TimeSpan.FromSeconds(10),
                    cancellationToken: ct
                );
                if (result.ExitCode != 0)
                {
                    return new List<string>();
                }

                var models = new List<string>();
                var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                // First line is the header ("NAME  ID  SIZE  MODIFIED"); the
                // model tag is always the first whitespace-separated token
                // on each subsequent line.
                for (int i = 1; i < lines.Length; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (trimmed.Length == 0)
                        continue;

                    int firstSpace = trimmed.IndexOf(' ');
                    string tag = firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        models.Add(tag);
                    }
                }

                return models;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to list installed Ollama models: {ex.Message}");
                return new List<string>();
            }
        }
    }
}
