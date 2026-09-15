using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Orchestra.Core.Contracts;
using Orchestra.Core.Models;

namespace Orchestra.Core.Services
{
    /// <summary>
    /// Coordinates the whole first-run setup flow for the in-app setup
    /// wizard (SetupWindow): Python environment, hardware detection +
    /// model recommendation, Ollama install/start, and model pull.
    /// Each phase is a separate method so the UI can show the hardware
    /// recommendation and let the user accept/override the model before
    /// the (potentially large) model download starts.
    /// </summary>
    public class SetupOrchestrator
    {
        private readonly IAgentLogger _logger;
        private readonly HardwareDetector _hardwareDetector;
        private readonly ModelRecommender _modelRecommender;
        private readonly PythonEnvironmentManager _pythonManager;
        private readonly OllamaManager _ollamaManager;
        private readonly SetupStateStore _stateStore;

        public string AiRuntimeDir { get; }

        /// <summary>
        /// Stable location for the Python venv, deliberately OUTSIDE
        /// AiRuntimeDir. AiRuntimeDir lives under the build output
        /// (bin\Debug\...) and gets wiped by any `dotnet clean`/rebuild --
        /// putting the venv there meant a rebuild silently destroyed a
        /// working Python environment while the persisted setup state in
        /// %LOCALAPPDATA% still claimed setup was complete, pointing at a
        /// now-deleted interpreter. That's what caused the reported crash
        /// ("Configured Python interpreter not found ... Python process is
        /// not running"). The venv now survives rebuilds; only the
        /// (cheap-to-recreate) scripts in AiRuntimeDir get wiped.
        /// </summary>
        public string VenvRootDir { get; }

