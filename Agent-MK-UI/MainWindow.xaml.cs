using System;
using System.Text.RegularExpressions;
using System.Threading;
using Agent_MK_UI.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Orchestra.Core.Contracts;
using Orchestra.Core.Models;
using Orchestra.Core.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace Agent_MK_UI
{
    public sealed partial class MainWindow : Window
    {
        private readonly IAgentLogger? _logger;
        private readonly AgentOrchestrator? _orchestrator;
        private readonly SetupOrchestrator? _setupOrchestrator;
        private readonly OllamaManager? _ollamaManager;

        private List<ChatSummary> _chats = new();
        private ChatSummary? _currentChat;

        private const long MaxScanFileBytes = 200 * 1024;
        private const long InlineFileContextBytes = 32 * 1024;
        private const int LargePromptCharThreshold = 48 * 1024;
        private const int LargePromptLineThreshold = 900;

        private DispatcherTimer? _thinkingTimer;
        private TextBlock? _thinkingStatusText;
        private DispatcherTimer? _ollamaStatsTimer;
        private int _ollamaStatsRefreshing;
        private HashSet<string> _ignoredPaths = new();
        // ------------------------------------------------------------
        // Theme helpers
        // ------------------------------------------------------------

        private static Brush GetThemeBrush(string key)
        {
            if (
                Application.Current.Resources.TryGetValue(key, out object boxedBrush)
                && boxedBrush is Brush brush
            )
            {
                return brush;
            }

            return new SolidColorBrush(Microsoft.UI.Colors.Magenta);
        }

        private static Style? GetThemeStyle(string key)
        {
            if (
                Application.Current.Resources.TryGetValue(key, out object boxedStyle)
                && boxedStyle is Style style
            )
            {
                return style;
            }

            return null;
        }

        // ------------------------------------------------------------
        // Constructor
        // ------------------------------------------------------------

        public MainWindow()
        {
            this.InitializeComponent();

            this.Title = "Agent-MK";
            this.ExtendsContentIntoTitleBar = true;
            // Dynamically resolve the absolute path to the icon
            string iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "icon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                this.AppWindow.SetIcon(iconPath);
            }

            var services = ((App)Application.Current).Services;

            _logger = services?.GetService<IAgentLogger>();
            _orchestrator = services?.GetService<AgentOrchestrator>();
            _setupOrchestrator = services?.GetService<SetupOrchestrator>();
            _ollamaManager = services?.GetService<OllamaManager>();

            var rootGrid = (Grid)this.Content;
            rootGrid.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;

            UserInputBox.PreviewKeyDown += UserInputBox_KeyDown;
        }

        // ------------------------------------------------------------
        // Window loaded
        // ------------------------------------------------------------

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            this.SetTitleBar(AppTitleBar);

            int titleBarInset = this.AppWindow.TitleBar.RightInset;

            RightPaddingColumn.Width = new GridLength(titleBarInset);

            UpdateProjectActionButtons();
            await RefreshChatListAsync();

            StartOllamaStatsTimer();
            await RefreshOllamaStatsAsync();
        }

        // ------------------------------------------------------------
        // Dialog helper
        // ------------------------------------------------------------

        private async Task<ContentDialogResult> ShowMessageDialogAsync(string message, string title)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot,
            };

            return await dialog.ShowAsync();
        }

        // ------------------------------------------------------------
        // User input
        // ------------------------------------------------------------

        private void UserInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                VirtualKey.Shift
            );

            bool isShiftDown =
                (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down)
                == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (e.Key == VirtualKey.Enter && !isShiftDown)
            {
                e.Handled = true;

                SendButton_Click(sender, new RoutedEventArgs());
            }
        }

        // ------------------------------------------------------------
        // Chat list
        // ------------------------------------------------------------

        private async Task RefreshChatListAsync()
        {
            if (_orchestrator == null)
                return;

            try
            {
                _chats = await _orchestrator.ListChatsAsync();

                if (_chats.Count == 0)
                {
                    var chat = await _orchestrator.CreateChatAsync("New chat", null);

                    _chats = await _orchestrator.ListChatsAsync();

                    _currentChat = chat;
                }
                else if (_currentChat == null || _chats.All(c => c.Id != _currentChat.Id))
                {
                    _currentChat = _chats.First();
                }

                ChatCountText.Text = $"Chats ({_chats.Count}/30)";

                BuildChatListPanel();

                if (_currentChat != null)
                {
                    await SelectChatAsync(_currentChat);
                }
            }
            catch (AgentIpcException ex)
            {
                _logger?.LogError($"Failed to load chats: {ex.Message}");

                await ShowMessageDialogAsync($"Failed to load chats: {ex.Message}", "Error");
            }
        }

        private void BuildChatListPanel()
        {
            ChatListPanel.Children.Clear();

            foreach (var chat in _chats)
            {
                bool isSelected = _currentChat != null && chat.Id == _currentChat.Id;

                var row = new Border
                {
                    Background = isSelected
                        ? GetThemeBrush("SubtleFillColorSecondaryBrush")
                        : new SolidColorBrush(Microsoft.UI.Colors.Transparent),

                    CornerRadius = new CornerRadius(4),

                    Padding = new Thickness(8, 6, 8, 6),

                    Margin = new Thickness(0, 0, 0, 2),
                };

                var grid = new Grid();

                grid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                );

                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textPanel = new StackPanel();

                textPanel.Children.Add(
                    new TextBlock
                    {
                        Text = chat.Title,

                        Foreground = GetThemeBrush("TextFillColorPrimaryBrush"),

                        TextTrimming = TextTrimming.CharacterEllipsis,

                        FontWeight = isSelected
                            ? Microsoft.UI.Text.FontWeights.SemiBold
                            : Microsoft.UI.Text.FontWeights.Normal,
                    }
                );

                if (!string.IsNullOrEmpty(chat.ProjectPath))
                {
                    textPanel.Children.Add(
                        new TextBlock
                        {
                            Text = Path.GetFileName(chat.ProjectPath.TrimEnd('\\', '/')),

                            Foreground = GetThemeBrush("AccentTextFillColorPrimaryBrush"),

                            FontSize = 10,

                            TextTrimming = TextTrimming.CharacterEllipsis,
                        }
                    );
                }

                Grid.SetColumn(textPanel, 0);

                grid.Children.Add(textPanel);

                var deleteButton = new Button
                {
                    Content = "\u2715",

                    Padding = new Thickness(4, 0, 0, 0),

                    VerticalAlignment = VerticalAlignment.Top,

                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),

                    BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                };

                deleteButton.Click += async (_, __) => await DeleteChatAsync(chat);

                Grid.SetColumn(deleteButton, 1);

                grid.Children.Add(deleteButton);

                row.Child = grid;

                row.PointerPressed += async (_, __) => await SelectChatAsync(chat);

                if (!isSelected)
                {
                    row.PointerEntered += (_, __) =>
                        row.Background = GetThemeBrush("SubtleFillColorTransparentBrush");

                    row.PointerExited += (_, __) =>
                        row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }

                ChatListPanel.Children.Add(row);
            }
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_orchestrator == null)
                return;

            if (_chats.Count >= 30)
            {
                await ShowMessageDialogAsync(
                    "Maximum of 30 chats reached. Delete one before creating a new chat.",
                    "Chat limit reached"
                );

                return;
            }

            try
            {
                // 1. Instantly create the chat using system defaults, bypassing the UI dialog.
                var chat = await _orchestrator.CreateChatAsync(
                    "New chat",
                    null,
                    _orchestrator.DefaultModel
                );

                _currentChat = chat;
                _ignoredPaths.Clear();
                await RefreshChatListAsync();

                // 2. Auto-focus the input box so the user can type immediately.
                UserInputBox.Focus(FocusState.Programmatic);
            }
            catch (AgentIpcException ex)
            {
                await ShowMessageDialogAsync(ex.Message, "Could not create chat");
            }
        }

        private async Task DeleteChatAsync(ChatSummary chat)
        {
            if (_orchestrator == null)
                return;

            var dialog = new ContentDialog
            {
                Title = "Delete chat",

                Content = $"Delete chat '{chat.Title}'? This can't be undone.",

                PrimaryButtonText = "Yes",
                CloseButtonText = "No",
                XamlRoot = this.Content.XamlRoot,
            };

            var confirm = await dialog.ShowAsync();

            if (confirm != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                await _orchestrator.DeleteChatAsync(chat.Id);

                if (_currentChat?.Id == chat.Id)
                {
                    _currentChat = null;
                }

                await RefreshChatListAsync();
            }
            catch (AgentIpcException ex)
            {
                await ShowMessageDialogAsync(ex.Message, "Could not delete chat");
            }
        }

        private async Task SelectChatAsync(ChatSummary chat)
        {
            _currentChat = chat;
            _ignoredPaths.Clear();
            BuildChatListPanel();

            ChatHistoryPanel.Children.Clear();

            ContextUsage? usage = await RefreshContextUsageAsync();

            if (_orchestrator != null)
            {
                try
                {
                    var messages = await _orchestrator.GetMessagesAsync(chat.Id);

                    int boundaryIndex =
                        usage != null
                            ? Math.Max(0, messages.Count - usage.RememberedMessageCount)
                            : 0;

                    for (int i = 0; i < messages.Count; i++)
                    {
                        if (i == boundaryIndex && boundaryIndex > 0)
                        {
                            AppendContextBoundaryDivider();
                        }

                        var msg = messages[i];

                        string role = msg.Role?.Trim().ToLowerInvariant() ?? "";

                        if (role == "user" || role == "human" || role == "user_message")
                        {
                            AppendUserBubble(msg.Content);
                        }
                        else
                        {
                            AppendAssistantBubble(
                                SanitizeAssistantResponse(msg.Content),
                                chat.ProjectPath
                            );
                        }
                    }
                }
                catch (AgentIpcException ex)
                {
                    _logger?.LogError($"Failed to load messages for chat {chat.Id}: {ex.Message}");
                }
            }

            await RefreshProjectPanelAsync();
        }

        private void AppendContextBoundaryDivider()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,

                Margin = new Thickness(0, 10, 0, 10),

                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var lineColor = GetThemeBrush("CardStrokeColorDefaultBrush");

            panel.Children.Add(
                new Border
                {
                    Width = 60,
                    Height = 1,
                    Background = lineColor,

                    VerticalAlignment = VerticalAlignment.Center,

                    Margin = new Thickness(0, 0, 8, 0),
                }
            );

            panel.Children.Add(
                new TextBlock
                {
                    Text = "Model's memory starts here \u2193",

                    FontSize = 11,

                    Foreground = GetThemeBrush("TextFillColorSecondaryBrush"),
                }
            );

            panel.Children.Add(
                new Border
                {
                    Width = 60,
                    Height = 1,
                    Background = lineColor,

                    VerticalAlignment = VerticalAlignment.Center,

                    Margin = new Thickness(8, 0, 0, 0),
                }
            );

            ChatHistoryPanel.Children.Add(panel);
        }

        // ------------------------------------------------------------
        // Context usage
        // ------------------------------------------------------------

        private async Task<ContextUsage?> RefreshContextUsageAsync()
        {
            if (_currentChat == null || _orchestrator == null)
            {
                return null;
            }

            try
            {
                var usage = await _orchestrator.GetContextUsageAsync(_currentChat.Id);

                ContextUsageBar.Value = usage.UsedFraction;

                string estimateNote = usage.MaxTokensIsEstimate
                    ? " (estimated max, model unreachable)"
                    : "";

                string text =
                    $"Context: ~{usage.UsedTokensEstimate:N0} / "
                    + $"{usage.MaxTokensModel:N0} tokens "
                    + $"({usage.UsedFraction:P0}){estimateNote} — "
                    + $"agent: {usage.AgentProfile}, output cap: {usage.MaxOutputTokens:N0}; "
                    + $"remembers the last "
                    + $"{usage.RememberedMessageCount} of "
                    + $"{usage.TotalStoredMessages} messages";

                if (usage.WasTrimmed)
                {
                    text += " (older ones dropped to fit)";
                }

                ContextUsageText.Text = text;

                return usage;
            }
            catch (AgentIpcException ex)
            {
                _logger?.LogWarning($"Failed to load context usage: {ex.Message}");

                ContextUsageText.Text = "";
                ContextUsageBar.Value = 0;

                return null;
            }
        }

        // ------------------------------------------------------------
        // Project panel
        // ------------------------------------------------------------

        private async Task RefreshProjectPanelAsync()
        {
            ProjectTreePanel.Children.Clear();

            if (_currentChat == null || string.IsNullOrWhiteSpace(_currentChat.ProjectPath))
            {
                ProjectPathText.Text = "No project attached";
                ProjectStatsText.Text = "Attach a project to inspect its files.";
                UpdateProjectActionButtons(false);
                return;
            }

            string projectPath = _currentChat.ProjectPath;
            ProjectPathText.Text = projectPath;
            UpdateProjectActionButtons(true);

            if (_orchestrator == null)
                return;

            try
            {
                var files = await _orchestrator.ListProjectFilesAsync(projectPath);
                UpdateProjectStats(files);
                BuildProjectTree(files);
            }
            catch (AgentIpcException ex)
            {
                ProjectStatsText.Text = "Project scan failed.";
                ProjectTreePanel.Children.Add(
                    new TextBlock
                    {
                        Text = $"Could not scan project: {ex.Message}",
                        Foreground = GetThemeBrush("SystemFillColorCriticalBrush"),
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
            }
        }

        private void UpdateProjectActionButtons(bool? attached = null)
        {
            bool hasProject = attached ?? !string.IsNullOrWhiteSpace(_currentChat?.ProjectPath);

            AttachProjectButton.Visibility = hasProject ? Visibility.Collapsed : Visibility.Visible;
            RefreshProjectButton.Visibility = hasProject
                ? Visibility.Visible
                : Visibility.Collapsed;
            ReviewProjectButton.Visibility = hasProject ? Visibility.Visible : Visibility.Collapsed;
            RemoveProjectButton.Visibility = hasProject ? Visibility.Visible : Visibility.Collapsed;

            RefreshProjectButton.IsEnabled = hasProject;
            ReviewProjectButton.IsEnabled = hasProject;
            RemoveProjectButton.IsEnabled = hasProject;
        }

        private void UpdateProjectStats(List<ProjectFileEntry> files)
        {
            var visibleFiles = files.Where(f =>
                !_ignoredPaths.Any(ignored =>
                    f.Path == ignored || f.Path.StartsWith(ignored + "/") || f.Path.StartsWith(ignored + "\\")
                )
            ).ToList();

            int fileCount = visibleFiles.Count(f => !f.IsDir);
            int directoryCount = visibleFiles.Count(f => f.IsDir);
            int codeLike = visibleFiles.Count(f => !f.IsDir && IsCodeLikeFile(f.Path));

            string stats = $"{fileCount:N0} files • {directoryCount:N0} folders • {codeLike:N0} code/text";

            if (_ignoredPaths.Count > 0)
            {
                stats += $"\n({_ignoredPaths.Count} items explicitly ignored)";
            }

            ProjectStatsText.Text = stats;
        }

        private static bool IsCodeLikeFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext
                is ".cs"
                    or ".csproj"
                    or ".xaml"
                    or ".razor"
                    or ".py"
                    or ".js"
                    or ".ts"
                    or ".tsx"
                    or ".jsx"
                    or ".json"
                    or ".xml"
                    or ".html"
                    or ".css"
                    or ".scss"
                    or ".md"
                    or ".txt"
                    or ".bat"
                    or ".ps1"
                    or ".cpp"
                    or ".h"
                    or ".hpp";
        }

        private void BuildProjectTree(List<ProjectFileEntry> files)
        {
            ProjectTreePanel.Children.Clear();

            // 1. Filter out ignored paths
            var visibleFiles = files.Where(f =>
                !_ignoredPaths.Any(ignored =>
                    f.Path == ignored || f.Path.StartsWith(ignored + "/") || f.Path.StartsWith(ignored + "\\")
                )
            ).ToList();
            // 2. Loop over visibleFiles instead of files
            foreach (var entry in visibleFiles)
            {
                int depth = entry.Path.Count(c => c == '/');

                string name = entry.Path.Contains('/')
                    ? entry.Path[(entry.Path.LastIndexOf('/') + 1)..]
                    : entry.Path;

                var row = new Grid { Margin = new Thickness(depth * 12, 1, 0, 1) };

                row.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                );

                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = (entry.IsDir ? "\U0001F4C1 " : "\U0001F4C4 ") + name,

                    Foreground = entry.IsDir
                        ? GetThemeBrush("SystemFillColorCautionBrush")
                        : GetThemeBrush("TextFillColorPrimaryBrush"),

                    FontSize = 12,

                    TextTrimming = TextTrimming.CharacterEllipsis,

                    VerticalAlignment = VerticalAlignment.Center,
                };

                Grid.SetColumn(label, 0);

                row.Children.Add(label);

                if (entry.IsDir)
                {
                    var removeBtn = new Button
                    {
                        Content = "✕",
                        FontSize = 10,
                        Padding = new Thickness(6, 2, 6, 2),
                        Style = GetThemeStyle("QuietButtonStyle"),
                        Foreground = GetThemeBrush("SystemFillColorCriticalBrush")
                    };

                    ToolTipService.SetToolTip(removeBtn, "Exclude folder from AI scans");

                    removeBtn.Click += (_, __) =>
                    {
                        _ignoredPaths.Add(entry.Path);
                        UpdateProjectStats(files); // Recalculate stats with the full list
                        BuildProjectTree(files);   // Rebuild tree from the full list
                    };

                    Grid.SetColumn(removeBtn, 1);
                    row.Children.Add(removeBtn);
                }
                else
                {
                    var scanButton = new Button
                    {
                        Content = "Scan",
                        FontSize = 10,
                        Padding = new Thickness(4, 1, 4, 1),
                        Style = GetThemeStyle("QuietButtonStyle"),
                    };
                    scanButton.Click += (_, __) => ScanFileIntoInput(entry.Path);
                    Grid.SetColumn(scanButton, 1);
                    row.Children.Add(scanButton);
                }

                ProjectTreePanel.Children.Add(row);
            }

            if (files.Count == 0)
            {
                ProjectTreePanel.Children.Add(
                    new TextBlock
                    {
                        Text = "(empty project)",

                        Foreground = GetThemeBrush("TextFillColorSecondaryBrush"),
                    }
                );
            }
            else if (visibleFiles.Count == 0)
            {
                ProjectTreePanel.Children.Add(new TextBlock { Text = "(all files ignored)", Foreground = GetThemeBrush("TextFillColorSecondaryBrush") });
            }
        }

        private void ScanFileIntoInput(string relativePath)
        {
            if (_currentChat?.ProjectPath == null)
                return;

            string fullPath = Path.Combine(
                _currentChat.ProjectPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)
            );

            InsertFileContentIntoInput(fullPath, relativePath);
        }

        private void InsertFileContentIntoInput(string fullPath, string displayName)
        {
            try
            {
                var info = new FileInfo(fullPath);

                if (!info.Exists)
                    return;

                string content = File.ReadAllText(fullPath);

                string block =
                    $"<AGENT_MK_FILE path=\"{displayName}\">\n"
                    + content
                    + "\n</AGENT_MK_FILE>\n\n";

                UserInputBox.Text = block + UserInputBox.Text;

                UserInputBox.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Could not read file: {ex.Message}");
            }
        }

        private async void RefreshProjectButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshProjectPanelAsync();
        }

        private void ReviewProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChat == null || string.IsNullOrWhiteSpace(_currentChat.ProjectPath))
                return;

            // Pass the explicit exclusions to the AI
            string ignorePrompt = _ignoredPaths.Count > 0
                ? $"\n\nCRITICAL INSTRUCTION: You must COMPLETELY IGNORE the following excluded folders and files. Do not scan, read, or summarize them under any circumstances: {string.Join(", ", _ignoredPaths)}."
                : "";

            // Point the AI directly to the absolute path of the new project
            string reviewPrompt =
                $"Review the attached project located strictly at this path: \"{_currentChat.ProjectPath}\". "
                + "Start by inspecting its structure and the files relevant to my request. "
                + "Do not ask me to paste the project into chat; use your project workspace tools to read the files you need from that exact path. "
                + "First summarize the architecture and identify the most relevant files, then proceed with the requested work."
                + ignorePrompt;

            UserInputBox.Text = string.IsNullOrWhiteSpace(UserInputBox.Text)
                ? reviewPrompt
                : reviewPrompt + "\n\n" + UserInputBox.Text;

            UserInputBox.Focus(FocusState.Programmatic);
        }

        private async void AttachProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChat == null || _orchestrator == null)
            {
                return;
            }

            var picker = new FolderPicker();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();

            if (folder == null)
                return;

            try
            {
                _currentChat = await _orchestrator.SetChatProjectAsync(
                    _currentChat.Id,
                    folder.Path
                );
                _ignoredPaths.Clear();
                UpdateProjectActionButtons(true);
                await RefreshProjectPanelAsync();
                await RefreshChatListAsync();
            }
            catch (AgentIpcException ex)
            {
                await ShowMessageDialogAsync(ex.Message, "Could not attach project");
            }
        }

        // ------------------------------------------------------------
        // Sending messages
        // ------------------------------------------------------------

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChat == null || _orchestrator == null)
                return;

            string promptText = UserInputBox.Text;
            if (string.IsNullOrWhiteSpace(promptText))
                return;

            if (IsLargePrompt(promptText))
            {
                var decision = new ContentDialog
                {
                    Title = "Large prompt",
                    Content = BuildLargePromptWarning(promptText),
                    PrimaryButtonText = "Send anyway",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot,
                };

                if (await decision.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }

            bool shouldAutoName = _currentChat.Title == "New chat";
            UserInputBox.Text = string.Empty;
            SendButton.IsEnabled = false;

            AppendUserBubble(promptText);
            var thinkingBubble = AppendThinkingBubble();

            try
            {
                string responseText = await _orchestrator.SendChatMessageAsync(
                    _currentChat.Id,
                    promptText
                );
                responseText = SanitizeAssistantResponse(responseText);

                _logger?.LogInfo($"AI response received ({responseText.Length} display chars).");

                StopThinkingAnimation();
                ChatHistoryPanel.Children.Remove(thinkingBubble);
                AppendAssistantBubble(responseText, _currentChat.ProjectPath);
                await RefreshContextUsageAsync();

                if (shouldAutoName)
                {
                    string derivedTitle = DeriveTitleFromMessage(promptText);
                    try
                    {
                        _currentChat = await _orchestrator.RenameChatAsync(
                            _currentChat.Id,
                            derivedTitle
                        );
                        int idx = _chats.FindIndex(c => c.Id == _currentChat.Id);
                        if (idx >= 0)
                            _chats[idx] = _currentChat;
                        BuildChatListPanel();
                    }
                    catch (AgentIpcException ex)
                    {
                        _logger?.LogWarning($"Failed to auto-name chat: {ex.Message}");
                    }
                }
            }
            catch (AgentIpcException ex)
            {
                StopThinkingAnimation();
                ChatHistoryPanel.Children.Remove(thinkingBubble);
                AppendAssistantBubble(
                    $"[error] {ex.Message}",
                    _currentChat.ProjectPath,
                    isError: true
                );
            }
            catch (Exception ex)
            {
                StopThinkingAnimation();
                ChatHistoryPanel.Children.Remove(thinkingBubble);
                _logger?.LogError($"Unexpected send failure: {ex}");
                AppendAssistantBubble(
                    $"[error] {ex.Message}",
                    _currentChat.ProjectPath,
                    isError: true
                );
            }
            finally
            {
                StopThinkingAnimation();
                await Task.Delay(100);
                ChatScrollViewer.UpdateLayout();
                ChatScrollViewer.ChangeView(null, ChatScrollViewer.ScrollableHeight, null);
                SendButton.IsEnabled = true;
                UserInputBox.Focus(FocusState.Programmatic);
            }
        }

        private static bool IsLargePrompt(string prompt)
        {
            return prompt.Length >= LargePromptCharThreshold
                || prompt.Count(c => c == '\n') + 1 >= LargePromptLineThreshold;
        }

        private static string BuildLargePromptWarning(string prompt)
        {
            int lines = prompt.Count(c => c == '\n') + 1;
            int approxTokens = Math.Max(1, prompt.Length / 4);
            return $"This message is unusually large ({lines:N0} lines, roughly {approxTokens:N0} tokens by a simple character estimate). "
                + "The current runtime protects conversation history, but a single very large user message can still exceed the model's input budget. "
                + "For large code, attach the project and use Review/Scan on the relevant files so the model can read them through its workspace tools instead of pasting thousands of lines into chat.";
        }

        private static string SanitizeAssistantResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // Reasoning should never be rendered into the chat transcript. Keep only the user-facing answer.
            string cleaned = Regex.Replace(
                text,
                @"(?is)<(?:think|analysis|reasoning)>.*?</(?:think|analysis|reasoning)>\s*",
                string.Empty
            );
            cleaned = Regex.Replace(
                cleaned,
                @"(?im)^\s*```(?:thinking|analysis|reasoning)\s*$.*?^\s*```\s*$",
                string.Empty,
                RegexOptions.Singleline
            );

            // Hide accidental model metadata lines, without filtering normal prose that happens to mention models.
            cleaned = Regex.Replace(
                cleaned,
                @"(?im)^\s*(?:model|model_name|model name)\s*:\s*[A-Za-z0-9_.:/-]+\s*$\n?",
                string.Empty
            );

            return cleaned.Trim();
        }

        private static string DeriveTitleFromMessage(string prompt)
        {
            string flat = prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();

            var words = flat.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 0)
                return "New chat";

            const int maxWords = 6;
            const int maxChars = 60;

            string title = string.Join(' ', words.Take(maxWords));

            if (title.Length > maxChars)
            {
                title = title[..maxChars].TrimEnd();
            }

            if (words.Length > maxWords || title.Length < flat.Length)
            {
                title += "...";
            }

            return title;
        }

        // ------------------------------------------------------------
        // File attachment
        // ------------------------------------------------------------

        private async void AttachFileButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add("*");

            var file = await picker.PickSingleFileAsync();

            if (file != null)
            {
                await InsertFileContentIntoInputAsync(file);
            }
        }

        private async Task InsertFileContentIntoInputAsync(Windows.Storage.StorageFile file)
        {
            try
            {
                var properties = await file.GetBasicPropertiesAsync();

                if (properties.Size > MaxScanFileBytes)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Large file",
                        Content =
                            $"'{file.Name}' is {properties.Size / 1024:N0} KB. To keep the model context healthy, it will be referenced as a workspace file instead of pasted into the chat.",
                        PrimaryButtonText = "Use workspace reference",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.Content.XamlRoot,
                    };

                    if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                        return;
                }

                string block;
                if (properties.Size > InlineFileContextBytes)
                {
                    block =
                        $"Please read the attached project file \"{file.Name}\" from the workspace before answering. "
                        + "Use the file as the source of truth and do not ask me to paste its full contents into chat.\n\n";
                }
                else
                {
                    string content = await Windows.Storage.FileIO.ReadTextAsync(file);
                    block =
                        $"<AGENT_MK_FILE path=\"{file.Name}\">\n"
                        + content
                        + "\n</AGENT_MK_FILE>\n\n";
                }

                UserInputBox.Text = block + UserInputBox.Text;
                UserInputBox.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync(
                    $"Could not read '{file.Name}': {ex.Message}",
                    "Error"
                );
            }
        }

        // ------------------------------------------------------------
        // Text rendering
        // ------------------------------------------------------------

        private static TextBlock CreateSelectableText(
            string text,
            Brush foreground,
            TextWrapping wrap,
            FontFamily? fontFamily = null,
            double fontSize = 13
        )
        {
            return new TextBlock
            {
                Text = text,

                Foreground = foreground,

                TextWrapping = wrap,

                FontFamily = fontFamily ?? new FontFamily("Segoe UI"),

                FontSize = fontSize,

                IsTextSelectionEnabled = true,

                Margin = new Thickness(0),
            };
        }

        private void AppendUserBubble(string text)
        {
            var content = new StackPanel { Spacing = 8 };
            var segments = CodeSnippetHelper.Split(text);

            foreach (var segment in segments)
            {
                if (!segment.IsCode)
                {
                    if (string.IsNullOrWhiteSpace(segment.Text)) continue;
                    content.Children.Add(CreateSelectableText(
                        segment.Text.Trim('\n'),
                        GetThemeBrush("TextOnAccentFillColorPrimaryBrush"),
                        TextWrapping.Wrap));
                }
                else
                {
                    content.Children.Add(BuildCodeBlock(
                        segment.Language, segment.Text, _currentChat?.ProjectPath, segment.SuggestedFileName));
                }
            }

            var fullView = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 600,
                Content = content,
                Visibility = Visibility.Collapsed
            };

            // First-line preview
            string normalized = text.Replace("\r\n", "\n").TrimStart('\n');
            int nl = normalized.IndexOf('\n');
            string firstLine = nl >= 0 ? normalized[..nl] : normalized;
            bool hasMore = nl >= 0 || segments.Any(s => s.IsCode) || firstLine.Length > 140;
            if (firstLine.Length > 140) firstLine = firstLine[..140] + "…";

            var previewBlock = CreateSelectableText(
                firstLine, GetThemeBrush("TextOnAccentFillColorPrimaryBrush"), TextWrapping.NoWrap);
            previewBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            var previewHost = new Border { Child = previewBlock, Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed };

            if (!hasMore)
                fullView.Visibility = Visibility.Visible; // nothing to collapse, just show it

            var toggleButton = new Button
            {
                Content = "Expand",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                Style = GetThemeStyle("QuietButtonStyle"),
                Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed
            };

            bool expanded = false;
            toggleButton.Click += (_, __) =>
            {
                expanded = !expanded;
                fullView.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                previewHost.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
                toggleButton.Content = expanded ? "Collapse" : "Expand";
            };

            // Same look as the AI code-block copy button, instead of the old icon-only one
            var copyButton = new Button
            {
                Content = "Copy",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                Style = GetThemeStyle("QuietButtonStyle")
            };
            ToolTipService.SetToolTip(copyButton, "Copy message");
            copyButton.Click += (_, __) =>
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
            };

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttonRow.Children.Add(toggleButton);
            buttonRow.Children.Add(copyButton);

            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(previewHost);
            body.Children.Add(fullView);

            var bubbleLayout = new StackPanel { Spacing = 4 };
            bubbleLayout.Children.Add(buttonRow);
            bubbleLayout.Children.Add(body);

            var border = new Border
            {
                Background = GetThemeBrush("AccentFillColorDefaultBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 8, 8),
                Margin = new Thickness(0, 4, 0, 4),
                MaxWidth = 760,
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = bubbleLayout
            };

            ChatHistoryPanel.Children.Add(border);
        }

        private Border AppendThinkingBubble()
        {
            var statusText = new TextBlock
            {
                Text = "Working",
                Foreground = GetThemeBrush("TextFillColorSecondaryBrush"),
                FontSize = 12,
            };

            var progress = new ProgressRing
            {
                IsActive = true,
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 8, 0),
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(progress);
            row.Children.Add(statusText);

            var border = new Border
            {
                Background = GetThemeBrush("CardBackgroundFillColorDefaultBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 4, 0, 4),
                MaxWidth = 760,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = row,
            };

            _thinkingStatusText = statusText;
            int dotCount = 0;
            _thinkingTimer?.Stop();
            _thinkingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _thinkingTimer.Tick += (_, __) =>
            {
                if (_thinkingStatusText == null)
                    return;

                dotCount = (dotCount + 1) % 4;
                _thinkingStatusText.Text = "Working" + new string('.', dotCount);
            };
            _thinkingTimer.Start();

            ChatHistoryPanel.Children.Add(border);
            return border;
        }

        private void StopThinkingAnimation()
        {
            _thinkingTimer?.Stop();
            _thinkingTimer = null;
            _thinkingStatusText = null;
        }

        // ------------------------------------------------------------
        // Assistant rendering
        // ------------------------------------------------------------

        private void AppendAssistantBubble(string text, string? projectPath, bool isError = false)
        {
            var content = new StackPanel { Spacing = 8 };

            var segments = CodeSnippetHelper.Split(text);

            foreach (var segment in segments)
            {
                if (!segment.IsCode)
                {
                    if (string.IsNullOrWhiteSpace(segment.Text))
                    {
                        continue;
                    }

                    var textBox = CreateSelectableText(
                        segment.Text.Trim('\n'),
                        isError
                            ? GetThemeBrush("SystemFillColorCriticalBrush")
                            : GetThemeBrush("TextFillColorPrimaryBrush"),
                        TextWrapping.Wrap
                    );

                    content.Children.Add(textBox);
                }
                else
                {
                    content.Children.Add(
                        BuildCodeBlock(
                            segment.Language,
                            segment.Text,
                            projectPath,
                            segment.SuggestedFileName
                        )
                    );
                }
            }

            var border = new Border
            {
                Background = GetThemeBrush("CardBackgroundFillColorDefaultBrush"),

                CornerRadius = new CornerRadius(8),

                Padding = new Thickness(12),

                Margin = new Thickness(0, 4, 0, 4),

                MaxWidth = 760,

                HorizontalAlignment = HorizontalAlignment.Left,

                Child = content,
            };

            ChatHistoryPanel.Children.Add(border);
        }

        // ------------------------------------------------------------
        // Code block rendering
        // ------------------------------------------------------------

        private UIElement BuildCodeBlock(
            string language,
            string code,
            string? projectPath,
            string? suggestedFileName = null
        )
        {
            var outer = new Border
            {
                Background = GetThemeBrush("LayerFillColorDefaultBrush"),

                BorderBrush = GetThemeBrush("CardStrokeColorDefaultBrush"),

                BorderThickness = new Thickness(1),

                CornerRadius = new CornerRadius(4),

                Margin = new Thickness(0, 4, 0, 4),

                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var stack = new StackPanel { Spacing = 0 };

            // --------------------------------------------------------
            // Code header
            // --------------------------------------------------------

            var header = new Grid { Margin = new Thickness(8, 4, 8, 4) };

            header.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            );

            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var langLabel = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(language) ? "code" : language.ToUpperInvariant(),

                Foreground = GetThemeBrush("TextFillColorSecondaryBrush"),

                FontFamily = new FontFamily("Consolas"),

                FontSize = 11,

                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(langLabel, 0);

            header.Children.Add(langLabel);

            // --------------------------------------------------------
            // Copy
            // --------------------------------------------------------

            var copyButton = new Button
            {
                Content = "Copy",

                FontSize = 11,

                Padding = new Thickness(6, 1, 6, 1),

                Margin = new Thickness(0, 0, 6, 0),

                Style = GetThemeStyle("QuietButtonStyle"),
            };

            copyButton.Click += (_, __) =>
            {
                var package = new DataPackage();

                package.SetText(code);

                Clipboard.SetContent(package);
            };

            Grid.SetColumn(copyButton, 1);

            header.Children.Add(copyButton);

            // --------------------------------------------------------
            // Save
            // --------------------------------------------------------

            var saveButton = new Button
            {
                Content = "Save as file",

                FontSize = 11,

                Padding = new Thickness(6, 1, 6, 1),

                Style = GetThemeStyle("QuietButtonStyle"),
            };

            saveButton.Click += async (_, __) =>
                await SaveSnippetAsFileAsync(language, code, projectPath, suggestedFileName);

            Grid.SetColumn(saveButton, 2);

            header.Children.Add(saveButton);

            stack.Children.Add(header);

            // --------------------------------------------------------
            // CODE VIEW
            // --------------------------------------------------------

            var codeText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),

                FontSize = 12,

                TextWrapping = TextWrapping.NoWrap,

                IsTextSelectionEnabled = true,

                Padding = new Thickness(8),

                Foreground = GetThemeBrush("TextFillColorPrimaryBrush"),

                HorizontalAlignment = HorizontalAlignment.Left,

                VerticalAlignment = VerticalAlignment.Top,
            };

            // --------------------------------------------------------
            // Syntax highlighting
            //
            // IMPORTANT:
            // SyntaxHighlighter.cs tokenizes the raw code and each token
            // gets its own color.
            // --------------------------------------------------------

            var tokens = SyntaxHighlighter.Tokenize(code, language);

            foreach (var token in tokens)
            {
                var run = new Run
                {
                    Text = token.Text,

                    Foreground = SyntaxHighlighter.BrushForToken(token.Type),
                };

                codeText.Inlines.Add(run);
            }

            // --------------------------------------------------------
            // Single scroll viewer
            // --------------------------------------------------------

            var codeScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,

                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,

                MaxHeight = 350,

                Content = codeText,
            };

            stack.Children.Add(codeScroll);

            outer.Child = stack;

            return outer;
        }

        // ------------------------------------------------------------
        // Save code
        // ------------------------------------------------------------

        private async Task SaveSnippetAsFileAsync(
            string language,
            string code,
            string? projectPath,
            string? suggestedFileName = null
        )
        {
            string ext = CodeSnippetHelper.ExtensionForLanguage(language);

            string fileName;

            if (!string.IsNullOrWhiteSpace(suggestedFileName))
            {
                fileName = suggestedFileName.Trim();

                // Make sure the extension exists.
                if (!Path.HasExtension(fileName))
                {
                    fileName += ext;
                }
            }
            else
            {
                fileName = "snippet" + ext;
            }

            var picker = new FileSavePicker();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.SuggestedFileName = fileName;

            picker.FileTypeChoices.Add("Source File", new List<string> { ext });

            var file = await picker.PickSaveFileAsync();

            if (file == null)
                return;

            try
            {
                await Windows.Storage.FileIO.WriteTextAsync(file, code);
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync($"Could not save file: {ex.Message}", "Error");
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs e)
        {
            StopThinkingAnimation();
            _ollamaStatsTimer?.Stop();
            _ollamaStatsTimer = null;
        }

        private void StartOllamaStatsTimer()
        {
            _ollamaStatsTimer?.Stop();
            _ollamaStatsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _ollamaStatsTimer.Tick += async (_, __) => await RefreshOllamaStatsAsync();
            _ollamaStatsTimer.Start();
        }

        private async Task RefreshOllamaStatsAsync()
        {
            if (_ollamaManager == null || Interlocked.Exchange(ref _ollamaStatsRefreshing, 1) != 0)
                return;

            try
            {
                OllamaProcessSummary summary = await _ollamaManager.GetProcessSummaryAsync();
                OllamaStatsText.Text = summary.IsOnline
                    ? $"Ollama • {summary.Text}"
                    : "Ollama • offline";
            }
            catch (Exception ex)
            {
                OllamaStatsText.Text = "Ollama • unavailable";
                _logger?.LogWarning($"Ollama stats refresh failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _ollamaStatsRefreshing, 0);
            }
        }

        // ------------------------------------------------------------
        // Settings
        // ------------------------------------------------------------

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_setupOrchestrator == null || _ollamaManager == null)
            {
                return;
            }

            // 1. Instantiate the UserControl with its required dependencies
            var settingsView = new SettingsView(_setupOrchestrator, _ollamaManager);

            // 2. Wire up the decoupled closing event
            settingsView.CloseRequested += (s, args) =>
            {
                // Hide the overlay
                SettingsOverlayHost.Visibility = Visibility.Collapsed;
                // Remove the control from the visual tree so it can be garbage collected
                SettingsOverlayHost.Children.Clear();
            };

            // 3. Inject the control into the host container and reveal it
            SettingsOverlayHost.Children.Clear(); // Ensure it's clean
            SettingsOverlayHost.Children.Add(settingsView);
            SettingsOverlayHost.Visibility = Visibility.Visible;
        }

        private async void RemoveProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChat == null || _orchestrator == null)
            {
                return;
            }

            try
            {
                // Detach the project by setting the ProjectPath to null
                _currentChat = await _orchestrator.SetChatProjectAsync(_currentChat.Id, null);
                _ignoredPaths.Clear();
                // Immediately restore the detached-state actions, then refresh the rest of the UI.
                UpdateProjectActionButtons(false);
                await RefreshProjectPanelAsync();
                await RefreshChatListAsync();
            }
            catch (AgentIpcException ex)
            {
                await ShowMessageDialogAsync(ex.Message, "Could not remove project");
            }
        }
    }
}
