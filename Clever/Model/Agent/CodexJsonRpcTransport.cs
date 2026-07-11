using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Clever.Model.Agent
{
    internal sealed class CodexRpcException : Exception
    {
        public CodexRpcException(int code, string message, string dataJson)
            : base(message ?? "Codex app-server request failed.")
        {
            Code = code;
            DataJson = dataJson ?? "";
        }

        public int Code { get; private set; }
        public string DataJson { get; private set; }
    }

    internal sealed class CodexRpcServerRequest
    {
        public JsonElement Id { get; set; }
        public string Method { get; set; }
        public JsonElement Params { get; set; }
        public string RawJson { get; set; }
    }

    internal sealed class CodexRpcServerResponse
    {
        public bool Success { get; set; }
        public object Result { get; set; }
        public int ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        public static CodexRpcServerResponse Ok(object result)
        {
            return new CodexRpcServerResponse { Success = true, Result = result ?? new Dictionary<string, object>() };
        }

        public static CodexRpcServerResponse Error(int code, string message)
        {
            return new CodexRpcServerResponse
            {
                Success = false,
                ErrorCode = code,
                ErrorMessage = string.IsNullOrWhiteSpace(message) ? "Request failed." : message
            };
        }
    }

    /// <summary>
    /// Bounded, bidirectional JSONL transport used by codex app-server. The app-server wire
    /// format intentionally omits the JSON-RPC 2.0 header.
    /// </summary>
    internal sealed class CodexJsonRpcTransport : IDisposable
    {
        private const int DefaultRequestTimeoutMilliseconds = 30000;
        public const int MaximumMessageBytes = 8 * 1024 * 1024;

        private readonly Stream _input;
        private readonly Stream _output;
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<long, PendingRequest> _pending =
            new ConcurrentDictionary<long, PendingRequest>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        private readonly byte[] _readBuffer = new byte[8192];

        private long _nextRequestId;
        private int _readOffset;
        private int _readCount;
        private int _started;
        private int _disposed;

        public CodexJsonRpcTransport(Stream input, Stream output)
        {
            _input = input ?? throw new ArgumentNullException("input");
            _output = output ?? throw new ArgumentNullException("output");
        }

        public event Action<string, JsonElement, string> NotificationReceived;
        public event Func<CodexRpcServerRequest, Task<CodexRpcServerResponse>> ServerRequestReceived;
        public event Action<Exception> Faulted;

        public void Start()
        {
            ThrowIfDisposed();
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;
            Task.Run((Func<Task>)ReadLoopAsync);
        }

        public async Task<JsonElement> SendRequestAsync(
            string method,
            object parameters,
            CancellationToken cancellationToken,
            int timeoutMilliseconds = DefaultRequestTimeoutMilliseconds)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("A JSON-RPC method is required.", "method");

            var id = Interlocked.Increment(ref _nextRequestId);
            var pending = new PendingRequest();
            if (!_pending.TryAdd(id, pending))
                throw new InvalidOperationException("A duplicate JSON-RPC request id was generated.");

            using (var timeout = new CancellationTokenSource(Math.Max(1, timeoutMilliseconds)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token,
                _lifetime.Token))
            using (linked.Token.Register(() => pending.TryCancel(linked.Token)))
            {
                try
                {
                    var message = new Dictionary<string, object>
                    {
                        { "method", method },
                        { "id", id }
                    };
                    if (parameters != null)
                        message["params"] = parameters;

                    await WriteMessageAsync(message, linked.Token).ConfigureAwait(false);
                    return await pending.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        throw new TimeoutException("Codex app-server request timed out: " + method);
                    throw;
                }
                finally
                {
                    PendingRequest ignored;
                    _pending.TryRemove(id, out ignored);
                }
            }
        }

        public Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("A JSON-RPC method is required.", "method");
            var message = new Dictionary<string, object> { { "method", method } };
            if (parameters != null)
                message["params"] = parameters;
            return WriteMessageAsync(message, cancellationToken);
        }

        private async Task ReadLoopAsync()
        {
            Exception terminal = null;
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var line = await ReadBoundedLineAsync(_lifetime.Token).ConfigureAwait(false);
                    if (line == null)
                        throw new EndOfStreamException("Codex app-server closed stdout.");
                    if (line.Length == 0)
                        continue;

                    using (var document = JsonDocument.Parse(line))
                    {
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException("Codex app-server emitted a non-object JSONL message.");

                        JsonElement methodElement;
                        JsonElement idElement;
                        var hasMethod = root.TryGetProperty("method", out methodElement) &&
                                        methodElement.ValueKind == JsonValueKind.String;
                        var hasId = root.TryGetProperty("id", out idElement) &&
                                    idElement.ValueKind != JsonValueKind.Null;

                        if (hasMethod && hasId)
                        {
                            var request = new CodexRpcServerRequest
                            {
                                Id = idElement.Clone(),
                                Method = methodElement.GetString(),
                                Params = GetParams(root),
                                RawJson = line
                            };
                            _ = Task.Run(() => HandleServerRequestAsync(request));
                        }
                        else if (hasMethod)
                        {
                            RaiseNotification(methodElement.GetString(), GetParams(root), line);
                        }
                        else if (hasId)
                        {
                            CompletePending(idElement, root);
                        }
                        else
                        {
                            throw new InvalidDataException("Codex app-server emitted an unrecognized JSONL message.");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                terminal = ex;
            }
            finally
            {
                if (terminal != null)
                {
                    FailAllPending(terminal);
                    var handler = Faulted;
                    if (handler != null)
                    {
                        try { handler(terminal); } catch { }
                    }
                }
            }
        }

        private async Task HandleServerRequestAsync(CodexRpcServerRequest request)
        {
            CodexRpcServerResponse response;
            var handler = ServerRequestReceived;
            if (handler == null)
            {
                response = CodexRpcServerResponse.Error(-32601, "Method not supported: " + request.Method);
            }
            else
            {
                try
                {
                    response = await handler(request).ConfigureAwait(false);
                    if (response == null)
                        response = CodexRpcServerResponse.Error(-32603, "The client returned no response.");
                }
                catch (OperationCanceledException)
                {
                    response = CodexRpcServerResponse.Error(-32800, "Client request was cancelled.");
                }
                catch (Exception ex)
                {
                    response = CodexRpcServerResponse.Error(-32603, ex.Message);
                }
            }

            var message = new Dictionary<string, object> { { "id", request.Id } };
            if (response.Success)
            {
                message["result"] = response.Result ?? new Dictionary<string, object>();
            }
            else
            {
                message["error"] = new Dictionary<string, object>
                {
                    { "code", response.ErrorCode },
                    { "message", response.ErrorMessage ?? "Request failed." }
                };
            }

            try
            {
                await WriteMessageAsync(message, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                var fault = Faulted;
                if (fault != null)
                {
                    try { fault(ex); } catch { }
                }
            }
        }

        private void CompletePending(JsonElement idElement, JsonElement root)
        {
            long id;
            if (!TryGetInt64(idElement, out id))
                return;

            PendingRequest pending;
            if (!_pending.TryGetValue(id, out pending))
                return;

            JsonElement error;
            if (root.TryGetProperty("error", out error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = GetInt32(error, "code", -32603);
                var message = GetString(error, "message") ?? "Codex app-server request failed.";
                JsonElement data;
                pending.TrySetException(new CodexRpcException(
                    code,
                    message,
                    error.TryGetProperty("data", out data) ? data.GetRawText() : ""));
                return;
            }

            JsonElement result;
            if (root.TryGetProperty("result", out result))
                pending.TrySetResult(result.Clone());
            else
                pending.TrySetResult(EmptyObject());
        }

        private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);
            if (bytes.Length > MaximumMessageBytes)
                throw new InvalidDataException("Codex JSONL message exceeds the 8 MiB limit.");

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _output.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await _output.WriteAsync(new byte[] { (byte)'\n' }, 0, 1, cancellationToken).ConfigureAwait(false);
                await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private async Task<string> ReadBoundedLineAsync(CancellationToken cancellationToken)
        {
            using (var buffer = new MemoryStream())
            {
                while (true)
                {
                    var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                    if (next < 0)
                        return buffer.Length == 0 ? null : Decode(buffer);
                    if (next == (byte)'\n')
                        return Decode(buffer);
                    if (buffer.Length >= MaximumMessageBytes)
                        throw new InvalidDataException("Codex app-server emitted a JSONL message larger than 8 MiB.");
                    if (next != (byte)'\r')
                        buffer.WriteByte((byte)next);
                }
            }
        }

        private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_readOffset >= _readCount)
            {
                _readCount = await _input.ReadAsync(_readBuffer, 0, _readBuffer.Length, cancellationToken)
                    .ConfigureAwait(false);
                _readOffset = 0;
                if (_readCount == 0)
                    return -1;
            }
            return _readBuffer[_readOffset++];
        }

        private static string Decode(MemoryStream buffer)
        {
            return new UTF8Encoding(false, true).GetString(buffer.ToArray());
        }

        private void RaiseNotification(string method, JsonElement parameters, string rawJson)
        {
            var handler = NotificationReceived;
            if (handler == null)
                return;
            try { handler(method, parameters, rawJson); } catch { }
        }

        private static JsonElement GetParams(JsonElement root)
        {
            JsonElement parameters;
            return root.TryGetProperty("params", out parameters) ? parameters.Clone() : EmptyObject();
        }

        private static JsonElement EmptyObject()
        {
            using (var document = JsonDocument.Parse("{}"))
                return document.RootElement.Clone();
        }

        private static bool TryGetInt64(JsonElement value, out long result)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
                return true;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out result))
                return true;
            result = 0;
            return false;
        }

        private static int GetInt32(JsonElement element, string name, int fallback)
        {
            JsonElement value;
            int result;
            return element.TryGetProperty(name, out value) && value.TryGetInt32(out result) ? result : fallback;
        }

        private static string GetString(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value) || value.ValueKind == JsonValueKind.Null)
                return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        }

        private void FailAllPending(Exception exception)
        {
            foreach (var pair in _pending)
                pair.Value.TrySetException(exception);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException("CodexJsonRpcTransport");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _lifetime.Cancel();
            FailAllPending(new ObjectDisposedException("CodexJsonRpcTransport"));
            // Server-request handlers run independently. Disposing the gate here races their
            // finally blocks and can turn a normal shutdown into ObjectDisposedException.
            // SemaphoreSlim has no unmanaged resource, so it is intentionally left for GC.
            _lifetime.Dispose();
        }

        private sealed class PendingRequest
        {
            private readonly TaskCompletionSource<JsonElement> _completion =
                new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<JsonElement> Task { get { return _completion.Task; } }
            public void TrySetResult(JsonElement result) { _completion.TrySetResult(result); }
            public void TrySetException(Exception exception) { _completion.TrySetException(exception); }
            public void TryCancel(CancellationToken token) { _completion.TrySetCanceled(); }
        }
    }
}
