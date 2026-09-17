using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Orchestra.Core.Models
{
    /// <summary>
    /// JSON payload sent via STDIN from C# to the Python process. This is a
    /// single flexible envelope covering every action the protocol
    /// supports (see "action") rather than one class per message type --
    /// simpler to keep in sync across the C#/Python boundary at this scale.
    /// Unused fields are just omitted (nulled) by System.Text.Json.
    /// </summary>
    public class AgentRequest
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = "chat";

        [JsonPropertyName("chat_id")]
        public int? ChatId { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("model_name")]
        public string? ModelName { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("project_path")]
        public string? ProjectPath { get; set; }

        [JsonPropertyName("agent_profile")]
        public string? AgentProfile { get; set; }
    }

    /// <summary>
    /// JSON payload received via STDOUT from the Python process. Same
    /// "one envelope, mostly-null fields" approach as AgentRequest.
    /// </summary>
    public class AgentResponse
    {
        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("response_text")]
        public string ResponseText { get; set; } = string.Empty;

        [JsonPropertyName("is_error")]
        public bool IsError { get; set; } = false;

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("chat_id")]
        public int? ChatId { get; set; }

        [JsonPropertyName("chat")]
        public ChatSummary? Chat { get; set; }

        [JsonPropertyName("chats")]
        public List<ChatSummary>? Chats { get; set; }

        [JsonPropertyName("messages")]
        public List<ChatMessageDto>? Messages { get; set; }

        [JsonPropertyName("files")]
        public List<ProjectFileEntry>? Files { get; set; }

        [JsonPropertyName("context_usage")]
        public ContextUsage? ContextUsage { get; set; }
    }

    public class ChatSummary
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("project_path")]
        public string? ProjectPath { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;
    }

    public class ChatMessageDto
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty; // "user" | "assistant" | "error"

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;
    }

    public class ProjectFileEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty; // relative to project root

        [JsonPropertyName("is_dir")]
        public bool IsDir { get; set; }
    }

    /// <summary>
    /// How full the model's context window is for a chat, right now.
    /// Token counts are a ~4-chars-per-token estimate (see assistant.py's
    /// estimate_tokens), not exact -- real tokenization varies by model.
    /// MaxTokensModel comes from Ollama's /api/show when reachable;
    /// otherwise it's a fallback derived from the character budget, and
    /// MaxTokensIsEstimate is true so the UI can say so.
    /// </summary>
    public class ContextUsage
    {
        [JsonPropertyName("message_count")]
        public int MessageCount { get; set; }

        [JsonPropertyName("kept_message_count")]
        public int KeptMessageCount { get; set; }

        [JsonPropertyName("used_chars")]
        public int UsedChars { get; set; }

        [JsonPropertyName("used_tokens_estimate")]
        public int UsedTokensEstimate { get; set; }

        [JsonPropertyName("max_chars")]
        public int MaxChars { get; set; }

        [JsonPropertyName("was_trimmed")]
        public bool WasTrimmed { get; set; }

        [JsonPropertyName("max_tokens_model")]
        public int MaxTokensModel { get; set; }

        [JsonPropertyName("max_tokens_is_estimate")]
        public bool MaxTokensIsEstimate { get; set; }

        [JsonPropertyName("agent_profile")]
        public string AgentProfile { get; set; } = "auto";

        [JsonPropertyName("max_output_tokens")]
        public int MaxOutputTokens { get; set; } = 8192;

        [JsonPropertyName("total_stored_messages")]
        public int TotalStoredMessages { get; set; }

        [JsonPropertyName("remembered_message_count")]
        public int RememberedMessageCount { get; set; }

        public double UsedFraction =>
            MaxTokensModel > 0 ? Math.Min(1.0, (double)UsedTokensEstimate / MaxTokensModel) : 0.0;
    }
}
