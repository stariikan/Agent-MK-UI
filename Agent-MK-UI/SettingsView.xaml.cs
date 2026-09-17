using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orchestra.Core.Models;
using Orchestra.Core.Services;

namespace Agent_MK_UI
{
    // 1. Inherit from UserControl instead of Window
    public sealed partial class SettingsView : UserControl
    {
        private readonly SetupOrchestrator _orchestrator;
        private readonly OllamaManager _ollamaManager;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logBuffer = new();
        private readonly System.Collections.Generic.List<string> _recentLogs = new(20);
        private DispatcherTimer? _logFlushTimer;
        private string _appDataFolder = string.Empty;
        // 2. Architectural Event for Decoupling
        // This allows the UserControl to shout "Close me!" without knowing about MainWindow.
        public event EventHandler? CloseRequested;

        public SettingsView(SetupOrchestrator orchestrator, OllamaManager ollamaManager)
        {
            this.InitializeComponent();
            _logFlushTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100), // Flush 10 times a second
            };
            _logFlushTimer.Tick += (s, e) => FlushLogBuffer();
            _logFlushTimer.Start();

            // 3. ARCHITECTURAL FIX: Use Unloaded instead of Closed for UserControls
            this.Unloaded += (s, args) => _logFlushTimer?.Stop();

            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _ollamaManager =
                ollamaManager ?? throw new ArgumentNullException(nameof(ollamaManager));

            NewModelComboBox.ItemsSource = PopularModelCatalog
                .Models.Select(m => m.OllamaTag)
                .ToList();
            AgentProfileComboBox.ItemsSource = new[] { "Auto (recommended)", "Fast", "Deep" };

