using NEW_CLEVER.AgentCore;
using NEW_CLEVER.ViewModel;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NEW_CLEVER.Model.Agent
{
    /// <summary>
    /// Production Codex app-server session backend. It owns one process connection and never
    /// replays turns or tool calls after overloads or process restarts.
    /// </summary>
    public sealed class CodexAppServerBackend : ICodexSessionBackend
    {
        private readonly ICodexRuntimeManager _runtimeManager;
        private readonly ICodexMcpBridgeProvider _bridgeProvider;
        private readonly IAgentToolRegistry _toolRegistry;
        private readonly SemaphoreSlim _lifecycleGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly object _stateSync = new object();
        private readonly Dictionary<string, CodexThreadSettings> _threadSettings =
            new Dictionary<string, CodexThreadSettings>(StringComparer.Ordinal);
        private readonly Dictionary<string, CodexGoal> _threadGoals =
            new Dictionary<string, CodexGoal>(StringComparer.Ordinal);
        private readonly Dictionary<string, CodexTokenUsageSnapshot> _threadTokenUsage =
            new Dictionary<string, CodexTokenUsageSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _threadCwds =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, CodexThreadState> _threadStates =
            new Dictionary<string, CodexThreadState>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ActiveToolCall> _activeToolCalls =
            new ConcurrentDictionary<string, ActiveToolCall>(StringComparer.Ordinal);
        private readonly HashSet<string> _liveDynamicToolThreads =
            new HashSet<string>(StringComparer.Ordinal);

        private CodexAppServerProcessHost _host;
        private CodexJsonRpcTransport _transport;
        private CodexRuntimeInfo _runtime;
        private CodexConnectionState _connectionState = CodexConnectionState.Disconnected;
        private CodexAccountSnapshot _account;
        private CodexRateLimitSnapshot _rateLimits;
        private string _lastMessage = "Codex app-server is not connected.";
        private string _activeThreadId;
        private string _activeTurnId;
        private int _recoveryScheduled;
        private int _disposed;
        private bool _stopping;
        private CancellationTokenSource _connectionCancellation;

        public CodexAppServerBackend(
            ICodexRuntimeManager runtimeManager,
            ICodexMcpBridgeProvider bridgeProvider,
            IAgentToolRegistry toolRegistry)
        {
            _runtimeManager = runtimeManager ?? throw new ArgumentNullException("runtimeManager");
            _bridgeProvider = bridgeProvider;
            _toolRegistry = toolRegistry ?? throw new ArgumentNullException("toolRegistry");
            Capabilities = CodexCapabilities.None();
        }

        public CodexCapabilities Capabilities { get; private set; }
        public Func<CodexUserInputRequest, CancellationToken, Task<CodexUserInputResponse>> UserInputHandler { get; set; }
        public event EventHandler StateChanged;
        public event EventHandler<CodexBackendEventArgs> EventReceived;

        public async Task<BackendResult<CodexStatusSnapshot>> StartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_connectionState == CodexConnectionState.Connected && _transport != null)
                    return BackendResult<CodexStatusSnapshot>.Ok(BuildStatus(null));

                StopConnection();
                SetConnectionState(CodexConnectionState.Connecting, "Starting Codex app-server...");
                var resolved = await _runtimeManager.ResolveAsync(cancellationToken).ConfigureAwait(false);
                if (!resolved.Success || resolved.Data == null)
                {
                    SetConnectionState(CodexConnectionState.Error, resolved.Message);
                    return BackendResult<CodexStatusSnapshot>.Fail(resolved.Code, resolved.Message);
                }
                if (!resolved.Data.Compatible)
                {
                    const string message = "Codex app-server 0.144.1 or newer is required.";
                    SetConnectionState(CodexConnectionState.Error, message);
                    return BackendResult<CodexStatusSnapshot>.Fail("codex_version_unsupported", message, true);
                }

                await StartConnectionAsync(resolved.Data, cancellationToken).ConfigureAwait(false);
                return BackendResult<CodexStatusSnapshot>.Ok(BuildStatus(null));
            }
            catch (Exception ex)
            {
                SetConnectionState(CodexConnectionState.Error, ex.Message);
                return Failure<CodexStatusSnapshot>(ex);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task<BackendResult<CodexStatusSnapshot>> RestartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ClearActiveTurnAndRaiseError("The active Codex turn was stopped because app-server was restarted.");
                StopConnection();
                lock (_stateSync)
                    _liveDynamicToolThreads.Clear();
                SetConnectionState(CodexConnectionState.Connecting, "Restarting Codex app-server...");
                var resolved = await _runtimeManager.ResolveAsync(cancellationToken).ConfigureAwait(false);
                if (!resolved.Success || resolved.Data == null)
                {
                    SetConnectionState(CodexConnectionState.Error, resolved.Message);
                    return BackendResult<CodexStatusSnapshot>.Fail(resolved.Code, resolved.Message);
                }
                if (!resolved.Data.Compatible)
                    return BackendResult<CodexStatusSnapshot>.Fail(
                        "codex_version_unsupported",
                        "Codex app-server 0.144.1 or newer is required.",
                        true);
                await StartConnectionAsync(resolved.Data, cancellationToken).ConfigureAwait(false);
                return BackendResult<CodexStatusSnapshot>.Ok(BuildStatus(null));
            }
            catch (Exception ex)
            {
                SetConnectionState(CodexConnectionState.Error, ex.Message);
                return Failure<CodexStatusSnapshot>(ex);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private async Task StartConnectionAsync(CodexRuntimeInfo runtime, CancellationToken cancellationToken)
        {
            var host = new CodexAppServerProcessHost();
            host.UnexpectedExit += OnUnexpectedExit;
            await host.StartAsync(runtime, cancellationToken).ConfigureAwait(false);

            var transport = host.Transport;
            transport.NotificationReceived += OnNotification;
            transport.ServerRequestReceived += OnServerRequestAsync;
            transport.Faulted += OnTransportFaulted;
            _runtime = runtime;
            _host = host;
            _transport = transport;
            var previousCancellation = Interlocked.Exchange(
                ref _connectionCancellation,
                new CancellationTokenSource());
            if (previousCancellation != null)
            {
                previousCancellation.Cancel();
                previousCancellation.Dispose();
            }

            try
            {
                await transport.SendRequestAsync(
                    "initialize",
                    new Dictionary<string, object>
                    {
                        { "clientInfo", new Dictionary<string, object>
                            {
                                { "name", "NEW_CLEVER_ide" },
                                { "title", "NEW_CLEVER IDE" },
                                { "version", GetClientVersion() }
                            }
                        },
                        { "capabilities", new Dictionary<string, object>
                            {
                                { "experimentalApi", true }
                            }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                await transport.SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);
                Capabilities = CreateFullCapabilities();
                SetConnectionState(CodexConnectionState.Connected, "Codex app-server is connected.");
                await RefreshAccountAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var stderr = host.StderrTail;
                StopConnection();
                if (!string.IsNullOrWhiteSpace(stderr))
                    throw new InvalidOperationException(ex.Message + Environment.NewLine + stderr, ex);
                throw;
            }
        }

        public async Task<BackendResult<CodexLoginStartResult>> StartChatGptLoginAsync(CancellationToken cancellationToken)
        {
            try
            {
                var result = await InvokeAsync("account/login/start", new Dictionary<string, object>
                {
                    { "type", "chatgpt" },
                    { "useHostedLoginSuccessPage", true },
                    { "appBrand", "codex" }
                }, false, cancellationToken).ConfigureAwait(false);
                return BackendResult<CodexLoginStartResult>.Ok(new CodexLoginStartResult
                {
                    LoginId = GetString(result, "loginId"),
                    AuthorizationUrl = FirstString(result, "authUrl", "authorizationUrl"),
                    VerificationUrl = GetString(result, "verificationUrl"),
                    UserCode = GetString(result, "userCode")
                });
            }
            catch (Exception ex) { return Failure<CodexLoginStartResult>(ex); }
        }

        public async Task<BackendResult<bool>> CancelLoginAsync(string loginId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(loginId))
                return BackendResult<bool>.Fail("login_id_required", "A Codex login id is required.");
            return await EmptyMutationAsync("account/login/cancel", new Dictionary<string, object>
            {
                { "loginId", loginId }
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<BackendResult<bool>> LogoutAsync(CancellationToken cancellationToken)
        {
            var result = await EmptyMutationAsync("account/logout", null, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                lock (_stateSync) _account = new CodexAccountSnapshot { RequiresOpenAiAuth = true };
                RaiseStateChanged();
            }
            return result;
        }

        public Task<BackendResult<CodexRuntimeUpdateInfo>> CheckForUpdateAsync(CancellationToken cancellationToken)
        {
            return _runtimeManager.CheckForUpdateAsync(cancellationToken);
        }

        public async Task<BackendResult<CodexRuntimeInfo>> InstallUpdateAsync(
            CodexRuntimeUpdateInfo update,
            CancellationToken cancellationToken)
        {
            return await _runtimeManager.InstallUpdateAsync(update, cancellationToken).ConfigureAwait(false);
        }

        public async Task<BackendResult<CodexThreadSummary>> StartThreadAsync(
            CodexThreadStartRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Cwd))
                return BackendResult<CodexThreadSummary>.Fail("project_required", "An active project root is required.");
            if (!IsChatGptAuthenticated())
                return BackendResult<CodexThreadSummary>.Fail("chatgpt_login_required", "Sign in with ChatGPT before starting a Codex chat.");
            try
            {
                var cwd = NormalizeAbsolutePath(request.Cwd);
                var parameters = new Dictionary<string, object>
                {
                    { "cwd", cwd },
                    { "runtimeWorkspaceRoots", new[] { cwd } },
                    { "approvalPolicy", request.ApprovalNever ? "never" : "on-request" },
                    { "sandbox", request.WorkspaceWrite ? "workspace-write" : "read-only" },
                    { "developerInstructions", request.DeveloperInstructions },
                    { "serviceName", "NEW_CLEVER_ide" },
                    { "dynamicTools", ParseJsonValue(DynamicToolWireAdapter.CreateDefinitionsJson(
                        _toolRegistry.Definitions,
                        true)) }
                };
                AddThreadSettings(parameters, request.Settings);
                var result = await InvokeAsync("thread/start", parameters, false, cancellationToken).ConfigureAwait(false);
                var threadElement = GetObject(result, "thread");
                var thread = ParseThread(threadElement);
                if (thread == null || string.IsNullOrWhiteSpace(thread.Id))
                    throw new InvalidDataException("thread/start returned no thread id.");
                lock (_stateSync)
                {
                    _liveDynamicToolThreads.Add(thread.Id);
                    _threadSettings[thread.Id] = CloneSettings(request.Settings);
                    _threadCwds[thread.Id] = cwd;
                }
                return BackendResult<CodexThreadSummary>.Ok(thread);
            }
            catch (Exception ex) { return Failure<CodexThreadSummary>(ex); }
        }

        public async Task<BackendResult<CodexThreadPage>> ListThreadsAsync(
            string cwd,
            string cursor,
            string searchTerm,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(cwd))
                return BackendResult<CodexThreadPage>.Fail("project_required", "An active project root is required.");
            try
            {
                var normalizedCwd = NormalizeAbsolutePath(cwd);
                var parameters = new Dictionary<string, object>
                {
                    { "cwd", normalizedCwd },
                    { "limit", 50 },
                    { "sortKey", "updated_at" },
                    { "sortDirection", "desc" }
                };
                AddIfNotBlank(parameters, "cursor", cursor);
                AddIfNotBlank(parameters, "searchTerm", searchTerm);
                var result = await InvokeAsync("thread/list", parameters, true, cancellationToken).ConfigureAwait(false);
                var page = new CodexThreadPage { NextCursor = GetString(result, "nextCursor") };
                JsonElement data;
                if (result.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var thread = ParseThread(item);
                        if (thread != null && PathsEqual(thread.Cwd, normalizedCwd))
                        {
                            page.Threads.Add(thread);
                            if (!string.IsNullOrWhiteSpace(thread.Id))
                                lock (_stateSync)
                                {
                                    _threadCwds[thread.Id] = normalizedCwd;
                                    _threadStates[thread.Id] = thread.State;
                                }
                        }
                    }
                }
                return BackendResult<CodexThreadPage>.Ok(page);
            }
            catch (Exception ex) { return Failure<CodexThreadPage>(ex); }
        }

        public async Task<BackendResult<CodexThreadDetails>> ResumeThreadAsync(
            string threadId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(threadId))
                return BackendResult<CodexThreadDetails>.Fail("thread_required", "A thread id is required.");
            try
            {
                string knownCwd;
                lock (_stateSync) _threadCwds.TryGetValue(threadId, out knownCwd);
                if (string.IsNullOrWhiteSpace(knownCwd))
                {
                    var read = await InvokeAsync("thread/read", new Dictionary<string, object>
                    {
                        { "threadId", threadId },
                        { "includeTurns", false }
                    }, true, cancellationToken).ConfigureAwait(false);
                    var storedThread = ParseThread(GetObject(read, "thread"));
                    knownCwd = storedThread == null || string.IsNullOrWhiteSpace(storedThread.Cwd)
                        ? null
                        : NormalizeAbsolutePath(storedThread.Cwd);
                    if (!string.IsNullOrWhiteSpace(knownCwd))
                        lock (_stateSync) _threadCwds[threadId] = knownCwd;
                }
                var parameters = new Dictionary<string, object>
                {
                    { "threadId", threadId },
                    { "excludeTurns", true }
                };
                bool live;
                lock (_stateSync) live = _liveDynamicToolThreads.Contains(threadId);
                if (!live)
                    AddColdResumeBridgeConfig(parameters, threadId, knownCwd);

                var result = await InvokeAsync("thread/resume", parameters, false, cancellationToken).ConfigureAwait(false);
                var details = ParseThreadDetails(result, threadId);
                lock (_stateSync)
                {
                    if (details.Thread != null && !string.IsNullOrWhiteSpace(details.Thread.Cwd))
                        _threadCwds[threadId] = NormalizeAbsolutePath(details.Thread.Cwd);
                    _threadSettings[threadId] = ParseEffectiveThreadSettings(result);
                    if (details.Thread != null)
                        _threadStates[threadId] = details.Thread.State;
                }
                var turns = await ListTurnsAsync(threadId, null, cancellationToken).ConfigureAwait(false);
                if (turns.Success && turns.Data != null)
                {
                    details.Items = turns.Data.Items;
                    details.Messages = turns.Data.Messages;
                    details.NextTurnsCursor = turns.Data.NextTurnsCursor;
                }
                return BackendResult<CodexThreadDetails>.Ok(details);
            }
            catch (Exception ex) { return Failure<CodexThreadDetails>(ex); }
        }

        public async Task<BackendResult<CodexThreadDetails>> ListTurnsAsync(
            string threadId,
            string cursor,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(threadId))
                return BackendResult<CodexThreadDetails>.Fail("thread_required", "A thread id is required.");
            try
            {
                var parameters = new Dictionary<string, object>
                {
                    { "threadId", threadId },
                    { "limit", 30 },
                    { "sortDirection", "desc" },
                    { "itemsView", "full" }
                };
                AddIfNotBlank(parameters, "cursor", cursor);
                var result = await InvokeAsync("thread/turns/list", parameters, true, cancellationToken).ConfigureAwait(false);
                var details = new CodexThreadDetails { NextTurnsCursor = GetString(result, "nextCursor") };
                JsonElement turns;
                if (result.TryGetProperty("data", out turns) && turns.ValueKind == JsonValueKind.Array)
                    ParseTurns(turns, details);
                return BackendResult<CodexThreadDetails>.Ok(details);
            }
            catch (Exception ex) { return Failure<CodexThreadDetails>(ex); }
        }

        public Task<BackendResult<bool>> RenameThreadAsync(
            string threadId,
            string name,
            CancellationToken cancellationToken)
        {
            return EmptyMutationAsync("thread/name/set", new Dictionary<string, object>
            {
                { "threadId", threadId },
                { "name", name ?? "" }
            }, cancellationToken);
        }

        public Task<BackendResult<bool>> ArchiveThreadAsync(string threadId, CancellationToken cancellationToken)
        {
            return EmptyMutationAsync("thread/archive", new Dictionary<string, object>
            {
                { "threadId", threadId }
            }, cancellationToken);
        }

        public Task<BackendResult<bool>> DeleteThreadAsync(string threadId, CancellationToken cancellationToken)
        {
            return DeleteThreadCoreAsync(threadId, cancellationToken);
        }

        private async Task<BackendResult<bool>> DeleteThreadCoreAsync(string threadId, CancellationToken cancellationToken)
        {
            var result = await EmptyMutationAsync("thread/delete", new Dictionary<string, object>
            {
                { "threadId", threadId }
            }, cancellationToken).ConfigureAwait(false);
            if (result.Success) RemoveThreadState(threadId);
            return result;
        }

        public async Task<BackendResult<CodexTurnHandle>> StartTurnAsync(
            CodexTurnRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ThreadId))
                return BackendResult<CodexTurnHandle>.Fail("thread_required", "A thread id is required.");
            if (!IsChatGptAuthenticated())
                return BackendResult<CodexTurnHandle>.Fail("chatgpt_login_required", "Sign in with ChatGPT before sending a Codex turn.");
            try
            {
                var cwd = NormalizeAbsolutePath(request.Cwd);
                ValidateTurnInputPaths(request, cwd);
                string expectedCwd;
                lock (_stateSync) _threadCwds.TryGetValue(request.ThreadId, out expectedCwd);
                if (!string.IsNullOrWhiteSpace(expectedCwd) && !PathsEqual(expectedCwd, cwd))
                    return BackendResult<CodexTurnHandle>.Fail("thread_project_mismatch", "The Codex thread belongs to a different project.");
                if (!PathsEqual(MainWindowVM.CurrentProjectRoot, cwd))
                    return BackendResult<CodexTurnHandle>.Fail("active_project_mismatch", "The active NEW_CLEVER project changed before the turn started.");
                var parameters = new Dictionary<string, object>
                {
                    { "threadId", request.ThreadId },
                    { "input", BuildTurnInput(request) },
                    { "cwd", cwd },
                    { "runtimeWorkspaceRoots", new[] { cwd } },
                    { "approvalPolicy", "never" },
                    { "additionalContext", request.AdditionalContext ??
                        new Dictionary<string, CodexAdditionalContextEntry>(StringComparer.Ordinal) },
                    { "sandboxPolicy", new Dictionary<string, object>
                        {
                            { "type", "workspaceWrite" },
                            { "writableRoots", new[] { cwd } },
                            { "networkAccess", request.NetworkAccess },
                            { "excludeTmpdirEnvVar", true },
                            { "excludeSlashTmp", true }
                        }
                    }
                };
                AddTurnSettings(parameters, request.Settings);
                var result = await InvokeAsync("turn/start", parameters, false, cancellationToken).ConfigureAwait(false);
                var turn = GetObject(result, "turn");
                var turnId = GetString(turn, "id");
                if (string.IsNullOrWhiteSpace(turnId))
                    throw new InvalidDataException("turn/start returned no turn id.");
                lock (_stateSync) _threadSettings[request.ThreadId] = CloneSettings(request.Settings);
                lock (_stateSync)
                {
                    _activeThreadId = request.ThreadId;
                    _activeTurnId = turnId;
                }
                return BackendResult<CodexTurnHandle>.Ok(new CodexTurnHandle
                {
                    ThreadId = request.ThreadId,
                    TurnId = turnId
                });
            }
            catch (Exception ex) { return Failure<CodexTurnHandle>(ex); }
        }

        public async Task<BackendResult<bool>> InterruptTurnAsync(
            string threadId,
            string turnId,
            CancellationToken cancellationToken)
        {
            CancelActiveToolCalls(turnId);
            return await EmptyMutationAsync("turn/interrupt", new Dictionary<string, object>
            {
                { "threadId", threadId },
                { "turnId", turnId }
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<BackendResult<string>> SteerTurnAsync(
            string threadId,
            string expectedTurnId,
            IList<CodexTurnInput> input,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(expectedTurnId))
                return BackendResult<string>.Fail("active_turn_required", "An active Codex turn is required.");
            try
            {
                var result = await InvokeAsync("turn/steer", new Dictionary<string, object>
                {
                    { "threadId", threadId },
                    { "expectedTurnId", expectedTurnId },
                    { "input", BuildTurnInput(new CodexTurnRequest { Inputs = input ?? new List<CodexTurnInput>() }) }
                }, false, cancellationToken).ConfigureAwait(false);
                var acceptedTurnId = GetString(result, "turnId");
                return string.IsNullOrWhiteSpace(acceptedTurnId)
                    ? BackendResult<string>.Fail("steer_response_invalid", "Codex returned no active turn id.")
                    : BackendResult<string>.Ok(acceptedTurnId);
            }
            catch (Exception ex) { return Failure<string>(ex); }
        }

        public async Task<BackendResult<IList<CodexModelInfo>>> ListModelsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var result = await InvokeAsync("model/list", new Dictionary<string, object>
                {
                    { "includeHidden", false }
                }, true, cancellationToken).ConfigureAwait(false);
                var models = new List<CodexModelInfo>();
                JsonElement data;
                if (result.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                        models.Add(ParseModel(item));
                }
                return BackendResult<IList<CodexModelInfo>>.Ok(models);
            }
            catch (Exception ex) { return Failure<IList<CodexModelInfo>>(ex); }
        }

        public async Task<BackendResult<bool>> UpdateSettingsAsync(
            string threadId,
            CodexThreadSettings settings,
            CancellationToken cancellationToken)
        {
            var parameters = new Dictionary<string, object> { { "threadId", threadId } };
            if (settings != null)
            {
                parameters["model"] = settings.Model;
                parameters["effort"] = settings.ReasoningEffort;
                parameters["serviceTier"] = string.IsNullOrWhiteSpace(settings.ServiceTier)
                    ? null
                    : (object)settings.ServiceTier;
            }
            var result = await EmptyMutationAsync("thread/settings/update", parameters, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                lock (_stateSync) _threadSettings[threadId] = CloneSettings(settings);
            }
            return result;
        }

        public async Task<BackendResult<CodexGoal>> GetGoalAsync(string threadId, CancellationToken cancellationToken)
        {
            try
            {
                var result = await InvokeAsync("thread/goal/get", new Dictionary<string, object>
                {
                    { "threadId", threadId }
                }, true, cancellationToken).ConfigureAwait(false);
                JsonElement goalElement;
                var goal = result.TryGetProperty("goal", out goalElement) && goalElement.ValueKind == JsonValueKind.Object
                    ? ParseGoal(goalElement)
                    : null;
                lock (_stateSync)
                {
                    if (goal == null) _threadGoals.Remove(threadId); else _threadGoals[threadId] = goal;
                }
                return BackendResult<CodexGoal>.Ok(goal);
            }
            catch (Exception ex) { return Failure<CodexGoal>(ex); }
        }

        public async Task<BackendResult<CodexGoal>> SetGoalAsync(
            string threadId,
            CodexGoal goal,
            CancellationToken cancellationToken)
        {
            if (goal == null || string.IsNullOrWhiteSpace(goal.Objective))
                return BackendResult<CodexGoal>.Fail("goal_required", "A goal objective is required.");
            try
            {
                var parameters = new Dictionary<string, object>
                {
                    { "threadId", threadId },
                    { "objective", goal.Objective }
                };
                if (goal.TokenBudget.HasValue) parameters["tokenBudget"] = goal.TokenBudget.Value;
                if (!string.IsNullOrWhiteSpace(goal.Status)) parameters["status"] = goal.Status;
                var result = await InvokeAsync("thread/goal/set", parameters, false, cancellationToken).ConfigureAwait(false);
                var parsed = ParseGoal(GetObject(result, "goal"));
                lock (_stateSync) _threadGoals[threadId] = parsed;
                return BackendResult<CodexGoal>.Ok(parsed);
            }
            catch (Exception ex) { return Failure<CodexGoal>(ex); }
        }

        public async Task<BackendResult<bool>> ClearGoalAsync(string threadId, CancellationToken cancellationToken)
        {
            var result = await EmptyMutationAsync("thread/goal/clear", new Dictionary<string, object>
            {
                { "threadId", threadId }
            }, cancellationToken).ConfigureAwait(false);
            if (result.Success) lock (_stateSync) _threadGoals.Remove(threadId);
            return result;
        }

        public Task<BackendResult<bool>> CompactAsync(string threadId, CancellationToken cancellationToken)
        {
            return EmptyMutationAsync("thread/compact/start", new Dictionary<string, object>
            {
                { "threadId", threadId }
            }, cancellationToken);
        }

        public Task<BackendResult<CodexStatusSnapshot>> GetStatusAsync(
            string threadId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(BackendResult<CodexStatusSnapshot>.Ok(BuildStatus(threadId)));
        }

        private async Task RefreshAccountAsync(CancellationToken cancellationToken)
        {
            try
            {
                var accountResult = await InvokeAsync("account/read", new Dictionary<string, object>
                {
                    { "refreshToken", false }
                }, true, cancellationToken).ConfigureAwait(false);
                var account = ParseAccount(accountResult);
                lock (_stateSync) _account = account;
                RaiseEvent(new CodexBackendEventArgs
                {
                    Kind = CodexBackendEventKind.AccountUpdated,
                    Account = account,
                    RawJson = accountResult.GetRawText()
                });

                if (account.LoggedIn && string.Equals(account.AuthMode, "chatgpt", StringComparison.OrdinalIgnoreCase))
                {
                    var limitsResult = await InvokeAsync("account/rateLimits/read", null, true, cancellationToken).ConfigureAwait(false);
                    var limits = ParseRateLimits(limitsResult);
                    lock (_stateSync) _rateLimits = limits;
                    RaiseEvent(new CodexBackendEventArgs
                    {
                        Kind = CodexBackendEventKind.RateLimitsUpdated,
                        RateLimits = limits,
                        RawJson = limitsResult.GetRawText()
                    });
                }
                RaiseStateChanged();
            }
            catch (CodexRpcException ex) when (ex.Code == -32601)
            {
                DisableCapability("account/read");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Account/rate-limit refresh is supplementary. A transient upstream failure must
                // not tear down an otherwise healthy local app-server transport.
                RaiseEvent(new CodexBackendEventArgs
                {
                    Kind = CodexBackendEventKind.Error,
                    Message = "Codex account status could not be refreshed: " + ex.Message
                });
            }
        }

        private async Task<JsonElement> InvokeAsync(
            string method,
            object parameters,
            bool safeRead,
            CancellationToken cancellationToken)
        {
            var transport = _transport;
            if (_connectionState != CodexConnectionState.Connected || transport == null)
                throw new InvalidOperationException("Codex app-server is not connected.");

            var attempts = safeRead ? 3 : 1;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    return await transport.SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
                }
                catch (CodexRpcException ex) when (ex.Code == -32001 && safeRead && attempt + 1 < attempts)
                {
                    var delay = 150 * (1 << attempt) + new Random(unchecked(Environment.TickCount + attempt)).Next(20, 100);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (CodexRpcException ex) when (ex.Code == -32601)
                {
                    DisableCapability(method);
                    throw;
                }
            }
            throw new InvalidOperationException("Codex app-server request failed.");
        }

        private async Task<BackendResult<bool>> EmptyMutationAsync(
            string method,
            object parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                await InvokeAsync(method, parameters, false, cancellationToken).ConfigureAwait(false);
                return BackendResult<bool>.Ok(true);
            }
            catch (Exception ex) { return Failure<bool>(ex); }
        }

        private async Task<CodexRpcServerResponse> OnServerRequestAsync(CodexRpcServerRequest request)
        {
            if (string.Equals(request.Method, "item/tool/call", StringComparison.Ordinal))
            {
                var threadId = GetString(request.Params, "threadId");
                string scopeError;
                if (!IsActiveProjectScope(threadId, out scopeError))
                    return CodexRpcServerResponse.Error(-32003, scopeError);
                var namespaceName = GetString(request.Params, "namespace");
                var toolName = GetString(request.Params, "tool");
                JsonElement arguments;
                var argumentsJson = request.Params.TryGetProperty("arguments", out arguments)
                    ? arguments.GetRawText()
                    : "{}";
                var qualifiedName = DynamicToolWireAdapter.ResolveQualifiedName(namespaceName, toolName, true);
                var active = BeginActiveToolCall(request, "dynamic");
                try
                {
                    var result = await _toolRegistry.ExecuteAsync(qualifiedName, argumentsJson, active.Cancellation.Token)
                        .ConfigureAwait(false);
                    return CodexRpcServerResponse.Ok(ParseJsonValue(
                        DynamicToolWireAdapter.CreateToolCallResponseJson(result)));
                }
                finally { EndActiveToolCall(active); }
            }

            if (string.Equals(request.Method, "item/permissions/requestApproval", StringComparison.Ordinal))
            {
                return CodexRpcServerResponse.Error(-32004,
                    "NEW_CLEVER does not grant app-server permissions. Use the scoped NEW_CLEVER tools instead.");
            }

            if (request.Method.IndexOf("requestApproval", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return CodexRpcServerResponse.Ok(new Dictionary<string, object> { { "decision", "decline" } });
            }

            if (string.Equals(request.Method, "item/tool/requestUserInput", StringComparison.Ordinal) ||
                string.Equals(request.Method, "tool/requestUserInput", StringComparison.Ordinal))
            {
                var prompt = ParseUserInputRequest(request.Params);
                string scopeError;
                if (!IsActiveProjectScope(prompt.ThreadId, out scopeError))
                    return CodexRpcServerResponse.Error(-32003, scopeError);
                var handler = UserInputHandler;
                var active = BeginActiveToolCall(request, "input");
                CodexUserInputResponse response;
                try
                {
                    response = handler == null
                        ? new CodexUserInputResponse()
                        : await handler(prompt, active.Cancellation.Token).ConfigureAwait(false) ?? new CodexUserInputResponse();
                }
                finally { EndActiveToolCall(active); }
                var answers = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var answer in response.Answers ?? new Dictionary<string, IList<string>>())
                    answers[answer.Key] = new Dictionary<string, object>
                    {
                        { "answers", answer.Value == null ? new string[0] : answer.Value.ToArray() }
                    };
                return CodexRpcServerResponse.Ok(new Dictionary<string, object>
                {
                    { "answers", answers }
                });
            }

            return CodexRpcServerResponse.Error(-32601, "Method not supported by NEW_CLEVER: " + request.Method);
        }

        private ActiveToolCall BeginActiveToolCall(CodexRpcServerRequest request, string prefix)
        {
            var connection = _connectionCancellation;
            var cancellation = connection == null
                ? new CancellationTokenSource()
                : CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            var callId = FirstString(request.Params, "callId", "itemId") ?? request.Id.GetRawText();
            var key = prefix + ":" + callId;
            var active = new ActiveToolCall
            {
                Key = key,
                ThreadId = GetString(request.Params, "threadId"),
                TurnId = GetString(request.Params, "turnId"),
                Cancellation = cancellation
            };
            if (!_activeToolCalls.TryAdd(key, active))
            {
                cancellation.Dispose();
                throw new InvalidOperationException("A duplicate Codex tool call was received.");
            }
            return active;
        }

        private void EndActiveToolCall(ActiveToolCall active)
        {
            if (active == null) return;
            ActiveToolCall removed;
            if (_activeToolCalls.TryRemove(active.Key, out removed)) removed.Cancellation.Dispose();
        }

        private void CancelActiveToolCalls(string turnId)
        {
            foreach (var active in _activeToolCalls.Values.ToArray())
                if (string.IsNullOrWhiteSpace(turnId) || string.Equals(active.TurnId, turnId, StringComparison.Ordinal))
                    try { active.Cancellation.Cancel(); } catch { }
        }

        private bool IsActiveProjectScope(string threadId, out string error)
        {
            error = null;
            string expectedRoot;
            lock (_stateSync) _threadCwds.TryGetValue(threadId ?? "", out expectedRoot);
            var currentRoot = MainWindowVM.CurrentProjectRoot;
            if (!string.IsNullOrWhiteSpace(expectedRoot) && !string.IsNullOrWhiteSpace(currentRoot) &&
                PathsEqual(expectedRoot, currentRoot))
                return true;
            error = "The active NEW_CLEVER project no longer matches this Codex thread.";
            return false;
        }

        private static CodexUserInputRequest ParseUserInputRequest(JsonElement parameters)
        {
            var result = new CodexUserInputRequest
            {
                ThreadId = GetString(parameters, "threadId"),
                TurnId = GetString(parameters, "turnId"),
                ItemId = GetString(parameters, "itemId"),
                AutoResolutionMs = GetNullableInt64(parameters, "autoResolutionMs")
            };
            JsonElement questions;
            if (!parameters.TryGetProperty("questions", out questions) || questions.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var value in questions.EnumerateArray())
            {
                var question = new CodexUserInputQuestion
                {
                    Id = GetString(value, "id"),
                    Header = GetString(value, "header"),
                    Question = GetString(value, "question"),
                    IsSecret = GetBoolean(value, "isSecret")
                };
                JsonElement options;
                if (value.TryGetProperty("options", out options) && options.ValueKind == JsonValueKind.Array)
                    foreach (var option in options.EnumerateArray())
                        question.Options.Add(new CodexUserInputOption
                        {
                            Label = GetString(option, "label"),
                            Description = GetString(option, "description")
                        });
                if (!string.IsNullOrWhiteSpace(question.Id))
                    result.Questions.Add(question);
            }
            return result;
        }

        private void OnNotification(string method, JsonElement parameters, string rawJson)
        {
            var eventArgs = MapNotification(method, parameters, rawJson);
            if (eventArgs != null)
                RaiseEvent(eventArgs);

            if (eventArgs != null && eventArgs.Kind == CodexBackendEventKind.TokenUsageUpdated &&
                !string.IsNullOrWhiteSpace(eventArgs.ThreadId) && eventArgs.TokenUsage != null)
                lock (_stateSync) _threadTokenUsage[eventArgs.ThreadId] = eventArgs.TokenUsage;

            if (string.Equals(method, "turn/started", StringComparison.Ordinal))
            {
                lock (_stateSync)
                {
                    _activeThreadId = eventArgs == null ? GetString(parameters, "threadId") : eventArgs.ThreadId;
                    _activeTurnId = eventArgs == null ? GetString(parameters, "turnId") : eventArgs.TurnId;
                }
            }
            else if (string.Equals(method, "turn/completed", StringComparison.Ordinal))
            {
                CancelActiveToolCalls(eventArgs == null ? GetString(parameters, "turnId") : eventArgs.TurnId);
                lock (_stateSync)
                {
                    _activeThreadId = null;
                    _activeTurnId = null;
                }
            }

            if (string.Equals(method, "account/updated", StringComparison.Ordinal))
            {
                lock (_stateSync)
                {
                    var authMode = GetString(parameters, "authMode");
                    var planType = GetString(parameters, "planType");
                    _account = new CodexAccountSnapshot
                    {
                        RequiresOpenAiAuth = _account == null || _account.RequiresOpenAiAuth,
                        LoggedIn = !string.IsNullOrWhiteSpace(authMode),
                        AuthMode = authMode,
                        Email = _account == null ? null : _account.Email,
                        PlanType = string.IsNullOrWhiteSpace(planType) && _account != null ? _account.PlanType : planType
                    };
                }
                RaiseStateChanged();
            }
            else if (string.Equals(method, "account/rateLimits/updated", StringComparison.Ordinal))
            {
                var limits = ParseRateLimits(parameters);
                lock (_stateSync) _rateLimits = MergeRateLimits(_rateLimits, limits);
                RaiseStateChanged();
            }
            else if (string.Equals(method, "thread/goal/updated", StringComparison.Ordinal))
            {
                var threadId = GetString(parameters, "threadId");
                JsonElement goalElement;
                if (parameters.TryGetProperty("goal", out goalElement) && !string.IsNullOrWhiteSpace(threadId))
                    lock (_stateSync) _threadGoals[threadId] = ParseGoal(goalElement);
            }
            else if (string.Equals(method, "thread/goal/cleared", StringComparison.Ordinal))
            {
                var threadId = GetString(parameters, "threadId");
                if (!string.IsNullOrWhiteSpace(threadId)) lock (_stateSync) _threadGoals.Remove(threadId);
            }
            else if (string.Equals(method, "thread/deleted", StringComparison.Ordinal))
            {
                var threadId = GetString(parameters, "threadId");
                if (!string.IsNullOrWhiteSpace(threadId)) RemoveThreadState(threadId);
            }
            else if (string.Equals(method, "thread/settings/updated", StringComparison.Ordinal))
            {
                var threadId = GetString(parameters, "threadId");
                JsonElement settings;
                if (!string.IsNullOrWhiteSpace(threadId) && parameters.TryGetProperty("threadSettings", out settings))
                    lock (_stateSync) _threadSettings[threadId] = ParseEffectiveThreadSettings(settings);
            }
            else if (string.Equals(method, "thread/status/changed", StringComparison.Ordinal))
            {
                var threadId = GetString(parameters, "threadId");
                JsonElement status;
                if (!string.IsNullOrWhiteSpace(threadId) && parameters.TryGetProperty("status", out status))
                    lock (_stateSync) _threadStates[threadId] = ParseThreadStateValue(status);
            }
        }

        private CodexBackendEventArgs MapNotification(string method, JsonElement parameters, string rawJson)
        {
            var args = new CodexBackendEventArgs
            {
                ThreadId = GetString(parameters, "threadId"),
                TurnId = GetString(parameters, "turnId"),
                ItemId = GetString(parameters, "itemId"),
                RawJson = rawJson
            };
            JsonElement thread;
            JsonElement turn;
            JsonElement item;
            if (parameters.TryGetProperty("thread", out thread) && thread.ValueKind == JsonValueKind.Object)
            {
                args.Thread = ParseThread(thread);
                if (args.Thread != null) args.ThreadId = args.Thread.Id;
            }
            if (parameters.TryGetProperty("turn", out turn) && turn.ValueKind == JsonValueKind.Object)
            {
                args.TurnId = GetString(turn, "id") ?? args.TurnId;
                args.TurnDurationMs = GetNullableInt64(turn, "durationMs");
            }
            if (parameters.TryGetProperty("item", out item) && item.ValueKind == JsonValueKind.Object)
            {
                args.Item = ParseItem(item, args.TurnId);
                if (args.Item != null) args.ItemId = args.Item.Id;
            }

            switch (method)
            {
                case "thread/started": args.Kind = CodexBackendEventKind.ThreadStarted; break;
                case "thread/name/updated":
                case "thread/status/changed":
                case "thread/settings/updated": args.Kind = CodexBackendEventKind.ThreadUpdated; break;
                case "thread/deleted": args.Kind = CodexBackendEventKind.ThreadDeleted; break;
                case "turn/started": args.Kind = CodexBackendEventKind.TurnStarted; break;
                case "turn/completed": args.Kind = CodexBackendEventKind.TurnCompleted; break;
                case "item/started": args.Kind = CodexBackendEventKind.ItemStarted; break;
                case "item/completed": args.Kind = CodexBackendEventKind.ItemCompleted; break;
                case "turn/plan/updated":
                    args.Kind = CodexBackendEventKind.ItemCompleted;
                    args.ItemId = "plan:" + (args.TurnId ?? "current");
                    args.Item = ParsePlanItem(parameters, args.ItemId, args.TurnId);
                    break;
                case "thread/tokenUsage/updated":
                    args.Kind = CodexBackendEventKind.TokenUsageUpdated;
                    args.TokenUsage = CodexTokenUsageSnapshot.ParseNotification(parameters);
                    args.ContextUsedPercent = args.TokenUsage == null ? (int?)null : args.TokenUsage.UsedPercent;
                    break;
                case "account/updated":
                    args.Kind = CodexBackendEventKind.AccountUpdated;
                    args.Account = new CodexAccountSnapshot
                    {
                        RequiresOpenAiAuth = true,
                        LoggedIn = !string.IsNullOrWhiteSpace(GetString(parameters, "authMode")),
                        AuthMode = GetString(parameters, "authMode"),
                        PlanType = GetString(parameters, "planType")
                    };
                    break;
                case "account/rateLimits/updated":
                    args.Kind = CodexBackendEventKind.RateLimitsUpdated;
                    args.RateLimits = ParseRateLimits(parameters);
                    break;
                case "error":
                    args.Kind = CodexBackendEventKind.Error;
                    args.Message = FirstString(parameters, "message");
                    JsonElement error;
                    if (string.IsNullOrWhiteSpace(args.Message) && parameters.TryGetProperty("error", out error))
                        args.Message = error.ValueKind == JsonValueKind.Object
                            ? FirstString(error, "message", "detail")
                            : (error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText());
                    break;
                default:
                    if (method.EndsWith("/delta", StringComparison.Ordinal) ||
                        method.EndsWith("Delta", StringComparison.Ordinal))
                    {
                        args.Kind = CodexBackendEventKind.ItemDelta;
                        args.Delta = FirstString(parameters, "delta", "text", "content");
                        return args;
                    }
                    return null;
            }
            return args;
        }

        private void OnTransportFaulted(Exception exception)
        {
            if (_stopping || Volatile.Read(ref _disposed) != 0)
                return;
            SetConnectionState(CodexConnectionState.Error, exception.Message);
            ClearActiveTurnAndRaiseError("Codex app-server transport failed: " + exception.Message);
            ScheduleRecovery();
        }

        private void OnUnexpectedExit(int exitCode)
        {
            if (_stopping || Volatile.Read(ref _disposed) != 0)
                return;
            SetConnectionState(CodexConnectionState.Error, "Codex app-server exited with code " + exitCode + ".");
            ClearActiveTurnAndRaiseError("Codex app-server exited with code " + exitCode + ".");
            ScheduleRecovery();
        }

        private void ClearActiveTurnAndRaiseError(string message)
        {
            string threadId;
            string turnId;
            lock (_stateSync)
            {
                threadId = _activeThreadId;
                turnId = _activeTurnId;
                _activeThreadId = null;
                _activeTurnId = null;
            }
            if (string.IsNullOrWhiteSpace(threadId) && string.IsNullOrWhiteSpace(turnId))
                return;
            CancelActiveToolCalls(turnId);
            RaiseEvent(new CodexBackendEventArgs
            {
                Kind = CodexBackendEventKind.Error,
                ThreadId = threadId,
                TurnId = turnId,
                Message = message
            });
            RaiseEvent(new CodexBackendEventArgs
            {
                Kind = CodexBackendEventKind.TurnCompleted,
                ThreadId = threadId,
                TurnId = turnId,
                Message = message
            });
        }

        private bool IsChatGptAuthenticated()
        {
            lock (_stateSync)
                return _account != null && _account.LoggedIn &&
                       string.Equals(_account.AuthMode, "chatgpt", StringComparison.OrdinalIgnoreCase);
        }

        private void ScheduleRecovery()
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _recoveryScheduled, 1, 0) != 0)
                return;
            Task.Run(async () =>
            {
                try
                {
                    var cancellationToken = _lifetime.Token;
                    var delays = new[] { 250, 1000, 3000 };
                    for (var attempt = 0; attempt < delays.Length && Volatile.Read(ref _disposed) == 0; attempt++)
                    {
                        await Task.Delay(delays[attempt], cancellationToken).ConfigureAwait(false);
                        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            StopConnection();
                            lock (_stateSync) _liveDynamicToolThreads.Clear();
                            var resolved = await _runtimeManager.ResolveAsync(cancellationToken).ConfigureAwait(false);
                            if (!resolved.Success || resolved.Data == null || !resolved.Data.Compatible)
                                continue;
                            await StartConnectionAsync(resolved.Data, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            SetConnectionState(CodexConnectionState.Error, ex.Message);
                        }
                        finally
                        {
                            _lifecycleGate.Release();
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _recoveryScheduled, 0);
                }
            });
        }

        private void AddColdResumeBridgeConfig(IDictionary<string, object> parameters, string threadId, string projectRoot)
        {
            if (_bridgeProvider == null || !_bridgeProvider.IsAvailable)
                return;
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new InvalidOperationException("The stored Codex thread has no valid project root.");
            var descriptor = _bridgeProvider.GetDescriptor(threadId, projectRoot);
            if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.Command))
                return;
            parameters["config"] = new Dictionary<string, object>
            {
                { "mcp_servers", new Dictionary<string, object>
                    {
                        { "NEW_CLEVER", new Dictionary<string, object>
                            {
                                { "command", descriptor.Command },
                                { "args", descriptor.Arguments == null ? new string[0] : descriptor.Arguments.ToArray() },
                                { "env", descriptor.Environment == null
                                    ? new Dictionary<string, string>()
                                    : new Dictionary<string, string>(descriptor.Environment) }
                            }
                        }
                    }
                }
            };
        }

        private static IList<object> BuildTurnInput(CodexTurnRequest request)
        {
            var result = new List<object>();
            if (request.Inputs != null)
            {
                foreach (var input in request.Inputs.Where(item => item != null))
                {
                    if (string.Equals(input.Type, "localImage", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(new Dictionary<string, object> { { "type", "localImage" }, { "path", input.Path } });
                    }
                    else if (string.Equals(input.Type, "mention", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(new Dictionary<string, object>
                        {
                            { "type", "mention" }, { "name", input.Name ?? Path.GetFileName(input.Path) }, { "path", input.Path }
                        });
                    }
                    else
                    {
                        result.Add(new Dictionary<string, object> { { "type", "text" }, { "text", input.Text ?? "" } });
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(request.Text) && !ContainsText(result, request.Text))
                result.Insert(0, new Dictionary<string, object> { { "type", "text" }, { "text", request.Text } });
            if (result.Count == 0)
                result.Add(new Dictionary<string, object> { { "type", "text" }, { "text", "" } });
            return result;
        }

        private static void ValidateTurnInputPaths(CodexTurnRequest request, string projectRoot)
        {
            if (request.Inputs == null)
                return;
            foreach (var input in request.Inputs.Where(item => item != null))
            {
                if (!string.Equals(input.Type, "localImage", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(input.Type, "mention", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(input.Path) || !IsPathInsideRoot(input.Path, projectRoot))
                    throw new InvalidOperationException("Codex attachments must be inside the active project: " + (input.Path ?? ""));
            }
        }

        private static bool IsPathInsideRoot(string path, string projectRoot)
        {
            try
            {
                var candidate = NormalizeAbsolutePath(path);
                if (string.Equals(candidate, projectRoot, StringComparison.OrdinalIgnoreCase))
                    return true;
                return candidate.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                       candidate.StartsWith(projectRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool ContainsText(IEnumerable<object> inputs, string text)
        {
            foreach (var input in inputs)
            {
                var dictionary = input as IDictionary<string, object>;
                object value;
                if (dictionary != null && dictionary.TryGetValue("text", out value) && string.Equals(value as string, text, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private CodexStatusSnapshot BuildStatus(string threadId)
        {
            CodexThreadSettings settings = null;
            CodexGoal goal = null;
            CodexTokenUsageSnapshot tokenUsage = null;
            CodexAccountSnapshot account;
            CodexRateLimitSnapshot limits;
            CodexThreadState threadState = CodexThreadState.NotLoaded;
            lock (_stateSync)
            {
                if (!string.IsNullOrWhiteSpace(threadId))
                {
                    _threadSettings.TryGetValue(threadId, out settings);
                    _threadGoals.TryGetValue(threadId, out goal);
                    _threadTokenUsage.TryGetValue(threadId, out tokenUsage);
                    if (string.Equals(_activeThreadId, threadId, StringComparison.Ordinal))
                        threadState = CodexThreadState.Active;
                    else if (!_threadStates.TryGetValue(threadId, out threadState))
                        threadState = CodexThreadState.Idle;
                }
                account = _account;
                limits = _rateLimits;
            }
            return new CodexStatusSnapshot
            {
                ConnectionState = _connectionState,
                Runtime = _runtime,
                Version = _runtime == null ? null : _runtime.Version,
                Account = account == null ? null : account.Email,
                PlanType = account == null ? null : account.PlanType,
                Auth = account,
                RateLimits = limits,
                TokenUsage = tokenUsage,
                Capabilities = Capabilities,
                Settings = settings ?? new CodexThreadSettings(),
                Goal = goal,
                ContextUsedPercent = tokenUsage == null ? (int?)null : tokenUsage.UsedPercent,
                ThreadState = string.IsNullOrWhiteSpace(threadId) ? CodexThreadState.NotLoaded : threadState,
                PrimaryRateLimitUsedPercent = limits == null || limits.Primary == null ? (int?)null : limits.Primary.UsedPercent,
                SecondaryRateLimitUsedPercent = limits == null || limits.Secondary == null ? (int?)null : limits.Secondary.UsedPercent,
                Message = _lastMessage
            };
        }

        private void SetConnectionState(CodexConnectionState state, string message)
        {
            _connectionState = state;
            _lastMessage = message ?? "";
            RaiseStateChanged();
            RaiseEvent(new CodexBackendEventArgs
            {
                Kind = CodexBackendEventKind.ConnectionChanged,
                Message = _lastMessage
            });
        }

        private void StopConnection()
        {
            _stopping = true;
            try
            {
                var connectionCancellation = Interlocked.Exchange(ref _connectionCancellation, null);
                if (connectionCancellation != null)
                {
                    try { connectionCancellation.Cancel(); } catch { }
                }
                CancelActiveToolCalls(null);
                var transport = _transport;
                _transport = null;
                if (transport != null)
                {
                    transport.NotificationReceived -= OnNotification;
                    transport.ServerRequestReceived -= OnServerRequestAsync;
                    transport.Faulted -= OnTransportFaulted;
                }
                var host = _host;
                _host = null;
                if (host != null)
                {
                    host.UnexpectedExit -= OnUnexpectedExit;
                    host.Dispose();
                }
                if (connectionCancellation != null) connectionCancellation.Dispose();
            }
            finally
            {
                _stopping = false;
            }
        }

        private void DisableCapability(string method)
        {
            switch (method)
            {
                case "thread/delete": Capabilities.ThreadDelete = false; break;
                case "thread/turns/list": Capabilities.PagedTurns = false; break;
                case "thread/settings/update": Capabilities.ThreadSettings = false; break;
                case "thread/goal/get":
                case "thread/goal/set":
                case "thread/goal/clear": Capabilities.Goals = false; break;
                case "thread/compact/start": Capabilities.Compact = false; break;
                case "model/list": Capabilities.Models = false; break;
                case "account/read": Capabilities.Authentication = false; break;
            }
            RaiseStateChanged();
        }

        private static CodexCapabilities CreateFullCapabilities()
        {
            return new CodexCapabilities
            {
                Threads = true, Turns = true, Streaming = true, Authentication = true,
                Models = true, Reasoning = true, Speed = true, Goals = true,
                Compact = true, Status = true, DynamicTools = true, ThreadSettings = true,
                ThreadDelete = true, PagedTurns = true
            };
        }

        private static CodexThreadSummary ParseThread(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;
            return new CodexThreadSummary
            {
                Id = GetString(element, "id"),
                Name = FirstString(element, "name", "title"),
                Preview = GetString(element, "preview") ?? "",
                Cwd = GetString(element, "cwd"),
                Source = ParseStringLike(element, "source"),
                CreatedAt = ParseTimestamp(element, "createdAt"),
                UpdatedAt = ParseTimestamp(element, "updatedAt"),
                State = ParseThreadState(element)
            };
        }

        private static CodexThreadDetails ParseThreadDetails(JsonElement response, string threadId)
        {
            var threadElement = GetObject(response, "thread");
            var details = new CodexThreadDetails { Thread = ParseThread(threadElement) };
            if (details.Thread == null) details.Thread = new CodexThreadSummary { Id = threadId };
            JsonElement turns;
            if (threadElement.ValueKind == JsonValueKind.Object &&
                threadElement.TryGetProperty("turns", out turns) && turns.ValueKind == JsonValueKind.Array)
                ParseTurns(turns, details);
            return details;
        }

        private static void ParseTurns(JsonElement turns, CodexThreadDetails details)
        {
            // The server pages turns newest-first. Render each page oldest-first so that
            // messages, reasoning and tool cards preserve their original timeline order.
            foreach (var turn in turns.EnumerateArray().Reverse())
            {
                var turnId = GetString(turn, "id");
                var turnDurationMs = GetNullableInt64(turn, "durationMs");
                JsonElement items;
                if (!turn.TryGetProperty("items", out items) || items.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var itemElement in items.EnumerateArray())
                {
                    var item = ParseItem(itemElement, turnId);
                    if (item == null) continue;
                    item.TurnDurationMs = turnDurationMs;
                    details.Items.Add(item);
                    if (item.Kind == CodexItemKind.UserMessage || item.Kind == CodexItemKind.AgentMessage)
                    {
                        details.Messages.Add(new CodexMessage
                        {
                            Id = item.Id,
                            IsUser = item.Kind == CodexItemKind.UserMessage,
                            Markdown = item.Text ?? "",
                            CreatedAt = DateTimeOffset.MinValue
                        });
                    }
                }
            }
        }

        private static CodexThreadItem ParseItem(JsonElement element, string turnId)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;
            var type = GetString(element, "type") ?? "";
            var kind = CodexItemKind.Unknown;
            switch (type)
            {
                case "userMessage": kind = CodexItemKind.UserMessage; break;
                case "agentMessage": kind = CodexItemKind.AgentMessage; break;
                case "reasoning": kind = CodexItemKind.Reasoning; break;
                case "plan": kind = CodexItemKind.Plan; break;
                case "commandExecution": kind = CodexItemKind.CommandExecution; break;
                case "fileChange": kind = CodexItemKind.FileChange; break;
                case "mcpToolCall": kind = CodexItemKind.McpToolCall; break;
                case "dynamicToolCall": kind = CodexItemKind.DynamicToolCall; break;
                case "contextCompaction": kind = CodexItemKind.ContextCompaction; break;
            }
            var extractedText = ExtractText(element);
            if (kind != CodexItemKind.UserMessage && kind != CodexItemKind.AgentMessage && extractedText.Length > 4000)
                extractedText = extractedText.Substring(0, 4000) + "…";
            var rawJson = element.GetRawText();
            return new CodexThreadItem
            {
                Id = GetString(element, "id"),
                TurnId = turnId,
                Kind = kind,
                Status = ParseStringLike(element, "status"),
                Title = FirstString(element, "title", "tool", "command"),
                Text = extractedText,
                Details = extractedText,
                RawJson = CompactItemJson(element, rawJson),
                Attachments = kind == CodexItemKind.UserMessage
                    ? ParseUserAttachments(element)
                    : new List<CodexAttachment>()
            };
        }

        private static IList<CodexAttachment> ParseUserAttachments(JsonElement element)
        {
            var attachments = new List<CodexAttachment>();
            JsonElement content;
            if (!element.TryGetProperty("content", out content) || content.ValueKind != JsonValueKind.Array)
                return attachments;

            foreach (var input in content.EnumerateArray())
            {
                var type = GetString(input, "type");
                var path = GetString(input, "path");
                if ((type != "localImage" && type != "mention") || string.IsNullOrWhiteSpace(path))
                    continue;
                attachments.Add(new CodexAttachment
                {
                    Path = path,
                    Kind = type == "localImage" ? "IMG" : "FILE",
                    Name = GetString(input, "name") ?? Path.GetFileName(path)
                });
            }
            return attachments;
        }

        private static string CompactItemJson(JsonElement element, string rawJson)
        {
            if (rawJson == null || rawJson.Length <= 65536)
                return rawJson ?? "{}";
            var compact = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[] { "type", "id", "status", "server", "tool", "command", "cwd", "exitCode", "durationMs" })
            {
                JsonElement value;
                if (element.TryGetProperty(name, out value))
                    compact[name] = LimitJsonValue(value, 4000);
            }
            JsonElement arguments;
            if (element.TryGetProperty("arguments", out arguments)) compact["arguments"] = LimitJsonValue(arguments, 8000);
            var result = default(JsonElement);
            if (element.TryGetProperty("result", out result))
            {
                var message = result.ValueKind == JsonValueKind.Object ? FirstString(result, "message", "code", "error") : null;
                compact["resultMessage"] = !string.IsNullOrWhiteSpace(message) ? message : LimitJsonValue(result, 2000);
            }
            JsonElement contentItems;
            if (element.TryGetProperty("contentItems", out contentItems))
                AddCompactContentItemSummary(contentItems, compact);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("contentItems", out contentItems))
                AddCompactContentItemSummary(contentItems, compact);
            return JsonSerializer.Serialize(compact);
        }

        private static void AddCompactContentItemSummary(JsonElement contentItems, IDictionary<string, string> compact)
        {
            if (contentItems.ValueKind != JsonValueKind.Array)
                return;
            foreach (var contentItem in contentItems.EnumerateArray())
            {
                var text = GetString(contentItem, "text");
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                try
                {
                    using (var envelope = JsonDocument.Parse(text))
                    {
                        var message = FirstString(envelope.RootElement, "message", "code", "error");
                        if (!string.IsNullOrWhiteSpace(message))
                            compact["resultMessage"] = LimitText(message, 1000);

                        var dataJson = GetString(envelope.RootElement, "dataJson");
                        if (string.IsNullOrWhiteSpace(dataJson))
                            continue;
                        using (var data = JsonDocument.Parse(dataJson))
                        {
                            var title = data.RootElement.ValueKind == JsonValueKind.Object
                                ? GetString(data.RootElement, "title")
                                : null;
                            if (string.IsNullOrWhiteSpace(title) && data.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var entry in data.RootElement.EnumerateArray())
                                {
                                    title = GetString(entry, "title");
                                    if (!string.IsNullOrWhiteSpace(title))
                                        break;
                                }
                            }
                            if (!string.IsNullOrWhiteSpace(title))
                                compact["resultDataTitle"] = LimitText(title, 1000);
                        }
                    }
                }
                catch { }
                return;
            }
        }

        private static string LimitJsonValue(JsonElement value, int max)
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            text = text ?? "";
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private static string LimitText(string text, int max)
        {
            text = text ?? "";
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private static string ExtractText(JsonElement element)
        {
            var direct = FirstString(element, "text", "message", "delta", "output");
            if (!string.IsNullOrEmpty(direct)) return direct;
            var texts = new List<string>();
            CollectText(element, texts, 0);
            return string.Join("\n", texts.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct());
        }

        private static void CollectText(JsonElement element, IList<string> values, int depth)
        {
            if (depth > 5) return;
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if ((property.NameEquals("text") || property.NameEquals("content")) && property.Value.ValueKind == JsonValueKind.String)
                        values.Add(property.Value.GetString());
                    else if (property.Value.ValueKind == JsonValueKind.Array || property.Value.ValueKind == JsonValueKind.Object)
                        CollectText(property.Value, values, depth + 1);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray()) CollectText(child, values, depth + 1);
            }
        }

        private static CodexModelInfo ParseModel(JsonElement element)
        {
            var model = new CodexModelInfo
            {
                Id = FirstString(element, "id", "model"),
                DisplayName = FirstString(element, "displayName", "name", "id"),
                Description = GetString(element, "description") ?? "",
                DefaultReasoningEffort = FirstString(element, "defaultReasoningEffort", "defaultReasoningLevel"),
                DefaultServiceTier = GetString(element, "defaultServiceTier"),
                IsDefault = GetBoolean(element, "isDefault")
            };
            JsonElement efforts;
            if (element.TryGetProperty("supportedReasoningEfforts", out efforts) && efforts.ValueKind == JsonValueKind.Array)
            {
                foreach (var effort in efforts.EnumerateArray())
                {
                    var value = effort.ValueKind == JsonValueKind.String ? effort.GetString() :
                        FirstString(effort, "reasoningEffort", "effort", "id");
                    if (!string.IsNullOrWhiteSpace(value)) model.SupportedReasoningEfforts.Add(value);
                }
            }
            JsonElement modalities;
            if (element.TryGetProperty("inputModalities", out modalities) && modalities.ValueKind == JsonValueKind.Array)
            {
                model.SupportsImageInput = modalities.EnumerateArray().Any(item =>
                    item.ValueKind == JsonValueKind.String &&
                    string.Equals(item.GetString(), "image", StringComparison.OrdinalIgnoreCase));
            }
            ParseServiceTiers(element, "serviceTiers", model);
            ParseServiceTiers(element, "additionalSpeedTiers", model);
            return model;
        }

        private static void ParseServiceTiers(JsonElement element, string propertyName, CodexModelInfo model)
        {
            JsonElement tiers;
            if (!element.TryGetProperty(propertyName, out tiers) || tiers.ValueKind != JsonValueKind.Array)
                return;
            foreach (var tier in tiers.EnumerateArray())
            {
                var id = tier.ValueKind == JsonValueKind.String ? tier.GetString() : FirstString(tier, "id", "serviceTier");
                if (string.IsNullOrWhiteSpace(id) ||
                    model.ServiceTiers.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                model.ServiceTiers.Add(new CodexServiceTierInfo
                {
                    Id = id,
                    Name = tier.ValueKind == JsonValueKind.String ? id : FirstString(tier, "displayName", "name", "id"),
                    Description = tier.ValueKind == JsonValueKind.String ? "" : GetString(tier, "description") ?? "",
                    IsDefault = string.Equals(id, model.DefaultServiceTier, StringComparison.Ordinal)
                });
            }
        }

        private static CodexAccountSnapshot ParseAccount(JsonElement response)
        {
            var snapshot = new CodexAccountSnapshot
            {
                RequiresOpenAiAuth = GetBoolean(response, "requiresOpenaiAuth")
            };
            JsonElement account;
            if (!response.TryGetProperty("account", out account) || account.ValueKind != JsonValueKind.Object)
                return snapshot;
            snapshot.LoggedIn = true;
            snapshot.AuthMode = FirstString(account, "type", "authMode");
            snapshot.Email = GetString(account, "email");
            snapshot.PlanType = GetString(account, "planType");
            return snapshot;
        }

        private static CodexRateLimitSnapshot ParseRateLimits(JsonElement response)
        {
            JsonElement limits;
            if (response.TryGetProperty("rateLimits", out limits) && limits.ValueKind == JsonValueKind.Object)
                response = limits;
            JsonElement primary;
            JsonElement secondary;
            var result = new CodexRateLimitSnapshot
            {
                Primary = response.TryGetProperty("primary", out primary) ? ParseRateWindow(primary) : null,
                Secondary = response.TryGetProperty("secondary", out secondary) ? ParseRateWindow(secondary) : null
            };
            JsonElement credits;
            if (response.TryGetProperty("credits", out credits) && credits.ValueKind == JsonValueKind.Object)
            {
                result.HasCredits = GetBoolean(credits, "hasCredits");
                result.UnlimitedCredits = GetBoolean(credits, "unlimited");
                result.CreditBalance = ParseStringLike(credits, "balance");
            }
            return result;
        }

        private static CodexRateLimitSnapshot MergeRateLimits(CodexRateLimitSnapshot current, CodexRateLimitSnapshot update)
        {
            if (current == null) return update;
            if (update == null) return current;
            return new CodexRateLimitSnapshot
            {
                Primary = update.Primary ?? current.Primary,
                Secondary = update.Secondary ?? current.Secondary,
                HasCredits = update.HasCredits || current.HasCredits,
                UnlimitedCredits = update.UnlimitedCredits || current.UnlimitedCredits,
                CreditBalance = string.IsNullOrWhiteSpace(update.CreditBalance) ? current.CreditBalance : update.CreditBalance
            };
        }

        private static CodexRateLimitWindow ParseRateWindow(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            return new CodexRateLimitWindow
            {
                UsedPercent = GetInt32(element, "usedPercent", 0),
                ResetsAtUnixSeconds = GetNullableInt64(element, "resetsAt"),
                WindowDurationMinutes = GetNullableInt64(element, "windowDurationMins") ?? GetNullableInt64(element, "windowDurationMinutes")
            };
        }

        private static CodexGoal ParseGoal(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            return new CodexGoal
            {
                Objective = GetString(element, "objective"),
                Status = ParseStringLike(element, "status"),
                TokenBudget = GetNullableInt64(element, "tokenBudget"),
                TokensUsed = GetNullableInt64(element, "tokensUsed") ?? 0,
                TimeUsedSeconds = GetNullableInt64(element, "timeUsedSeconds") ?? 0
            };
        }

        private static CodexThreadState ParseThreadState(JsonElement thread)
        {
            JsonElement status;
            return thread.ValueKind == JsonValueKind.Object && thread.TryGetProperty("status", out status)
                ? ParseThreadStateValue(status)
                : CodexThreadState.NotLoaded;
        }

        private static CodexThreadState ParseThreadStateValue(JsonElement status)
        {
            var state = status.ValueKind == JsonValueKind.Object
                ? FirstString(status, "type", "status", "value")
                : (status.ValueKind == JsonValueKind.String ? status.GetString() : null);
            state = state ?? "notLoaded";
            if (state.IndexOf("active", StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf("running", StringComparison.OrdinalIgnoreCase) >= 0)
                return CodexThreadState.Active;
            if (state.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                return CodexThreadState.Error;
            if (state.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0)
                return CodexThreadState.Idle;
            return CodexThreadState.NotLoaded;
        }

        private static CodexThreadSettings ParseEffectiveThreadSettings(JsonElement element)
        {
            JsonElement nested;
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("threadSettings", out nested))
                element = nested;
            return new CodexThreadSettings
            {
                Model = GetString(element, "model"),
                ReasoningEffort = FirstString(element, "effort", "reasoningEffort"),
                ServiceTier = GetString(element, "serviceTier")
            };
        }

        private static CodexThreadItem ParsePlanItem(JsonElement parameters, string itemId, string turnId)
        {
            var lines = new List<string>();
            JsonElement plan;
            if (parameters.TryGetProperty("plan", out plan) && plan.ValueKind == JsonValueKind.Array)
                foreach (var step in plan.EnumerateArray())
                {
                    var status = GetString(step, "status") ?? "unknown";
                    var text = GetString(step, "step") ?? "";
                    lines.Add("[" + status + "] " + text);
                }
            return new CodexThreadItem
            {
                Id = itemId,
                TurnId = turnId,
                Kind = CodexItemKind.Plan,
                Title = FirstString(parameters, "explanation") ?? "Plan",
                Text = string.Join(Environment.NewLine, lines),
                Details = string.Join(Environment.NewLine, lines),
                Status = "updated"
            };
        }

        private void RemoveThreadState(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return;
            lock (_stateSync)
            {
                _threadSettings.Remove(threadId);
                _threadGoals.Remove(threadId);
                _threadTokenUsage.Remove(threadId);
                _threadCwds.Remove(threadId);
                _threadStates.Remove(threadId);
                _liveDynamicToolThreads.Remove(threadId);
            }
        }

        private static void AddThreadSettings(IDictionary<string, object> parameters, CodexThreadSettings settings)
        {
            if (settings == null) return;
            AddIfNotBlank(parameters, "model", settings.Model);
            AddIfNotBlank(parameters, "serviceTier", settings.ServiceTier);
            if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort))
                parameters["config"] = new Dictionary<string, object> { { "model_reasoning_effort", settings.ReasoningEffort } };
        }

        private static void AddTurnSettings(IDictionary<string, object> parameters, CodexThreadSettings settings)
        {
            if (settings == null) return;
            AddIfNotBlank(parameters, "model", settings.Model);
            AddIfNotBlank(parameters, "effort", settings.ReasoningEffort);
            if (settings.ServiceTier != null) parameters["serviceTier"] = settings.ServiceTier;
        }

        private static CodexThreadSettings CloneSettings(CodexThreadSettings settings)
        {
            return settings == null ? new CodexThreadSettings() : new CodexThreadSettings
            {
                Model = settings.Model,
                ReasoningEffort = settings.ReasoningEffort,
                ServiceTier = settings.ServiceTier
            };
        }

        private static JsonElement ParseJsonValue(string json)
        {
            using (var document = JsonDocument.Parse(json)) return document.RootElement.Clone();
        }

        private static JsonElement GetObject(JsonElement element, string name)
        {
            JsonElement value;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)
                ? value.Clone()
                : ParseJsonValue("{}");
        }

        private static string GetString(JsonElement element, string name)
        {
            JsonElement value;
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out value) || value.ValueKind == JsonValueKind.Null)
                return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static string FirstString(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                var value = GetString(element, name);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        private static string ParseStringLike(JsonElement element, string name)
        {
            JsonElement value;
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out value) || value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Object)
                return FirstString(value, "type", "status", "value") ?? value.GetRawText();
            return value.GetRawText();
        }

        private static bool GetBoolean(JsonElement element, string name)
        {
            JsonElement value;
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out value)) return false;
            return value.ValueKind == JsonValueKind.True ||
                   (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result) && result);
        }

        private static int GetInt32(JsonElement element, string name, int fallback)
        {
            JsonElement value;
            int result;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.TryGetInt32(out result)
                ? result : fallback;
        }

        private static long? GetNullableInt64(JsonElement element, string name)
        {
            JsonElement value;
            long result;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.TryGetInt64(out result)
                ? result : (long?)null;
        }

        private static DateTimeOffset ParseTimestamp(JsonElement element, string name)
        {
            var unix = GetNullableInt64(element, name);
            if (unix.HasValue)
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(unix.Value); } catch { }
            }
            var text = GetString(element, name);
            DateTimeOffset parsed;
            return DateTimeOffset.TryParse(text, out parsed) ? parsed : DateTimeOffset.MinValue;
        }

        private static void AddIfNotBlank(IDictionary<string, object> dictionary, string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) dictionary[name] = value;
        }

        private static string NormalizeAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("An absolute project path is required.", "path");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try { return string.Equals(NormalizeAbsolutePath(left), NormalizeAbsolutePath(right), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static string GetClientVersion()
        {
            try { return typeof(CodexAppServerBackend).Assembly.GetName().Version.ToString(); }
            catch { return "1.0.0"; }
        }

        private BackendResult<T> Failure<T>(Exception exception)
        {
            var rpc = exception as CodexRpcException;
            if (rpc != null)
                return BackendResult<T>.Fail(
                    rpc.Code == -32601 ? "rpc_method_unsupported" : "rpc_" + rpc.Code,
                    rpc.Message,
                    rpc.Code == -32601);
            if (exception is TimeoutException)
                return BackendResult<T>.Fail("rpc_timeout", exception.Message);
            if (exception is OperationCanceledException)
                return BackendResult<T>.Fail("cancelled", "The Codex operation was cancelled.");
            return BackendResult<T>.Fail("codex_backend_error", exception.Message);
        }

        private void RaiseStateChanged()
        {
            var handler = StateChanged;
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); } catch { }
            }
        }

        private void RaiseEvent(CodexBackendEventArgs args)
        {
            var handler = EventReceived;
            if (handler != null)
            {
                try { handler(this, args); } catch { }
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException("CodexAppServerBackend");
        }

        private sealed class ActiveToolCall
        {
            public string Key { get; set; }
            public string ThreadId { get; set; }
            public string TurnId { get; set; }
            public CancellationTokenSource Cancellation { get; set; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _stopping = true;
            _lifetime.Cancel();
            StopConnection();
            Capabilities = CodexCapabilities.None();
            _connectionState = CodexConnectionState.Disconnected;
        }
    }
}