        public SetupOrchestrator(
            IAgentLogger logger,
            HardwareDetector hardwareDetector,
            ModelRecommender modelRecommender,
            PythonEnvironmentManager pythonManager,
            OllamaManager ollamaManager,
            SetupStateStore stateStore)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hardwareDetector = hardwareDetector ?? throw new ArgumentNullException(nameof(hardwareDetector));
            _modelRecommender = modelRecommender ?? throw new ArgumentNullException(nameof(modelRecommender));
            _pythonManager = pythonManager ?? throw new ArgumentNullException(nameof(pythonManager));
            _ollamaManager = ollamaManager ?? throw new ArgumentNullException(nameof(ollamaManager));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));

            AiRuntimeDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AI_Runtime");
            VenvRootDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AgentMK", "venv");
        }

        public bool IsSetupComplete() => _stateStore.IsSetupComplete();

        public SetupState LoadState() => _stateStore.Load();

        /// <summary>Hardware detection is cheap and synchronous-ish; run it off the calling thread anyway to keep the UI responsive.</summary>
        public Task<ModelRecommendation> DetectHardwareAndRecommendAsync(double? overrideRamGb = null, double? overrideVramGb = null)
        {
            return Task.Run(() =>
            {
                var profile = _hardwareDetector.Detect();
                return _modelRecommender.Recommend(profile, overrideRamGb, overrideVramGb);
            });
        }

        /// <summary>Synchronous wrapper so the UI can re-derive a recommendation from a hardware profile it already has (e.g. from ScanSystemAsync) without detecting hardware twice.</summary>
        public ModelRecommendation GetRecommendation(HardwareProfile hardware, double? overrideRamGb = null, double? overrideVramGb = null)
            => _modelRecommender.Recommend(hardware, overrideRamGb, overrideVramGb);

        /// <summary>
        /// Pre-flight check: looks at what's already on the machine
        /// *before* touching anything, so the wizard can tell the user
        /// what it actually needs to do instead of just running every
        /// install step blind. Safe to call repeatedly (read-only).
        /// </summary>
        public async Task<SystemScanResult> ScanSystemAsync(CancellationToken ct = default)
        {
            var result = new SystemScanResult
            {
                Hardware = await Task.Run(() => _hardwareDetector.Detect(), ct),
            };

            string? systemPython = await Task.Run(() => _pythonManager.FindSystemPython(), ct);
            result.PythonFound = systemPython != null;
            result.PythonExePath = systemPython;
            if (systemPython != null)
            {
                result.PythonVersion = await Task.Run(() => _pythonManager.GetPythonVersionString(systemPython), ct);
            }

            result.VenvExists = _pythonManager.VenvExists(VenvRootDir);
            result.DependenciesInstalled = result.VenvExists && _pythonManager.AreDependenciesInstalled(VenvRootDir);

            result.OllamaInstalled = _ollamaManager.IsInstalled();
            result.OllamaRunning = result.OllamaInstalled && await _ollamaManager.IsReachableAsync(ct);
            result.InstalledModels = result.OllamaRunning
                ? await _ollamaManager.ListInstalledModelsAsync(ct)
                : new List<string>();

            return result;
        }

        public async Task<bool> EnsurePythonEnvironmentAsync(IProgress<string> progress, CancellationToken ct = default)
        {
            if (!Directory.Exists(AiRuntimeDir))
            {
                progress.Report($"AI_Runtime directory not found at {AiRuntimeDir}.");
                return false;
            }

            string? systemPython = await Task.Run(() => _pythonManager.FindSystemPython(), ct);

            if (systemPython == null)
            {
                progress.Report("No usable Python 3.10+ found on this system.");
                bool installed = await _pythonManager.InstallPythonViaWingetAsync(progress, ct);
                if (!installed)
                {
                    return false;
                }
                systemPython = await Task.Run(() => _pythonManager.FindSystemPython(), ct);
            }

            if (systemPython == null)
            {
                progress.Report("Still no usable Python interpreter after install attempt.");
                return false;
            }

            progress.Report($"Using system Python: {systemPython}");

            bool venvOk = await _pythonManager.CreateVenvAsync(systemPython, VenvRootDir, progress, ct);
            if (!venvOk) return false;

            bool depsOk;
            if (_pythonManager.AreDependenciesInstalled(VenvRootDir))
            {
                progress.Report("Python dependencies already installed, skipping.");
                depsOk = true;
            }
            else
            {
                string requirementsPath = Path.Combine(AiRuntimeDir, "requirements.txt");
                depsOk = await _pythonManager.InstallDependenciesAsync(VenvRootDir, requirementsPath, progress, ct);
            }
            if (!depsOk) return false;

            var state = _stateStore.Load();
            state.MarkStepComplete("python_installed");
            state.MarkStepComplete("venv_created");
            state.MarkStepComplete("deps_installed");
            state.VenvPythonPath = _pythonManager.GetVenvPythonPath(VenvRootDir);
            _stateStore.Save(state);

            return true;
        }

        public async Task<bool> EnsureOllamaAsync(IProgress<string> progress, CancellationToken ct = default)
        {
            bool installed = await _ollamaManager.InstallAsync(progress, ct);
            if (!installed)
            {
                progress.Report("Ollama installation could not be confirmed. You can install it " +
                                 "manually from https://ollama.com/download and re-run setup.");
                return false;
            }

            bool running = await _ollamaManager.EnsureRunningAsync(progress, ct);
            if (!running) return false;

            var state = _stateStore.Load();
            state.MarkStepComplete("ollama_installed");
            state.MarkStepComplete("ollama_running");
            _stateStore.Save(state);

            return true;
        }

        public async Task<bool> PullModelAsync(string modelTag, IProgress<string> progress, CancellationToken ct = default)
        {
            bool pulled = await _ollamaManager.PullModelAsync(modelTag, progress, ct);
            if (!pulled) return false;

            var state = _stateStore.Load();
            state.ChosenModel = modelTag;
            state.MarkStepComplete($"model_pulled_{modelTag}");
            _stateStore.Save(state);

            return true;
        }

        /// <summary>Switch the active model without re-running the rest of setup (used later from within the app, not just first-run).</summary>
        public void SetChosenModel(string modelTag)
        {
            var state = _stateStore.Load();
            state.ChosenModel = modelTag;
            _stateStore.Save(state);
        }

        /// <summary>Persist the user-selected agent behavior preset.</summary>
        public void SetAgentProfile(string profile)
        {
            profile = (profile ?? "auto").Trim().ToLowerInvariant();
            if (profile is not ("auto" or "fast" or "deep"))
            {
                profile = "auto";
            }

            var state = _stateStore.Load();
            state.AgentProfile = profile;
            _stateStore.Save(state);
        }

        /// <summary>
        /// Used by the Settings window's "Reset environment" action.
        /// Deletes the Python venv and clears saved setup state so the
        /// wizard runs again on next launch. Deliberately does NOT touch
        /// Ollama or any pulled models -- those are managed separately
        /// (see OllamaManager.DeleteModelAsync) since a user may still
        /// want them for other tools.
        /// </summary>
        public void ResetEnvironment()
        {
            string venvDir = Path.Combine(VenvRootDir, ".venv");
            if (Directory.Exists(venvDir))
            {
                Directory.Delete(venvDir, recursive: true);
            }

            _stateStore.Reset();
        }

        /// <summary>
        /// The comprehensive teardown: deletes every locally-pulled Ollama
        /// model, then does everything ResetEnvironment does (venv + saved
        /// state). Deliberately does NOT touch chat history/projects
        /// (chats.sqlite3) -- that's user data, not "setup". Ollama itself
        /// also isn't uninstalled (no safe way to do that from here); this
        /// clears everything the setup wizard put in place.
        /// </summary>
        public async Task WipeEverythingAsync(IProgress<string> progress, CancellationToken ct = default)
        {
            var models = await _ollamaManager.ListInstalledModelsAsync(ct);

            foreach (var model in models)
            {
                progress.Report($"Deleting model '{model}'...");
                await _ollamaManager.DeleteModelAsync(model, progress, ct);
            }

            progress.Report("Removing Python virtual environment and setup state...");
            ResetEnvironment();
            progress.Report("Done. Restart the app to run setup again.");
        }
    }
}


