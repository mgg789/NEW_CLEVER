using NEW_CLEVER.AgentCore;
using NEW_CLEVER.ViewModel;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NEW_CLEVER.Model.Agent
{
    /// <summary>
    /// Hosts the IDE side of the cold-resume MCP bridge. The child process can connect only
    /// as the current Windows user and must authenticate with an unguessable per-host token.
    /// </summary>
    public sealed class CodexMcpBridgeHost : ICodexMcpBridgeProvider, IDisposable
    {
        private const int MaximumLineLength = 8 * 1024 * 1024;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private readonly IAgentToolRegistry _registry;
        private readonly string _bridgeExecutable;
        private readonly string _pipeName;
        private readonly string _token;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCalls =
            new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _connectedServers =
            new ConcurrentDictionary<NamedPipeServerStream, byte>();
        private readonly ConcurrentDictionary<string, string> _threadRoots =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly object _serverLock = new object();
        private NamedPipeServerStream _activeServer;
        private Task _serverTask;
        private string _lastError;
        private bool _disposed;

        public CodexMcpBridgeHost(IAgentToolRegistry registry, string bridgeExecutable = null)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            _registry = registry;
            _bridgeExecutable = Path.GetFullPath(string.IsNullOrWhiteSpace(bridgeExecutable)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NEW_CLEVER.AgentMcpBridge.exe")
                : bridgeExecutable);
            _pipeName = "NEW_CLEVER-codex-" + Guid.NewGuid().ToString("N");
            _token = CreateToken();
        }

        public bool IsAvailable
        {
            get { return !_disposed && File.Exists(_bridgeExecutable); }
        }

        public string LastError
        {
            get { lock (_serverLock) return _lastError; }
        }

        public CodexMcpBridgeDescriptor GetDescriptor()
        {
            return GetDescriptor(null, null);
        }

        public CodexMcpBridgeDescriptor GetDescriptor(string threadId, string projectRoot)
        {
            if (_disposed) throw new ObjectDisposedException(GetType().FullName);
            if (!File.Exists(_bridgeExecutable))
                throw new FileNotFoundException("The NEW_CLEVER Codex MCP bridge is not installed.", _bridgeExecutable);

            EnsureStarted();
            var descriptor = new CodexMcpBridgeDescriptor { Command = _bridgeExecutable };
            descriptor.Arguments.Add("--pipe");
            descriptor.Arguments.Add(_pipeName);
            descriptor.Environment["NEW_CLEVER_MCP_PIPE_TOKEN"] = _token;
            if (!string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(projectRoot))
            {
                _threadRoots[threadId] = Path.GetFullPath(projectRoot);
                descriptor.Environment["NEW_CLEVER_MCP_THREAD_ID"] = threadId;
            }
            return descriptor;
        }

        private void EnsureStarted()
        {
            lock (_serverLock)
            {
                if (_serverTask == null)
                    _serverTask = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
            }
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    var security = CreatePipeSecurity();
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                        65536,
                        65536,
                        security,
                        HandleInheritability.None,
                        (PipeAccessRights)0);
                    lock (_serverLock) _activeServer = server;
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    lock (_serverLock)
                    {
                        if (ReferenceEquals(_activeServer, server)) _activeServer = null;
                    }
                    _connectedServers.TryAdd(server, 0);
                    var connected = server;
                    var connectionId = Guid.NewGuid().ToString("N");
                    server = null;
                    _ = Task.Run(() => RunConnectedClientAsync(connected, connectionId, cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lock (_serverLock) _lastError = ex.Message;
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (_serverLock)
                    {
                        if (ReferenceEquals(_activeServer, server)) _activeServer = null;
                    }
                    if (server != null) server.Dispose();
                }
            }
        }

        private async Task RunConnectedClientAsync(NamedPipeServerStream server, string connectionId, CancellationToken cancellationToken)
        {
            try
            {
                await HandleClientAsync(server, connectionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_serverLock) _lastError = ex.Message;
            }
            finally
            {
                CancelCalls(connectionId + ":");
                byte ignored;
                _connectedServers.TryRemove(server, out ignored);
                server.Dispose();
            }
        }

        private async Task HandleClientAsync(Stream stream, string connectionId, CancellationToken cancellationToken)
        {
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 65536, true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, true) { AutoFlush = true })
            using (var writeGate = new SemaphoreSlim(1, 1))
            {
                var authenticated = false;
                string scopedThreadId = null;
                while (!cancellationToken.IsCancellationRequested && stream.CanRead)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length > MaximumLineLength)
                        throw new InvalidDataException("MCP bridge request exceeded the eight-megabyte limit.");

                    JsonDocument document;
                    try { document = JsonDocument.Parse(line); }
                    catch (JsonException) { throw new InvalidDataException("MCP bridge received malformed JSON."); }
                    using (document)
                    {
                        var root = document.RootElement;
                        var method = GetString(root, "method");
                        var id = GetId(root);
                        JsonElement parameters;
                        root.TryGetProperty("params", out parameters);

                        if (!authenticated)
                        {
                            var supplied = parameters.ValueKind == JsonValueKind.Object ? GetString(parameters, "token") : null;
                            var requestedThreadId = parameters.ValueKind == JsonValueKind.Object ? GetString(parameters, "threadId") : null;
                            if (!string.Equals(method, "auth", StringComparison.Ordinal) || !FixedTimeEquals(_token, supplied))
                            {
                                await WriteErrorAsync(writer, writeGate, id, -32001, "MCP bridge authentication failed.", cancellationToken).ConfigureAwait(false);
                                return;
                            }
                            if (!string.IsNullOrWhiteSpace(requestedThreadId) && !_threadRoots.ContainsKey(requestedThreadId))
                            {
                                await WriteErrorAsync(writer, writeGate, id, -32002, "The MCP bridge thread scope is unknown.", cancellationToken).ConfigureAwait(false);
                                return;
                            }
                            scopedThreadId = requestedThreadId;
                            authenticated = true;
                            await WriteResultAsync(writer, writeGate, id, new { authenticated = true }, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (string.Equals(method, "cancel", StringComparison.Ordinal))
                        {
                            long callId;
                            if (parameters.ValueKind == JsonValueKind.Object && TryGetInt64(parameters, "id", out callId))
                            {
                                CancellationTokenSource active;
                                if (_activeCalls.TryGetValue(connectionId + ":" + callId, out active)) active.Cancel();
                            }
                            if (id.HasValue)
                                await WriteResultAsync(writer, writeGate, id, new { cancelled = true }, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (string.Equals(method, "ping", StringComparison.Ordinal))
                        {
                            await WriteResultAsync(writer, writeGate, id, new { }, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (string.Equals(method, "tools/list", StringComparison.Ordinal))
                        {
                            string scopeError;
                            if (!IsProjectScopeCurrent(scopedThreadId, out scopeError))
                            {
                                await WriteErrorAsync(writer, writeGate, id, -32003, scopeError, cancellationToken).ConfigureAwait(false);
                                continue;
                            }
                            var tools = _registry.Definitions.Select(definition => new
                            {
                                name = definition.Name,
                                description = definition.Description ?? "",
                                inputSchema = ParseSchema(definition.InputSchemaJson)
                            }).ToArray();
                            await WriteResultAsync(writer, writeGate, id, new { tools = tools }, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (string.Equals(method, "tools/call", StringComparison.Ordinal))
                        {
                            string scopeError;
                            if (!IsProjectScopeCurrent(scopedThreadId, out scopeError))
                            {
                                await WriteErrorAsync(writer, writeGate, id, -32003, scopeError, cancellationToken).ConfigureAwait(false);
                                continue;
                            }
                            long callId;
                            if (!id.HasValue || !id.Value.TryGetInt64(out callId))
                            {
                                await WriteErrorAsync(writer, writeGate, id, -32600, "A numeric tool call id is required.", cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            var name = parameters.ValueKind == JsonValueKind.Object ? GetString(parameters, "name") : null;
                            if (!string.IsNullOrWhiteSpace(name) && name.IndexOf('.') < 0)
                                name = "NEW_CLEVER." + name;
                            JsonElement arguments;
                            var argumentsJson = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out arguments)
                                ? arguments.GetRawText()
                                : "{}";
                            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            var callKey = connectionId + ":" + callId;
                            if (!_activeCalls.TryAdd(callKey, linked))
                            {
                                linked.Dispose();
                                await WriteErrorAsync(writer, writeGate, id, -32600, "Duplicate tool call id.", cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            var capturedId = id.Value.Clone();
                            _ = Task.Run(async delegate
                            {
                                try
                                {
                                    var result = await _registry.ExecuteAsync(name, argumentsJson, linked.Token).ConfigureAwait(false);
                                    await WriteResultAsync(writer, writeGate, capturedId, result, cancellationToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    await WriteErrorAsync(writer, writeGate, capturedId, -32800, "Tool call cancelled.", cancellationToken).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    await WriteErrorAsync(writer, writeGate, capturedId, -32603, ex.Message, cancellationToken).ConfigureAwait(false);
                                }
                                finally
                                {
                                    CancellationTokenSource removed;
                                    if (_activeCalls.TryRemove(callKey, out removed)) removed.Dispose();
                                }
                            });
                            continue;
                        }

                        await WriteErrorAsync(writer, writeGate, id, -32601, "Unsupported bridge method: " + (method ?? ""), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private static async Task WriteResultAsync(StreamWriter writer, SemaphoreSlim gate, JsonElement? id, object result, CancellationToken cancellationToken)
        {
            if (!id.HasValue) return;
            var payload = JsonSerializer.Serialize(new { id = id.Value, result = result }, JsonOptions);
            await WriteLineAsync(writer, gate, payload, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteErrorAsync(StreamWriter writer, SemaphoreSlim gate, JsonElement? id, int code, string message, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new { id = id, error = new { code = code, message = message ?? "" } }, JsonOptions);
            await WriteLineAsync(writer, gate, payload, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteLineAsync(StreamWriter writer, SemaphoreSlim gate, string payload, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(payload).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        private static JsonElement ParseSchema(string schema)
        {
            using (var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(schema) ? "{}" : schema))
                return document.RootElement.Clone();
        }

        private static JsonElement? GetId(JsonElement root)
        {
            JsonElement id;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out id) ? id.Clone() : (JsonElement?)null;
        }

        private static string GetString(JsonElement element, string property)
        {
            JsonElement value;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static bool TryGetInt64(JsonElement element, string property, out long value)
        {
            JsonElement item;
            value = 0;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out item) && item.TryGetInt64(out value);
        }

        private static string CreateToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private static PipeSecurity CreatePipeSecurity()
        {
            var identity = WindowsIdentity.GetCurrent();
            if (identity == null || identity.User == null)
                throw new InvalidOperationException("The current Windows user identity is unavailable.");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }

        private static bool FixedTimeEquals(string expected, string actual)
        {
            if (expected == null || actual == null) return false;
            var left = Encoding.UTF8.GetBytes(expected);
            var right = Encoding.UTF8.GetBytes(actual);
            if (left.Length == 0 || right.Length == 0) return left.Length == right.Length;
            var difference = left.Length ^ right.Length;
            var length = Math.Max(left.Length, right.Length);
            for (var index = 0; index < length; index++)
                difference |= left[index % left.Length] ^ right[index % right.Length];
            return difference == 0;
        }

        private bool IsProjectScopeCurrent(string threadId, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(threadId)) return true;
            string expectedRoot;
            if (!_threadRoots.TryGetValue(threadId, out expectedRoot))
            {
                error = "The MCP bridge thread scope is unknown.";
                return false;
            }
            var currentRoot = MainWindowVM.CurrentProjectRoot;
            try
            {
                if (!string.IsNullOrWhiteSpace(currentRoot) &&
                    string.Equals(Path.GetFullPath(currentRoot).TrimEnd('\\', '/'), expectedRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
            error = "The active NEW_CLEVER project no longer matches this Codex thread.";
            return false;
        }

        private void CancelAllCalls()
        {
            foreach (var call in _activeCalls.Values)
                try { call.Cancel(); } catch { }
        }

        private void CancelCalls(string keyPrefix)
        {
            foreach (var pair in _activeCalls.Where(item => item.Key.StartsWith(keyPrefix, StringComparison.Ordinal)).ToArray())
            {
                CancellationTokenSource removed;
                if (_activeCalls.TryRemove(pair.Key, out removed))
                {
                    try { removed.Cancel(); } catch { }
                    removed.Dispose();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            CancelAllCalls();
            lock (_serverLock)
            {
                if (_activeServer != null) _activeServer.Dispose();
                _activeServer = null;
            }
            foreach (var server in _connectedServers.Keys)
                try { server.Dispose(); } catch { }
            try { if (_serverTask != null) _serverTask.Wait(2000); } catch { }
            foreach (var call in _activeCalls.Values) call.Dispose();
            _activeCalls.Clear();
            _shutdown.Dispose();
        }
    }
}
