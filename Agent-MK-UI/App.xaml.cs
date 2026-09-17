using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Orchestra.Core.Contracts;
using Orchestra.Core.Services;
using Orchestra.Infrastructure.Logging;

namespace Agent_MK_UI
{
    public partial class App : Application
    {
        public IServiceProvider? Services { get; private set; }
        private Window? m_window;

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // Top-level safety net to catch any silent CLR crash during startup
            try
            {
                var serviceCollection = new ServiceCollection();
                ConfigureServices(serviceCollection);
                Services = serviceCollection.BuildServiceProvider();

                var logger = Services.GetRequiredService<IAgentLogger>();
                logger.LogInfo("[Boot] DI Container built successfully.");

                var setupOrchestrator = Services.GetRequiredService<SetupOrchestrator>();
                var stateStore = Services.GetRequiredService<SetupStateStore>();

                logger.LogInfo("[Boot] Evaluating environment via SetupOrchestrator...");
                bool isEnvironmentIntact = setupOrchestrator.IsSetupComplete();
                logger.LogInfo($"[Boot] IsSetupComplete result: {isEnvironmentIntact}");

                if (!isEnvironmentIntact)
                {
                    logger.LogInfo("[Boot] Environment incomplete. Launching SetupWindow...");
                    var state = stateStore.Load();
                    bool isRepairMode =
                        state != null && !string.IsNullOrWhiteSpace(state.ChosenModel);
                    logger.LogInfo($"[Boot] Setup mode determined -> IsRepairMode: {isRepairMode}");

                    m_window = new SetupWindow(setupOrchestrator, isRepairMode);
                }
                else
                {
                    logger.LogInfo(
                        "[Boot] Environment fully intact. Attempting to start Python engine..."
                    );
                    if (TryStartPythonEngine())
                    {
                        logger.LogInfo("[Boot] Python engine started. Launching MainWindow...");
                        m_window = new MainWindow();
                        m_window.Closed += MainWindow_Closed;
                    }
                    else
                    {
                        logger.LogInfo(
                            "[Boot] Python engine failed to start. Forcing RepairMode SetupWindow..."
                        );
                        m_window = new SetupWindow(setupOrchestrator, isRepairMode: true);
                    }
                }

                logger.LogInfo("[Boot] Activating main application window.");
                m_window.Activate();
            }
            catch (Exception ex)
            {
                // If a fatal crash occurs before any window opens, force-log it
                System.Diagnostics.Debug.WriteLine($"FATAL BOOT EXCEPTION: {ex}");
                throw; // Re-throw to inspect in debugger if attached
            }
        }

        private string DeployRuntimeToAppData()
        {
            // Extract the embedded Python files into %LOCALAPPDATA%\AgentMK\AI_Runtime
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );
            string runtimeDir = Path.Combine(localAppData, "AgentMK", "AI_Runtime");

            // If headless.py is missing, extract the whole payload from the embedded zip
            if (!File.Exists(Path.Combine(runtimeDir, "headless.py")))
            {
                if (Directory.Exists(runtimeDir))
                {
                    Directory.Delete(runtimeDir, true);
                }
                Directory.CreateDirectory(runtimeDir);

                using Stream? stream = Assembly
                    .GetExecutingAssembly()
                    .GetManifestResourceStream("AIRuntime.zip");

                if (stream != null)
                {
                    using ZipArchive archive = new ZipArchive(stream);
                    archive.ExtractToDirectory(runtimeDir);
                }
            }

            return runtimeDir;
        }

        public bool TryStartPythonEngine()
        {
            var logger = Services!.GetRequiredService<IAgentLogger>();
            var pythonEngine = Services!.GetRequiredService<PythonEngine>();
            var stateStore = Services!.GetRequiredService<SetupStateStore>();

            try
            {
                logger.LogInfo("Bootstrapping system: Launching Python Engine...");

                // Deploy/Verify the files in AppData before trying to launch
                string runtimeDir = DeployRuntimeToAppData();
                string scriptPath = Path.Combine(runtimeDir, "headless.py");
                logger.LogInfo($"[Engine] Expected script path: {scriptPath}");

                var state = stateStore.Load();
                string? pythonExe = state.VenvPythonPath;
                logger.LogInfo($"[Engine] Configured venv python path: {pythonExe ?? "(null)"}");

                // Validate venv directory and pyvenv.cfg existence
                string? venvDir = !string.IsNullOrEmpty(pythonExe)
                    ? Path.GetDirectoryName(Path.GetDirectoryName(pythonExe))
                    : null;
                bool isVenvValid =
                    !string.IsNullOrEmpty(venvDir)
                    && File.Exists(Path.Combine(venvDir, "pyvenv.cfg"));

                logger.LogInfo(
                    $"[Engine] Validation checks -> ScriptExists: {File.Exists(scriptPath)}, PythonExeExists: {!string.IsNullOrEmpty(pythonExe) && File.Exists(pythonExe)}, VenvConfigValid: {isVenvValid}"
                );

                if (
                    !File.Exists(scriptPath)
                    || string.IsNullOrWhiteSpace(pythonExe)
                    || !File.Exists(pythonExe)
                    || !isVenvValid
                )
                {
                    logger.LogError(
                        "CRITICAL: Python script or virtual environment configuration (pyvenv.cfg) is missing or corrupted."
                    );
                    return false;
                }

                logger.LogInfo("[Engine] Calling pythonEngine.StartProcess...");
                pythonEngine.StartProcess(pythonExe, scriptPath);

                // Give the OS a tiny fraction of a second to surface immediate startup crashes
                Thread.Sleep(200);

                bool isRunning = pythonEngine.IsRunning;
                logger.LogInfo($"[Engine] Process start evaluation -> IsRunning: {isRunning}");

                if (!isRunning)
                {
                    logger.LogError(
                        "CRITICAL: Python engine process died immediately after StartProcess."
                    );
                    return false;
                }

                logger.LogInfo("[Engine] Python engine running successfully.");
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    $"Critical failure during Python engine initialization: {ex.Message} | StackTrace: {ex.StackTrace}"
                );
                return false;
            }
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IAgentLogger, LocalFileLogger>();
            services.AddSingleton<System.Net.Http.HttpClient>();

            services.AddSingleton<SetupStateStore>();
            services.AddSingleton<HardwareDetector>();
            services.AddSingleton<ModelRecommender>();
            services.AddSingleton<PythonEnvironmentManager>();
            services.AddSingleton<OllamaManager>();
            services.AddSingleton<SetupOrchestrator>();

            services.AddSingleton<PythonEngine>();
            services.AddTransient<AgentOrchestrator>();
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            if (Services?.GetService<PythonEngine>() is PythonEngine engine)
            {
                engine.Dispose();
            }
        }
    }
}