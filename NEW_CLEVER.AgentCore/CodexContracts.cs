using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NEW_CLEVER.AgentCore
{
    public enum CodexConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Error
    }

    public enum CodexThreadState
    {
        NotLoaded,
        Idle,
        Active,
        Error
    }

    public enum CodexBackendEventKind
    {
        ConnectionChanged,
        AccountUpdated,
        RateLimitsUpdated,
        ThreadStarted,
        ThreadUpdated,
        ThreadDeleted,
        TurnStarted,
        TurnCompleted,
        ItemStarted,
        ItemDelta,
        ItemCompleted,
        TokenUsageUpdated,
        Error
    }

    public enum CodexItemKind
    {
        Unknown,
        UserMessage,
        AgentMessage,
        Reasoning,
        Plan,
        CommandExecution,
        FileChange,
        McpToolCall,
        DynamicToolCall,
        ContextCompaction
    }

    public sealed class CodexCapabilities
    {
        public bool Threads { get; set; }
        public bool Turns { get; set; }
        public bool Streaming { get; set; }
        public bool Authentication { get; set; }
        public bool Models { get; set; }
        public bool Reasoning { get; set; }
        public bool Speed { get; set; }
        public bool Goals { get; set; }
        public bool Compact { get; set; }
        public bool Status { get; set; }
        public bool DynamicTools { get; set; }
        public bool ThreadSettings { get; set; }
        public bool ThreadDelete { get; set; }
        public bool PagedTurns { get; set; }

        public static CodexCapabilities None()
        {
            return new CodexCapabilities();
        }
    }

    public sealed class BackendResult<T>
    {
        public bool Success { get; set; }
        public bool Unsupported { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public T Data { get; set; }

        public static BackendResult<T> Ok(T data)
        {
            return new BackendResult<T> { Success = true, Code = "ok", Message = "", Data = data };
        }

        public static BackendResult<T> Fail(string code, string message, bool unsupported = false)
        {
            return new BackendResult<T>
            {
                Success = false,
                Unsupported = unsupported,
                Code = string.IsNullOrWhiteSpace(code) ? "backend_error" : code,
                Message = message ?? ""
            };
        }
    }

    public sealed class CodexRuntimeInfo
    {
        public string ExecutablePath { get; set; }
        public string Version { get; set; }
        public string Source { get; set; }
        public bool Compatible { get; set; }
    }

    public sealed class CodexRuntimeUpdateInfo
    {
        public bool UpdateAvailable { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string ReleaseNotes { get; set; }
        public string DownloadUrl { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class CodexAccountSnapshot
    {
        public bool RequiresOpenAiAuth { get; set; }
        public bool LoggedIn { get; set; }
        public string AuthMode { get; set; }
        public string Email { get; set; }
        public string PlanType { get; set; }
    }

    public sealed class CodexLoginStartResult
    {
        public string LoginId { get; set; }
        public string AuthorizationUrl { get; set; }
        public string VerificationUrl { get; set; }
        public string UserCode { get; set; }
    }

    public sealed class CodexRateLimitWindow
    {
        public int UsedPercent { get; set; }
        public long? ResetsAtUnixSeconds { get; set; }
        public long? WindowDurationMinutes { get; set; }
    }

    public sealed class CodexRateLimitSnapshot
    {
        public CodexRateLimitWindow Primary { get; set; }
        public CodexRateLimitWindow Secondary { get; set; }
        public bool HasCredits { get; set; }
        public bool UnlimitedCredits { get; set; }
        public string CreditBalance { get; set; }
    }

    public sealed class CodexTokenUsageSnapshot
    {
        public long TotalTokens { get; set; }
        public long ModelContextWindow { get; set; }

        public int UsedPercent
        {
            get
            {
                if (ModelContextWindow <= 0)
                    return 0;
                return (int)Math.Max(0, Math.Min(100, TotalTokens * 100 / ModelContextWindow));
            }
        }

        public static CodexTokenUsageSnapshot ParseNotification(JsonElement parameters)
        {
            JsonElement usage;
            if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("tokenUsage", out usage))
                parameters = usage;
            long context;
            if (!TryGetInt64(parameters, "modelContextWindow", out context) || context <= 0)
                return null;
            JsonElement total;
            // `last` is the current request/context footprint. `total` is lifetime
            // thread usage and must not drive the context-window indicator.
            if (!parameters.TryGetProperty("last", out total) && !parameters.TryGetProperty("totalTokenUsage", out total))
                return null;
            long used;
            if (!TryGetInt64(total, "totalTokens", out used))
                return null;
            return new CodexTokenUsageSnapshot
            {
                TotalTokens = Math.Max(0, used),
                ModelContextWindow = context
            };
        }

        private static bool TryGetInt64(JsonElement element, string property, out long value)
        {
            value = 0;
            JsonElement child;
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out child))
                return false;
            if (child.ValueKind == JsonValueKind.Number) return child.TryGetInt64(out value);
            return child.ValueKind == JsonValueKind.String && long.TryParse(child.GetString(), out value);
        }
    }

    public sealed class CodexThreadSummary
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Preview { get; set; }
        public string Cwd { get; set; }
        public string Source { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public CodexThreadState State { get; set; }
    }

    public sealed class CodexThreadPage
    {
        public CodexThreadPage()
        {
            Threads = new List<CodexThreadSummary>();
        }

        public IList<CodexThreadSummary> Threads { get; set; }
        public string NextCursor { get; set; }
    }

    public sealed class CodexMessage
    {
        public string Id { get; set; }
        public bool IsUser { get; set; }
        public string Markdown { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public sealed class CodexThreadItem
    {
        public CodexThreadItem()
        {
            Attachments = new List<CodexAttachment>();
        }

        public string Id { get; set; }
        public string TurnId { get; set; }
        public CodexItemKind Kind { get; set; }
        public string Status { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
        public string Details { get; set; }
        public string RawJson { get; set; }
        public long? TurnDurationMs { get; set; }
        public IList<CodexAttachment> Attachments { get; set; }
    }

    public sealed class CodexAttachment
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Path { get; set; }
    }

    public sealed class CodexThreadDetails
    {
        public CodexThreadDetails()
        {
            Messages = new List<CodexMessage>();
            Items = new List<CodexThreadItem>();
        }

        public CodexThreadSummary Thread { get; set; }
        public IList<CodexMessage> Messages { get; set; }
        public IList<CodexThreadItem> Items { get; set; }
        public string NextTurnsCursor { get; set; }
    }

    public sealed class CodexServiceTierInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsDefault { get; set; }
    }

    public sealed class CodexModelInfo
    {
        public CodexModelInfo()
        {
            SupportedReasoningEfforts = new List<string>();
            ServiceTiers = new List<CodexServiceTierInfo>();
        }

        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string DefaultReasoningEffort { get; set; }
        public string DefaultServiceTier { get; set; }
        public bool IsDefault { get; set; }
        // Null means that the connected app-server did not report input modalities.
        public bool? SupportsImageInput { get; set; }
        public IList<string> SupportedReasoningEfforts { get; set; }
        public IList<CodexServiceTierInfo> ServiceTiers { get; set; }
    }

    public sealed class CodexThreadSettings
    {
        public string Model { get; set; }
        public string ReasoningEffort { get; set; }
        public string ServiceTier { get; set; }
    }

    public sealed class CodexGoal
    {
        public string Objective { get; set; }
        public string Status { get; set; }
        public long? TokenBudget { get; set; }
        public long TokensUsed { get; set; }
        public long TimeUsedSeconds { get; set; }
    }

    public sealed class CodexStatusSnapshot
    {
        public CodexConnectionState ConnectionState { get; set; }
        public CodexRuntimeInfo Runtime { get; set; }
        public string Version { get; set; }
        public string Account { get; set; }
        public string PlanType { get; set; }
        public string Message { get; set; }
        public CodexCapabilities Capabilities { get; set; }
        public CodexThreadSettings Settings { get; set; }
        public CodexGoal Goal { get; set; }
        public CodexAccountSnapshot Auth { get; set; }
        public CodexRateLimitSnapshot RateLimits { get; set; }
        public CodexTokenUsageSnapshot TokenUsage { get; set; }
        public CodexThreadState ThreadState { get; set; }
        public int? ContextUsedPercent { get; set; }
        public int? PrimaryRateLimitUsedPercent { get; set; }
        public int? SecondaryRateLimitUsedPercent { get; set; }
    }

    public sealed class CodexThreadStartRequest
    {
        public string Cwd { get; set; }
        public string DeveloperInstructions { get; set; }
        public CodexThreadSettings Settings { get; set; }
        public bool ApprovalNever { get; set; }
        public bool WorkspaceWrite { get; set; }
        public bool NetworkAccess { get; set; }
    }

    public sealed class CodexTurnInput
    {
        public string Type { get; set; }
        public string Text { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
    }

    public sealed class CodexAdditionalContextEntry
    {
        public string Kind { get; set; }
        public string Value { get; set; }
    }

    public sealed class CodexTurnRequest
    {
        public CodexTurnRequest()
        {
            Inputs = new List<CodexTurnInput>();
            AdditionalContext = new Dictionary<string, CodexAdditionalContextEntry>(StringComparer.Ordinal);
        }

        public string ThreadId { get; set; }
        public string Cwd { get; set; }
        public string Text { get; set; }
        public IList<CodexTurnInput> Inputs { get; set; }
        public IDictionary<string, CodexAdditionalContextEntry> AdditionalContext { get; set; }
        public CodexThreadSettings Settings { get; set; }
        public bool NetworkAccess { get; set; }
    }

    public sealed class CodexTurnHandle
    {
        public string ThreadId { get; set; }
        public string TurnId { get; set; }
    }

    public sealed class CodexUserInputOption
    {
        public string Label { get; set; }
        public string Description { get; set; }
    }

    public sealed class CodexUserInputQuestion
    {
        public CodexUserInputQuestion()
        {
            Options = new List<CodexUserInputOption>();
        }

        public string Id { get; set; }
        public string Header { get; set; }
        public string Question { get; set; }
        public bool IsSecret { get; set; }
        public IList<CodexUserInputOption> Options { get; set; }
    }

    public sealed class CodexUserInputRequest
    {
        public CodexUserInputRequest()
        {
            Questions = new List<CodexUserInputQuestion>();
        }

        public string ThreadId { get; set; }
        public string TurnId { get; set; }
        public string ItemId { get; set; }
        public long? AutoResolutionMs { get; set; }
        public IList<CodexUserInputQuestion> Questions { get; set; }
    }

    public sealed class CodexUserInputResponse
    {
        public CodexUserInputResponse()
        {
            Answers = new Dictionary<string, IList<string>>(StringComparer.Ordinal);
        }

        public IDictionary<string, IList<string>> Answers { get; set; }
    }

    public sealed class CodexBackendEventArgs : EventArgs
    {
        public CodexBackendEventKind Kind { get; set; }
        public string ThreadId { get; set; }
        public string TurnId { get; set; }
        public string ItemId { get; set; }
        public string Delta { get; set; }
        public string Message { get; set; }
        public CodexThreadSummary Thread { get; set; }
        public CodexThreadItem Item { get; set; }
        public CodexAccountSnapshot Account { get; set; }
        public CodexRateLimitSnapshot RateLimits { get; set; }
        public CodexTokenUsageSnapshot TokenUsage { get; set; }
        public long? TurnDurationMs { get; set; }
        public int? ContextUsedPercent { get; set; }
        public string RawJson { get; set; }
    }

    public sealed class CodexMcpBridgeDescriptor
    {
        public CodexMcpBridgeDescriptor()
        {
            Arguments = new List<string>();
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Command { get; set; }
        public IList<string> Arguments { get; set; }
        public IDictionary<string, string> Environment { get; set; }
    }

    public interface ICodexMcpBridgeProvider
    {
        bool IsAvailable { get; }
        CodexMcpBridgeDescriptor GetDescriptor(string threadId, string projectRoot);
    }

    public interface ICodexRuntimeManager
    {
        Task<BackendResult<CodexRuntimeInfo>> ResolveAsync(CancellationToken cancellationToken);
        Task<BackendResult<CodexRuntimeUpdateInfo>> CheckForUpdateAsync(CancellationToken cancellationToken);
        Task<BackendResult<CodexRuntimeInfo>> InstallUpdateAsync(CodexRuntimeUpdateInfo update, CancellationToken cancellationToken);
    }

    public interface ICodexSessionBackend : IDisposable
    {
        CodexCapabilities Capabilities { get; }
        Func<CodexUserInputRequest, CancellationToken, Task<CodexUserInputResponse>> UserInputHandler { get; set; }
        event EventHandler StateChanged;
        event EventHandler<CodexBackendEventArgs> EventReceived;

        Task<BackendResult<CodexStatusSnapshot>> StartAsync(CancellationToken cancellationToken);
        Task<BackendResult<CodexStatusSnapshot>> RestartAsync(CancellationToken cancellationToken);
        Task<BackendResult<CodexLoginStartResult>> StartChatGptLoginAsync(CancellationToken cancellationToken);
        Task<BackendResult<bool>> CancelLoginAsync(string loginId, CancellationToken cancellationToken);
        Task<BackendResult<bool>> LogoutAsync(CancellationToken cancellationToken);
        Task<BackendResult<CodexRuntimeUpdateInfo>> CheckForUpdateAsync(CancellationToken cancellationToken);
        Task<BackendResult<CodexRuntimeInfo>> InstallUpdateAsync(CodexRuntimeUpdateInfo update, CancellationToken cancellationToken);
        Task<BackendResult<CodexThreadSummary>> StartThreadAsync(CodexThreadStartRequest request, CancellationToken cancellationToken);
        Task<BackendResult<CodexThreadPage>> ListThreadsAsync(string cwd, string cursor, string searchTerm, CancellationToken cancellationToken);
        Task<BackendResult<CodexThreadDetails>> ResumeThreadAsync(string threadId, CancellationToken cancellationToken);
        Task<BackendResult<CodexThreadDetails>> ListTurnsAsync(string threadId, string cursor, CancellationToken cancellationToken);
        Task<BackendResult<bool>> RenameThreadAsync(string threadId, string name, CancellationToken cancellationToken);
        Task<BackendResult<bool>> ArchiveThreadAsync(string threadId, CancellationToken cancellationToken);
        Task<BackendResult<bool>> DeleteThreadAsync(string threadId, CancellationToken cancellationToken);
        Task<BackendResult<CodexTurnHandle>> StartTurnAsync(CodexTurnRequest request, CancellationToken cancellationToken);
        Task<BackendResult<string>> SteerTurnAsync(string threadId, string expectedTurnId, IList<CodexTurnInput> input, CancellationToken cancellationToken);
        Task<BackendResult<bool>> InterruptTurnAsync(string threadId, string turnId, CancellationToken cancellationToken);
        Task<BackendResult<IList<CodexModelInfo>>> ListModelsAsync(CancellationToken cancellationToken);
        Task<BackendResult<bool>> UpdateSettingsAsync(string threadId, CodexThreadSettings settings, CancellationToken cancellationToken);
        Task<BackendResult<CodexGoal>> GetGoalAsync(string threadId, CancellationToken cancellationToken);
        Task<BackendResult<CodexGoal>> SetGoalAsync(string threadId, CodexGoal goal, CancellationToken cancellationToken);
        Task<BackendResult<bool>> ClearGoalAsync(string threadId, CancellationToken cancellationToken);
        Task<BackendResult<bool>> CompactAsync(string threadId, CancellationToken cancellationToken);
        Task<BackendResult<CodexStatusSnapshot>> GetStatusAsync(string threadId, CancellationToken cancellationToken);
    }

    public sealed class DisconnectedCodexSessionBackend : ICodexSessionBackend
    {
        private readonly string _message;

        public DisconnectedCodexSessionBackend(string message = null)
        {
            _message = string.IsNullOrWhiteSpace(message)
                ? "Codex app-server is not connected."
                : message;
            Capabilities = CodexCapabilities.None();
        }

        public CodexCapabilities Capabilities { get; private set; }
        public Func<CodexUserInputRequest, CancellationToken, Task<CodexUserInputResponse>> UserInputHandler { get; set; }
        public event EventHandler StateChanged { add { } remove { } }
        public event EventHandler<CodexBackendEventArgs> EventReceived { add { } remove { } }

        public Task<BackendResult<CodexStatusSnapshot>> StartAsync(CancellationToken cancellationToken) { return Task.FromResult(Status()); }
        public Task<BackendResult<CodexStatusSnapshot>> RestartAsync(CancellationToken cancellationToken) { return Task.FromResult(Status()); }
        public Task<BackendResult<CodexLoginStartResult>> StartChatGptLoginAsync(CancellationToken cancellationToken) { return Missing<CodexLoginStartResult>(); }
        public Task<BackendResult<bool>> CancelLoginAsync(string loginId, CancellationToken cancellationToken) { return Missing<bool>(); }
        public Task<BackendResult<bool>> LogoutAsync(CancellationToken cancellationToken) { return Missing<bool>(); }
        public Task<BackendResult<CodexRuntimeUpdateInfo>> CheckForUpdateAsync(CancellationToken cancellationToken) { return Missing<CodexRuntimeUpdateInfo>(); }
        public Task<BackendResult<CodexRuntimeInfo>> InstallUpdateAsync(CodexRuntimeUpdateInfo update, CancellationToken cancellationToken) { return Missing<CodexRuntimeInfo>(); }
        public Task<BackendResult<CodexThreadSummary>> StartThreadAsync(CodexThreadStartRequest request, CancellationToken cancellationToken) { return Missing<CodexThreadSummary>(); }
        public Task<BackendResult<CodexThreadPage>> ListThreadsAsync(string cwd, string cursor, string searchTerm, CancellationToken cancellationToken) { return Missing<CodexThreadPage>(); }
        public Task<BackendResult<CodexThreadDetails>> ResumeThreadAsync(string threadId, CancellationToken cancellationToken) { return Missing<CodexThreadDetails>(); }
        public Task<BackendResult<CodexThreadDetails>> ListTurnsAsync(string threadId, string cursor, CancellationToken cancellationToken) { return Missing<CodexThreadDetails>(); }
        public Task<BackendResult<bool>> RenameThreadAsync(string threadId, string name, CancellationToken cancellationToken) { return Missing<bool>(); }
        public Task<BackendResult<bool>> ArchiveThreadAsync(string threadId, CancellationToken cancellationToken) { return Missing<bool>(); }
        public Task<BackendResult<bool>> DeleteThreadAsync(string threadId, CancellationToken cancellationToken) { return Missing<bool>(); }
        public Task<BackendResult<CodexTurnHandle>> StartTurnAsync(CodexTurnRequest request, CancellationToken cancellationToken) { return Missing<CodexTurnHandle>(); }
        public Task<BackendResult<string>> SteerTurnAsync(string threadId, string expectedTurnId, IList<CodexTurnInput> input, CancellationToken cancellationToken) { return Missing<string>(); }
        public Task<BackendResult<bool>> InterruptTurnAsync(string threadId, string turnId, CancellationToken cancellationToken) { return Missing<bool>(); }
        public Task<BackendResult<IList<CodexModelInfo>>> ListModelsAsync(CancellationToken cancellationToken) { return Missing<IList<CodexModelInfo>>(); }
        public Task<BackendResult<bool>> UpdateSettingsAsync(string threadId, CodexThreadSettings settings, CancellationToken cancellationToken) { return Missing<bool>(); }
        public Task<BackendResult<CodexGoal>> GetGoalAsync(string threadId, CancellationToken cancellationToken) { return Missing<CodexGoal>(); }
        public Task<BackendResult<CodexGoal>> SetGoalAsync(string threadId, CodexGoal goal, CancellationToken cancellationToken) { return Missing<CodexGoal>(); }
        public Task<BackendResult<bool>> ClearGoalAsync(string threadId, CancellationToken cancellationToken) { return Missing<bool>(); }
        public Task<BackendResult<bool>> CompactAsync(string threadId, CancellationToken cancellationToken) { return Missing<bool>(); }
        public Task<BackendResult<CodexStatusSnapshot>> GetStatusAsync(string threadId, CancellationToken cancellationToken) { return Task.FromResult(Status()); }

        private BackendResult<CodexStatusSnapshot> Status()
        {
            return BackendResult<CodexStatusSnapshot>.Ok(new CodexStatusSnapshot
            {
                ConnectionState = CodexConnectionState.Disconnected,
                Capabilities = Capabilities,
                Message = _message,
                Settings = new CodexThreadSettings()
            });
        }

        private Task<BackendResult<T>> Missing<T>()
        {
            return Task.FromResult(BackendResult<T>.Fail("codex_not_connected", _message, true));
        }

        public void Dispose() { }
    }
}
