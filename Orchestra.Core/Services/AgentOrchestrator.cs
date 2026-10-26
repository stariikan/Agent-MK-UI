using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Orchestra.Core.Contracts;
using Orchestra.Core.Models;

namespace Orchestra.Core.Services
{
    /// <summary>Thrown when the Python engine reports an error, or gives no response at all, for any action.</summary>
    public class AgentIpcException : Exception
    {
        public AgentIpcException(string message)
            : base(message) { }
    }

    /// <summary>
    /// Core orchestration engine (Pillar 2). Thin, typed wrapper around
    /// PythonEngine's action-based JSON protocol -- one method per action,
    /// so the UI layer never has to build an AgentRequest by hand.
    /// </summary>
    public class AgentOrchestrator
    {
        private readonly IAgentLogger _logger;
        private readonly PythonEngine _pythonEngine;
        private readonly SetupStateStore _stateStore;

        public event Action<int, string>? ProgressReported;

        public AgentOrchestrator(
            IAgentLogger logger,
            PythonEngine pythonEngine,
            SetupStateStore stateStore
        )
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pythonEngine = pythonEngine ?? throw new ArgumentNullException(nameof(pythonEngine));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _pythonEngine.ChatProgressReceived += RelayProgress;
        }

        private void RelayProgress(int chatId, string message)
        {
            ProgressReported?.Invoke(chatId, message);
        }

        private async Task<AgentResponse> SendAsync(AgentRequest request)
        {
            var response = await _pythonEngine.SendRequestAsync(request);
            if (response == null)
            {
                throw new AgentIpcException("No response from the Python engine.");
            }
            if (response.IsError)
            {
                throw new AgentIpcException(
                    response.ErrorMessage ?? "Unknown error from the Python engine."
                );
            }
            return response;
        }

        public string DefaultModel => _stateStore.Load().ChosenModel ?? "qwen2.5-coder:7b";

        public async Task<string> SendChatMessageAsync(int chatId, string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return "Prompt cannot be empty.";
            }

            _logger.LogInfo($"Sending chat message to chat {chatId} ({prompt.Length} chars).");

            var response = await SendAsync(
                new AgentRequest
                {
                    Action = "chat",
                    ChatId = chatId,
                    Prompt = prompt,
                    AgentProfile = _stateStore.Load().AgentProfile,
                }
            );

            return response.ResponseText;
        }

        public async Task<string> PrepareAgenticTaskAsync(int chatId, string prompt)
        {
            var response = await SendAsync(new AgentRequest
            {
                Action = "prepare_agentic_task",
                ChatId = chatId,
                Prompt = prompt,
                AgentProfile = _stateStore.Load().AgentProfile,
            });
            return response.ResponseText;
        }

        public async Task<string> RunAgenticTaskAsync(int chatId, string taskId, string goal, bool allowCommands)
        {
            string cancelPath = GetTaskCancelPath(taskId);
            if (File.Exists(cancelPath))
                File.Delete(cancelPath);
            var response = await SendAsync(new AgentRequest
            {
                Action = "agentic_task",
                ChatId = chatId,
                TaskId = taskId,
                AllowCommands = allowCommands,
                Prompt = goal,
                AgentProfile = _stateStore.Load().AgentProfile,
            });
            return response.ResponseText;
        }

        public void CancelAgenticTask(string taskId)
        {
            string cancelPath = GetTaskCancelPath(taskId);
            Directory.CreateDirectory(Path.GetDirectoryName(cancelPath)!);
            File.WriteAllText(cancelPath, "stop");
        }

        private static string GetTaskCancelPath(string taskId)
        {
            if (!Guid.TryParseExact(taskId, "N", out _))
                throw new ArgumentException("Invalid agentic task id.", nameof(taskId));
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "AgentMK", "task_control", taskId + ".cancel");
        }
        public async Task<List<ChatSummary>> ListChatsAsync()
        {
            var response = await SendAsync(new AgentRequest { Action = "list_chats" });
            return response.Chats ?? new List<ChatSummary>();
        }

        public async Task<ChatSummary> CreateChatAsync(
            string title,
            string? projectPath,
            string? model = null,
            string mode = "chat"
        )
        {
            var response = await SendAsync(
                new AgentRequest
                {
                    Action = "create_chat",
                    Title = title,
                    ProjectPath = projectPath,
                    ModelName = model ?? DefaultModel,
                    ChatMode = mode,
                }
            );

            return response.Chat ?? throw new AgentIpcException("create_chat returned no chat.");
        }

        public async Task DeleteChatAsync(int chatId)
        {
            await SendAsync(new AgentRequest { Action = "delete_chat", ChatId = chatId });
        }

        public async Task<ChatSummary> SetChatProjectAsync(int chatId, string? projectPath)
        {
            var response = await SendAsync(
                new AgentRequest
                {
                    Action = "set_chat_project",
                    ChatId = chatId,
                    ProjectPath = projectPath,
                }
            );

            return response.Chat
                ?? throw new AgentIpcException("set_chat_project returned no chat.");
        }

        public async Task<ChatSummary> RenameChatAsync(int chatId, string title)
        {
            var response = await SendAsync(
                new AgentRequest
                {
                    Action = "rename_chat",
                    ChatId = chatId,
                    Title = title,
                }
            );

            return response.Chat ?? throw new AgentIpcException("rename_chat returned no chat.");
        }

        public async Task<List<ChatMessageDto>> GetMessagesAsync(int chatId)
        {
            var response = await SendAsync(
                new AgentRequest { Action = "get_messages", ChatId = chatId }
            );
            return response.Messages ?? new List<ChatMessageDto>();
        }

        public async Task<List<ProjectFileEntry>> ListProjectFilesAsync(string projectPath)
        {
            var response = await SendAsync(
                new AgentRequest { Action = "list_project_files", ProjectPath = projectPath }
            );
            return response.Files ?? new List<ProjectFileEntry>();
        }

        public async Task<ContextUsage> GetContextUsageAsync(int chatId)
        {
            var response = await SendAsync(
                new AgentRequest
                {
                    Action = "get_context_usage",
                    ChatId = chatId,
                    AgentProfile = _stateStore.Load().AgentProfile,
                }
            );
            return response.ContextUsage
                ?? throw new AgentIpcException("get_context_usage returned no data.");
        }
    }
}


