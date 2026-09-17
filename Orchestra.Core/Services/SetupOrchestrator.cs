using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Orchestra.Core.Contracts;
using Orchestra.Core.Models;

namespace Orchestra.Core.Services
{
    public class SetupOrchestrator
    {
        private readonly IAgentLogger _logger;
        private readonly HardwareDetector _hardwareDetector;
        private readonly ModelRecommender _modelRecommender;
        private readonly PythonEnvironmentManager _pythonManager;
        private readonly OllamaManager _ollamaManager;
        private readonly SetupStateStore _stateStore;

        public string AiRuntimeDir { get; }
        public string VenvRootDir { get; }

        public SetupOrchestrator(
            IAgentLogger logger,
            HardwareDetector hardwareDetector,
            ModelRecommender modelRecommender,
            PythonEnvironmentManager pythonManager,
            OllamaManager ollamaManager,
            SetupStateStore stateStore
        )
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hardwareDetector = hardwareDetector ?? throw new ArgumentNullException(nameof(hardwareDetector));
            _modelRecommender = modelRecommender ?? throw new ArgumentNullException(nameof(modelRecommender));
            _pythonManager = pythonManager ?? throw new ArgumentNullException(nameof(pythonManager));
            _ollamaManager = ollamaManager ?? throw new ArgumentNullException(nameof(ollamaManager));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // UPDATED: Both paths now safely target the AppData extraction directories
            AiRuntimeDir = Path.Combine(localAppData, "AgentMK", "AI_Runtime");
            VenvRootDir = Path.Combine(localAppData, "AgentMK", "venv");
        }

        public bool IsSetupComplete() => _stateStore.IsSetupComplete();

        public SetupState LoadState() => _stateStore.Load();

        public Task<ModelRecommendation> DetectHardwareAndRecommendAsync(
            double? overrideRamGb = null,
            double? overrideVramGb = null
        )
        {
            return Task.Run(() =>
            {
                var profile = _hardwareDetector.Detect();
                return _modelRecommender.Recommend(profile, overrideRamGb, overrideVramGb);
            });
        }

        public ModelRecommendation GetRecommendation(
            HardwareProfile hardware,
            double? overrideRamGb = null,
            double? overrideVramGb = null
        ) => _modelRecommender.Recommend(hardware, overrideRamGb, overrideVramGb);

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
                result.PythonVersion = await Task.Run(
                    () => _pythonManager.GetPythonVersionString(systemPython),
                    ct
                );
            }

            result.VenvExists = _pythonManager.VenvExists(VenvRootDir);
            result.DependenciesInstalled =
                result.VenvExists && _pythonManager.AreDependenciesInstalled(VenvRootDir);

            result.OllamaInstalled = _ollamaManager.IsInstalled();
            result.OllamaRunning =
                result.OllamaInstalled && await _ollamaManager.IsReachableAsync(ct);
            result.InstalledModels = result.OllamaRunning
                ? await _ollamaManager.ListInstalledModelsAsync(ct)
                : new List<string>();

            return result;
        }

        public async Task<bool> EnsurePythonEnvironmentAsync(
            IProgress<string> progress,
            CancellationToken ct = default
        )
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

            bool venvOk = await _pythonManager.CreateVenvAsync(
                systemPython,
                VenvRootDir,
                progress,
                ct
            );
            if (!venvOk)
                return false;

            bool depsOk;
            if (_pythonManager.AreDependenciesInstalled(VenvRootDir))
            {
                progress.Report("Python dependencies already installed, skipping.");
                depsOk = true;
            }
            else
            {
                string requirementsPath = Path.Combine(AiRuntimeDir, "requirements.txt");
                depsOk = await _pythonManager.InstallDependenciesAsync(
                    VenvRootDir,
                    requirementsPath,
                    progress,
                    ct
                );
            }
            if (!depsOk)
                return false;

            var state = _stateStore.Load();
            state.MarkStepComplete("python_installed");
            state.MarkStepComplete("venv_created");
            state.MarkStepComplete("deps_installed");
            state.VenvPythonPath = _pythonManager.GetVenvPythonPath(VenvRootDir);
            _stateStore.Save(state);

            return true;
        }

        public async Task<bool> EnsureOllamaAsync(
            IProgress<string> progress,
            CancellationToken ct = default
        )
        {
            bool installed = await _ollamaManager.InstallAsync(progress, ct);
            if (!installed)
            {
                progress.Report(
                    "Ollama installation could not be confirmed. You can install it "
                        + "manually from https://ollama.com/download and re-run setup."
                );
                return false;
            }

            bool running = await _ollamaManager.EnsureRunningAsync(progress, ct);
            if (!running)
                return false;

            var state = _stateStore.Load();
            state.MarkStepComplete("ollama_installed");
            state.MarkStepComplete("ollama_running");
            _stateStore.Save(state);

            return true;
        }

        public async Task<bool> PullModelAsync(
            string modelTag,
            IProgress<string> progress,
            CancellationToken ct = default
        )
        {
            bool pulled = await _ollamaManager.PullModelAsync(modelTag, progress, ct);
            if (!pulled)
                return false;

            var state = _stateStore.Load();
            state.ChosenModel = modelTag;
            state.MarkStepComplete($"model_pulled_{modelTag}");
            _stateStore.Save(state);

            return true;
        }

        public void SetChosenModel(string modelTag)
        {
            var state = _stateStore.Load();
            state.ChosenModel = modelTag;
            _stateStore.Save(state);
        }

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

        public void ResetEnvironment()
        {
            string venvDir = Path.Combine(VenvRootDir, ".venv");
            if (Directory.Exists(venvDir))
            {
                Directory.Delete(venvDir, recursive: true);
            }

            _stateStore.Reset();
        }

        public async Task WipeEverythingAsync(
            IProgress<string> progress,
            CancellationToken ct = default
        )
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