            // 4. ARCHITECTURAL FIX: UserControl has its own Loaded event directly
            this.Loaded += SettingsView_Loaded;
        }

        private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            var state = _orchestrator.LoadState();
            ActiveModelText.Text = $"Currently active: {state.ChosenModel ?? "(none set)"}";
            AgentProfileComboBox.SelectedItem = ProfileDisplay(state.AgentProfile ?? "auto");

            await RefreshInstalledModelsAsync();
            InstalledModelsComboBox.SelectedItem = state.ChosenModel;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _appDataFolder = System.IO.Path.Combine(localAppData, "AgentMK");
            string runtimeDir = System.IO.Path.Combine(_appDataFolder, "AI_Runtime");

            AppDataPathText.Text = $"Python Environment: {runtimeDir}\nDatabase: {System.IO.Path.Combine(_appDataFolder, "chats.sqlite3")}";
        }

        // 5. Fire the event when the Back button is clicked
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private async Task RefreshInstalledModelsAsync()
        {
            var installed = await _ollamaManager.ListInstalledModelsAsync();
            InstalledModelsComboBox.ItemsSource = installed;
        }

        private void NewModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string? selectedTag = NewModelComboBox.SelectedItem?.ToString();
            var knownModel = PopularModelCatalog.Models.FirstOrDefault(m =>
                m.OllamaTag == selectedTag
            );

            if (knownModel != null)
            {
                NewModelExplanationText.Text =
                    $"Size: {knownModel.ParamSize} | VRAM needed: ~{knownModel.ApproxVramGb}GB\n{knownModel.Description}";
                NewModelExplanationText.Visibility = Visibility.Visible;
            }
            else
            {
                NewModelExplanationText.Visibility = Visibility.Collapsed;
            }
        }

        // --- Helper Dialogs ---
        private async Task<bool> ShowConfirmationDialogAsync(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = "Yes",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot, // FIXED: UserControls access XamlRoot directly
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private async Task ShowMessageDialogAsync(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot, // FIXED: UserControls access XamlRoot directly
            };
            await dialog.ShowAsync();
        }

        // --- Button Handlers ---
        private static string ProfileDisplay(string profile) =>
            profile switch
            {
                "fast" => "Fast",
                "deep" => "Deep",
                _ => "Auto (recommended)",
            };

        private static string ProfileValue(string display) =>
            display switch
            {
                "Fast" => "fast",
                "Deep" => "deep",
                _ => "auto",
            };

        private void SetAgentProfileButton_Click(object sender, RoutedEventArgs e)
        {
            string profile = ProfileValue(
                AgentProfileComboBox.SelectedItem?.ToString() ?? "Auto (recommended)"
            );
            _orchestrator.SetAgentProfile(profile);
            AppendLog($"Agent behavior set to '{ProfileDisplay(profile)}'.");
        }

        private void SetActiveButton_Click(object sender, RoutedEventArgs e)
        {
            string? model = InstalledModelsComboBox.SelectedItem?.ToString();

            if (string.IsNullOrWhiteSpace(model))
            {
                AppendLog("[error] Select an installed model first.");
                return;
            }

            _orchestrator.SetChosenModel(model);
            ActiveModelText.Text = $"Currently active: {model}";
            AppendLog($"Active model set to '{model}'. New chats will default to this.");
        }

        private async void PullButton_Click(object sender, RoutedEventArgs e)
        {
            string model = NewModelComboBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                AppendLog("[error] Enter or select a model tag first.");
                return;
            }

            // Lock UI & Start Progress
            PullButton.IsEnabled = false;
            NewModelComboBox.IsEnabled = false;
            if (SettingsProgressBar != null)
                SettingsProgressBar.IsIndeterminate = true;

            var progress = new Progress<string>(AppendLog);

            try
            {
                bool ok = await _ollamaManager.PullModelAsync(model, progress);
                AppendLog(
                    ok ? $"Pulled '{model}' successfully." : $"[error] Failed to pull '{model}'."
                );
                await RefreshInstalledModelsAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"[error] {ex.Message}");
            }
            finally
            {
                // Unlock UI & Stop Progress
                if (SettingsProgressBar != null)
                    SettingsProgressBar.IsIndeterminate = false;
                PullButton.IsEnabled = true;
                NewModelComboBox.IsEnabled = true;
            }
        }

        private async void DeleteModelButton_Click(object sender, RoutedEventArgs e)
        {
            string? model = InstalledModelsComboBox.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(model))
            {
                AppendLog("[error] Select a model to delete first.");
                return;
            }

            bool confirmed = await ShowConfirmationDialogAsync(
                "Delete model",
                $"Are you sure you want to delete '{model}' from disk? This cannot be undone."
            );

            if (!confirmed)
                return;

            DeleteModelButton.IsEnabled = false;
            try
            {
                bool ok = await _ollamaManager.DeleteModelAsync(
                    model,
                    new Progress<string>(AppendLog)
                );
                AppendLog(ok ? $"Deleted '{model}'." : $"[error] Failed to delete '{model}'.");

                await RefreshInstalledModelsAsync();

                if (ActiveModelText.Text.Contains(model))
                {
                    ActiveModelText.Text = "Currently active: (none set)";
                }
            }
            finally
            {
                DeleteModelButton.IsEnabled = true;
            }
        }

        private async void ResetEnvironmentButton_Click(object sender, RoutedEventArgs e)
        {
            bool confirmed = await ShowConfirmationDialogAsync(
                "Reset App Environment",
                "This deletes Agent-MK's isolated Python environment (.venv) and your setup state.\n\n"
                    + "Your downloaded models, system Python, and the Ollama engine will NOT be touched. Continue?"
            );

            if (!confirmed)
                return;

            try
            {
                _orchestrator.ResetEnvironment();
                AppendLog("Environment reset. Restart the app to run setup again.");
                await ShowMessageDialogAsync("Done", "Environment reset. Please restart Agent-MK.");
            }
            catch (Exception ex)
            {
                AppendLog($"[error] Reset failed: {ex.Message}");
            }
        }

        private async void WipeEverythingButton_Click(object sender, RoutedEventArgs e)
        {
            bool confirmed = await ShowConfirmationDialogAsync(
                "Factory Reset Agent-MK",
                "This will delete:\n"
                    + "1. All models downloaded via Agent-MK.\n"
                    + "2. The Agent-MK isolated Python environment.\n"
                    + "3. All setup state.\n\n"
                    + "Note: This does NOT uninstall the Ollama engine or system Python from your PC. Continue?"
            );

            if (!confirmed)
                return;

            // Lock UI & Start Progress
            WipeEverythingButton.IsEnabled = false;
            if (SettingsProgressBar != null)
                SettingsProgressBar.IsIndeterminate = true;

            try
            {
                await _orchestrator.WipeEverythingAsync(new Progress<string>(AppendLog));
                await RefreshInstalledModelsAsync();
                await ShowMessageDialogAsync(
                    "Done",
                    "Agent-MK has been factory reset. Please restart the application."
                );
            }
            catch (Exception ex)
            {
                AppendLog($"[error] {ex.Message}");
            }
            finally
            {
                // Unlock UI & Stop Progress
                if (SettingsProgressBar != null)
                    SettingsProgressBar.IsIndeterminate = false;
                WipeEverythingButton.IsEnabled = true;
            }
        }

        private void AppendLog(string line)
        {
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
        private async void OpenAppDataFolder_Click(object sender, RoutedEventArgs e)
        {
            if (System.IO.Directory.Exists(_appDataFolder))
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(_appDataFolder);
            }
        }
    }
}
