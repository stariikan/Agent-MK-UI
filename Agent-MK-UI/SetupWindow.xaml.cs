using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Orchestra.Core.Models;
using Orchestra.Core.Services;

namespace Agent_MK_UI
{
    public sealed partial class SetupWindow : Window
    {
        private readonly SetupOrchestrator _orchestrator;
        private readonly bool _isRepairMode;

        private bool _requiresModelPull;
        private CancellationTokenSource? _cts;
        private SystemScanResult? _scan;
        private ModelRecommendation? _recommendation;
        private bool _running;
        private Microsoft.UI.Windowing.AppWindow? _appWindow;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logBuffer = new();
        private readonly System.Collections.Generic.List<string> _recentLogs = new(20);
        private DispatcherTimer? _logFlushTimer;

        public SetupWindow(SetupOrchestrator orchestrator, bool isRepairMode = false)
        {
            this.InitializeComponent();
            _logFlushTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100), // Flush 10 times a second
            };
            _logFlushTimer.Tick += (s, e) => FlushLogBuffer();
            _logFlushTimer.Start();
            this.Title = isRepairMode ? "Agent-MK Repair" : "Agent-MK Setup";
            this.ExtendsContentIntoTitleBar = true;

            //  Dynamically resolve the absolute path to the icon for the setup window
            string iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "icon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                this.AppWindow.SetIcon(iconPath);
            }

            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _isRepairMode = isRepairMode;

            var rootGrid = (Grid)this.Content;
            rootGrid.Loaded += SetupWindow_Loaded;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            _appWindow.Closing += AppWindow_Closing;
        }

        private async void SetupWindow_Loaded(object sender, RoutedEventArgs e)
        {
            AddScanRow("Scanning system...", ScanStatus.Pending);

            try
            {
                _scan = await _orchestrator.ScanSystemAsync();
                DisplayScanResult(_scan);

                // ARCHITECTURAL FIX: Dynamic Repair Inference
                // If Python/Venv is broken, but Ollama and models already exist,
                // we should auto-switch to Repair mode even if the window wasn't explicitly launched that way.
                bool pythonIsBroken =
                    !_scan.PythonFound || !_scan.VenvExists || !_scan.DependenciesInstalled;
                bool ollamaIsReady = _scan.OllamaRunning && _scan.InstalledModels.Count > 0;

                bool effectiveRepairMode = _isRepairMode || (pythonIsBroken && ollamaIsReady);

                _requiresModelPull = !effectiveRepairMode || _scan.InstalledModels.Count == 0;

                if (!_requiresModelPull)
                {
                    // -- STRICT REPAIR MODE UI --
                    SetupTitleText.Text = "Repairing Environment";
                    SetupSubtitleText.Text =
                        "Your settings and models are safe, but the background runtime is missing or corrupted. We'll rebuild it now.";

                    HardwarePanelBorder.Visibility = Visibility.Collapsed;
                    ModelSelectionBorder.Visibility = Visibility.Collapsed;

                    StartButton.Content = "Start Repair";
                    SetupProgressBar.Maximum = 2; // Only 2 steps: Python & Ollama
                }
                else
                {
                    // -- FULL SETUP UI --
                    if (effectiveRepairMode)
                    {
                        SetupTitleText.Text = "Repair & Download";
                        SetupSubtitleText.Text =
                            "The runtime is corrupted AND no models were found. We need to rebuild the environment and pull a model.";
                    }

                    ModelComboBox.ItemsSource = PopularModelCatalog
                        .Models.Select(m => m.OllamaTag)
                        .ToList();
                    SetupProgressBar.Maximum = 4;

                    _recommendation = _orchestrator.GetRecommendation(_scan.Hardware);
                    DisplayRecommendation(_recommendation);

                    ModelComboBox.Text = _recommendation.RecommendedModel;
                    UpdateModelExplanation(_recommendation.RecommendedModel);
                }
            }
            catch (Exception ex)
            {
                Fail($"Initial scan failed: {ex.Message}");
                _requiresModelPull = true;
                ModelComboBox.ItemsSource = PopularModelCatalog
                    .Models.Select(m => m.OllamaTag)
                    .ToList();
                ModelComboBox.Text = "qwen3:14bq4_K_M"; // Updated to match your actual default
                UpdateModelExplanation("qwen3:14bq4_K_M");
            }
        }

        // ------------------------------------------------------------
        // Dynamic Model UI Logic (Replaces old PopulatePopularModels)
        // ------------------------------------------------------------

        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string? selectedTag = ModelComboBox.SelectedItem?.ToString();
            UpdateModelExplanation(selectedTag);
        }

        private void ModelComboBox_TextSubmitted(
            ComboBox sender,
            ComboBoxTextSubmittedEventArgs args
        )
        {
            UpdateModelExplanation(args.Text);
        }

        private void UpdateModelExplanation(string? modelTag)
        {
            if (string.IsNullOrWhiteSpace(modelTag))
            {
                ModelExplanationText.Visibility = Visibility.Collapsed;
                return;
            }

            var knownModel = PopularModelCatalog.Models.FirstOrDefault(m =>
                m.OllamaTag == modelTag.Trim()
            );

            if (knownModel != null)
            {
                // It's a catalog model: Show standard specs
                ModelExplanationText.Text =
                    $"Size: {knownModel.ParamSize} | VRAM needed: ~{knownModel.ApproxVramGb}GB\n{knownModel.Description}";
                ModelExplanationText.Foreground = (Brush)
                    Application.Current.Resources["TextFillColorSecondaryBrush"];
                ModelExplanationText.Visibility = Visibility.Visible;
            }
            else
            {
                // It's a custom typed model: Show typo warning
                ModelExplanationText.Text =
                    "Custom model tag entered. Please ensure it is written without typos, as this exact tag will be pulled from the Ollama registry.";
                ModelExplanationText.Foreground = (Brush)
                    Application.Current.Resources["SystemFillColorCautionBrush"]; // Yellow warning color
                ModelExplanationText.Visibility = Visibility.Visible;
            }
        }

        // ------------------------------------------------------------
        // System Scan Checklist UI
        // ------------------------------------------------------------

        private enum ScanStatus
        {
            Ok,
            Missing,
            Pending,
            Fail,
        }

        private void DisplayScanResult(SystemScanResult scan)
        {
            ScanChecklistPanel.Children.Clear();

            AddScanRow(
                scan.PythonFound
                    ? $"Python found ({scan.PythonVersion ?? scan.PythonExePath})"
                    : "Python 3.10+ not found -- will install",
                scan.PythonFound ? ScanStatus.Ok : ScanStatus.Missing
            );

            if (scan.PythonFound)
            {
                AddScanRow(
                    scan.VenvExists
                        ? "Virtual environment already exists"
                        : "Virtual environment not created yet",
                    scan.VenvExists ? ScanStatus.Ok : ScanStatus.Missing
                );

                if (scan.VenvExists)
                {
                    AddScanRow(
                        scan.DependenciesInstalled
                            ? "Python dependencies already installed"
                            : "Python dependencies not installed yet",
                        scan.DependenciesInstalled ? ScanStatus.Ok : ScanStatus.Missing
                    );
                }
            }

            AddScanRow(
                scan.OllamaInstalled
                    ? "Ollama is installed"
                    : "Ollama not installed -- will install",
                scan.OllamaInstalled ? ScanStatus.Ok : ScanStatus.Missing
            );

            if (scan.OllamaInstalled)
            {
                AddScanRow(
                    scan.OllamaRunning ? "Ollama is running" : "Ollama is not currently running",
                    scan.OllamaRunning ? ScanStatus.Ok : ScanStatus.Missing
                );
            }

            if (scan.InstalledModels.Count > 0)
            {
                AddScanRow(
                    $"Models already downloaded: {string.Join(", ", scan.InstalledModels)}",
                    ScanStatus.Ok
                );
            }
            else
            {
                AddScanRow("No models downloaded -- will require download", ScanStatus.Missing);
            }

            if (scan.IsFullyReady && !_requiresModelPull)
            {
                AddScanRow(
                    "Everything required is already set up -- setup will only need to confirm/pull the model you choose below.",
                    ScanStatus.Ok
                );
            }
        }

        private void AddScanRow(string text, ScanStatus status)
        {
            string prefix = status switch
            {
                ScanStatus.Ok => "\u2714 ",
                ScanStatus.Missing => "\u2716 ",
                ScanStatus.Fail => "\u2716 ",
                _ => "\u2026 ",
            };

            var color = status switch
            {
                ScanStatus.Ok => new SolidColorBrush(Microsoft.UI.Colors.ForestGreen),
                ScanStatus.Missing => new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
                ScanStatus.Fail => new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
                _ => new SolidColorBrush(Microsoft.UI.Colors.Gray),
            };

            ScanChecklistPanel.Children.Add(
                new TextBlock
                {
                    Text = prefix + text,
                    Foreground = color,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                }
            );
        }

        private async Task RefreshScanUiAsync()
        {
            try
            {
                _scan = await _orchestrator.ScanSystemAsync();
                DisplayScanResult(_scan);
            }
            catch
            { /* Non-fatal */
            }
        }

        private void DisplayRecommendation(ModelRecommendation rec)
        {
            string vram = rec.Hardware.VramGb.HasValue
                ? $"{rec.Hardware.VramGb:F1} GB{(rec.Hardware.VramIsApproximate ? " (approximate)" : "")}"
                : "unknown";

            HardwareSummaryText.Text =
                $"RAM: {rec.Hardware.RamGb:F1} GB   |   GPU: {rec.Hardware.GpuName ?? "none detected"} "
                + $"({rec.Hardware.GpuVendor})   |   VRAM: {vram}\n"
                + $"Recommended model: {rec.RecommendedModel}  [{rec.RecommendedTier}, ~{rec.ApproxDiskGb:F1} GB on disk]";

            RecommendationReasonText.Text = rec.Reason;
            RecommendationNotesText.Text = rec.Notes.Count > 0 ? string.Join(" ", rec.Notes) : "";
        }

        // ------------------------------------------------------------
        // Setup Run Execution
        // ------------------------------------------------------------

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_running)
                return;

            string chosenModel = "";

            if (_requiresModelPull)
            {
                chosenModel = ModelComboBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(chosenModel))
                {
                    AppendLog("[error] Enter a model tag first (e.g. qwen3-coder:8b).");
                    return;
                }
            }

            _running = true;
            StartButton.IsEnabled = false;
            ModelComboBox.IsEnabled = false;
            ContinueButton.IsEnabled = false;
            SetupProgressBar.Value = 0;
            StatusText.Text = "Running...";

            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(AppendLog);

            try
            {
                AppendLog($"=== Step 1/{SetupProgressBar.Maximum}: Python environment ===");
                bool pythonOk = await _orchestrator.EnsurePythonEnvironmentAsync(
                    progress,
                    _cts.Token
                );
                if (!pythonOk)
                {
                    Fail("Python environment setup failed. See log above.");
                    return;
                }
                SetupProgressBar.Value = 1;
                await RefreshScanUiAsync();

                AppendLog($"=== Step 2/{SetupProgressBar.Maximum}: Ollama ===");
                bool ollamaOk = await _orchestrator.EnsureOllamaAsync(progress, _cts.Token);
                if (!ollamaOk)
                {
                    Fail("Ollama setup failed. See log above.");
                    return;
                }
                SetupProgressBar.Value = 2;
                await RefreshScanUiAsync();

                if (_requiresModelPull)
                {
                    AppendLog($"=== Step 3/4: Downloading model '{chosenModel}' ===");
                    bool pullOk = await _orchestrator.PullModelAsync(
                        chosenModel,
                        progress,
                        _cts.Token
                    );
                    if (!pullOk)
                    {
                        Fail(
                            $"Failed to pull model '{chosenModel}'. Check the model tag and your connection."
                        );
                        return;
                    }
                    SetupProgressBar.Value = 3;
                    await RefreshScanUiAsync();

                    AppendLog("=== Step 4/4: Finalizing ===");
                    _orchestrator.SetChosenModel(chosenModel);
                    SetupProgressBar.Value = 4;

                    AppendLog("Setup complete! Click 'Continue to Chat' to start.");
                }
                else
                {
                    if (
                        string.IsNullOrWhiteSpace(_orchestrator.LoadState().ChosenModel)
                        && _scan?.InstalledModels.Count > 0
                    )
                    {
                        _orchestrator.SetChosenModel(_scan.InstalledModels.First());
                    }
                    AppendLog("=== Repair Complete! ===");
                    AppendLog(
                        "The background engine has been successfully rebuilt. Click 'Continue to Chat' to start."
                    );
                }

                StatusText.Text = "Done.";
                ContinueButton.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                AppendLog("Operation cancelled.");
                StatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                Fail($"Unexpected error: {ex.Message}");
            }
            finally
            {
                _running = false;
                StartButton.IsEnabled = true;
                ModelComboBox.IsEnabled = true;
            }
        }

        private void Fail(string message)
        {
            AppendLog($"[error] {message}");
            StatusText.Text = "Failed -- fix the issue above and click Start to retry.";
        }

        private void AppendLog(string line)
        {
            // Thread-safe enqueue. Zero UI thread contention.
            _logBuffer.Enqueue(line);
        }

        private void FlushLogBuffer()
        {
            if (_logBuffer.IsEmpty)
                return;

            bool newLinesAdded = false;
            while (_logBuffer.TryDequeue(out var line))
            {
                _recentLogs.Add(line);
                newLinesAdded = true;

                // Keep only the last 20 lines
                if (_recentLogs.Count > 20)
                {
                    _recentLogs.RemoveAt(0);
                }
            }

            if (newLinesAdded)
            {
                LogTextBox.Text = string.Join(Environment.NewLine, _recentLogs);
                LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
            }
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;

            if (app.TryStartPythonEngine())
            {
                var mainWindow = new MainWindow();

                mainWindow.Closed += (s, args) =>
                {
                    if (app.Services?.GetService<PythonEngine>() is PythonEngine engine)
                    {
                        engine.Dispose();
                    }
                };

                mainWindow.Activate();
                _logFlushTimer?.Stop();
                this.Close();
            }
            else
            {
                Fail("Failed to start the background Python engine. Check the setup logs.");
            }
        }

        private async void AppWindow_Closing(
            Microsoft.UI.Windowing.AppWindow sender,
            Microsoft.UI.Windowing.AppWindowClosingEventArgs args
        )
        {
            if (_running)
            {
                args.Cancel = true;

                var dialog = new ContentDialog
                {
                    Title = "Process in progress",
                    Content =
                        "A process is currently running. Closing now may leave the environment partially configured. Close anyway?",
                    PrimaryButtonText = "Yes",
                    CloseButtonText = "No",
                    XamlRoot = this.Content.XamlRoot,
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    _cts?.Cancel();
                    _logFlushTimer?.Stop();
                    sender.Closing -= AppWindow_Closing;
                    this.Close();
                }
            }
        }
    }
}
