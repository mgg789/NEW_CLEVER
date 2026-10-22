using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NEW_CLEVER.AgentMcpBridge
{
    internal static class Program
    {
        private const int MaximumLineLength = 8 * 1024 * 1024;
        private const int CreateNoWindow = 0x08000000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        private static int Main(string[] args)
        {
            if (!string.IsNullOrWhiteSpace(ReadArgument(args, "--codex-stdio")))
                return RunCodexStdioLauncher(ReadArgument(args, "--codex-stdio"));
            Console.InputEncoding = new UTF8Encoding(false, true);
            Console.OutputEncoding = new UTF8Encoding(false);
            try { return RunAsync(args).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return 0; }
            catch (Exception ex)
            {
                Console.Error.WriteLine("NEW_CLEVER MCP bridge: " + ex.Message);
                return 1;
            }
        }

        private static int RunCodexStdioLauncher(string codexExecutable)
        {
            if (!File.Exists(codexExecutable))
                throw new FileNotFoundException("The Codex runtime was not found.", codexExecutable);

            // .NET Framework's Process.StandardInput emits an UTF-8 BOM before the first
            // write. app-server JSONL intentionally rejects that BOM, so this launcher
            // consumes exactly the preamble and lets Codex inherit the same stdio handles.
            var input = Console.OpenStandardInput();
            var preamble = new byte[3];
            var offset = 0;
            while (offset < preamble.Length)
            {
                var read = input.Read(preamble, offset, preamble.Length - offset);
                if (read <= 0) return 0;
                offset += read;
            }
            Environment.SetEnvironmentVariable("RUST_LOG", "warn");
            Environment.SetEnvironmentVariable("LOG_FORMAT", "json");
            var hasPreamble = preamble[0] == 0xEF && preamble[1] == 0xBB && preamble[2] == 0xBF;
            return LaunchWithInheritedStandardHandles(
                Path.GetFullPath(codexExecutable),
                "app-server --stdio",
                input,
                hasPreamble ? null : preamble);
        }

        private static int LaunchWithInheritedStandardHandles(string executable, string arguments, Stream sourceInput, byte[] prefix)
        {
            var standardOutput = GetStdHandle(-11);
            var standardError = GetStdHandle(-12);
            SetHandleInformation(standardOutput, 1, 1);
            SetHandleInformation(standardError, 1, 1);
            using (var childInput = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable))
            {
                var childInputHandle = new IntPtr(long.Parse(childInput.GetClientHandleAsString(), CultureInfo.InvariantCulture));
                var startup = new StartupInfo
                {
                    Size = Marshal.SizeOf(typeof(StartupInfo)),
                    Flags = 0x00000100,
                    StandardInput = childInputHandle,
                    StandardOutput = standardOutput,
                    StandardError = standardError
                };
                ProcessInformation process;
                var commandLine = new StringBuilder("\"" + executable.Replace("\"", "\\\"") + "\" " + arguments);
                if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, CreateNoWindow,
                        IntPtr.Zero, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ref startup, out process))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Codex app-server did not start.");
                childInput.DisposeLocalCopyOfClientHandle();
                IntPtr job;
                try
                {
                    job = CreateKillOnCloseJob(process.ProcessHandle);
                }
                catch
                {
                    try { Process.GetProcessById(process.ProcessId).Kill(); } catch { }
                    CloseHandle(process.ThreadHandle);
                    CloseHandle(process.ProcessHandle);
                    throw;
                }
                var proxy = Task.Run(() =>
                {
                    try
                    {
                        if (prefix != null && prefix.Length > 0)
                            childInput.Write(prefix, 0, prefix.Length);
                        sourceInput.CopyTo(childInput);
                        childInput.Flush();
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                });
                try
                {
                    using (var child = Process.GetProcessById(process.ProcessId))
                    {
                        child.WaitForExit();
                        return child.ExitCode;
                    }
                }
                finally
                {
                    childInput.Dispose();
                    try { proxy.Wait(1000); } catch { }
                    CloseHandle(job);
                    CloseHandle(process.ThreadHandle);
                    CloseHandle(process.ProcessHandle);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved;
            public string Desktop;
            public string Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr ProcessHandle;
            public IntPtr ThreadHandle;
            public int ProcessId;
            public int ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public long Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, int creationFlags,
            IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, int informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        private static IntPtr CreateKillOnCloseJob(IntPtr processHandle)
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a Codex containment job.");

            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
            };
            var size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, size) ||
                    !AssignProcessToJobObject(job, processHandle))
                {
                    var error = Marshal.GetLastWin32Error();
                    CloseHandle(job);
                    throw new Win32Exception(error, "Could not contain the Codex app-server process.");
                }
                return job;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            var pipeName = ReadArgument(args, "--pipe");
            var token = Environment.GetEnvironmentVariable("NEW_CLEVER_MCP_PIPE_TOKEN");
            var threadId = Environment.GetEnvironmentVariable("NEW_CLEVER_MCP_THREAD_ID");
            if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("NEW_CLEVER MCP bridge requires --pipe and NEW_CLEVER_MCP_PIPE_TOKEN.");
                return 2;
            }

            Trace("connecting to IDE pipe");

            using (var shutdown = new CancellationTokenSource())
            using (var bridge = await PipeBridgeClient.ConnectAsync(pipeName, token, threadId, shutdown.Token).ConfigureAwait(false))
            using (var outputGate = new SemaphoreSlim(1, 1))
            {
                Trace("IDE pipe authenticated");
                var active = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
                try
                {
                    while (!shutdown.IsCancellationRequested)
                    {
                        var readTask = Console.In.ReadLineAsync();
                        var completed = await Task.WhenAny(readTask, bridge.Disconnected).ConfigureAwait(false);
                        if (completed == bridge.Disconnected) break;
                        var line = await readTask.ConfigureAwait(false);
                        if (line == null) break;
                        if (line.Length > 0 && line[0] == '\uFEFF') line = line.Substring(1);
                        Trace("received MCP input");
                        if (line.Length > MaximumLineLength)
                        {
                            await WriteErrorAsync(outputGate, null, -32700, "JSON-RPC message exceeded the eight-megabyte limit.", shutdown.Token).ConfigureAwait(false);
                            continue;
                        }

                        JsonDocument document;
                        try { document = JsonDocument.Parse(line); }
                        catch (JsonException)
                        {
                            await WriteErrorAsync(outputGate, null, -32700, "Malformed JSON-RPC message.", shutdown.Token).ConfigureAwait(false);
                            continue;
                        }

                        using (document)
                        {
                            var root = document.RootElement;
                            var method = GetString(root, "method");
                            var id = GetId(root);
                            JsonElement parameters;
                            root.TryGetProperty("params", out parameters);

                            if (string.Equals(method, "notifications/initialized", StringComparison.Ordinal))
                                continue;

                            if (string.Equals(method, "notifications/cancelled", StringComparison.Ordinal))
                            {
                                JsonElement requestId;
                                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("requestId", out requestId))
                                {
                                    CancellationTokenSource call;
                                    if (active.TryGetValue(requestId.GetRawText(), out call)) call.Cancel();
                                }
                                continue;
                            }

                            if (string.Equals(method, "shutdown", StringComparison.Ordinal))
                            {
                                await WriteResultAsync(outputGate, id, new { }, shutdown.Token).ConfigureAwait(false);
                                break;
                            }

                            var capturedMethod = method;
                            var capturedId = id.HasValue ? id.Value.Clone() : (JsonElement?)null;
                            var capturedParameters = parameters.ValueKind == JsonValueKind.Undefined ? default(JsonElement) : parameters.Clone();
                            _ = Task.Run(() => HandleRequestAsync(
                                bridge,
                                outputGate,
                                active,
                                capturedMethod,
                                capturedId,
                                capturedParameters,
                                shutdown.Token));
                        }
                    }
                }
                finally
                {
                    shutdown.Cancel();
                    foreach (var call in active.Values) call.Cancel();
                    foreach (var call in active.Values) call.Dispose();
                }
            }
            return 0;
        }

        internal static void Trace(string message)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("NEW_CLEVER_MCP_DIAGNOSTICS"), "1", StringComparison.Ordinal))
                Console.Error.WriteLine("NEW_CLEVER MCP bridge diagnostic: " + message);
        }

        private static async Task HandleRequestAsync(
            PipeBridgeClient bridge,
            SemaphoreSlim outputGate,
            ConcurrentDictionary<string, CancellationTokenSource> active,
            string method,
            JsonElement? id,
            JsonElement parameters,
            CancellationToken shutdown)
        {
            try
            {
                Trace("handling " + (method ?? "<null>"));
                if (string.Equals(method, "initialize", StringComparison.Ordinal))
                {
                    var requestedVersion = parameters.ValueKind == JsonValueKind.Object ? GetString(parameters, "protocolVersion") : null;
                    await WriteResultAsync(outputGate, id, new
                    {
                        protocolVersion = string.IsNullOrWhiteSpace(requestedVersion) ? "2025-06-18" : requestedVersion,
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "NEW_CLEVER-codex-tools", version = "1.0.0" },
                        instructions = "NEW_CLEVER robot and workspace tools. Tool results are authoritative."
                    }, shutdown).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(method, "ping", StringComparison.Ordinal))
                {
                    await bridge.SendAsync("ping", new { }, shutdown).ConfigureAwait(false);
                    await WriteResultAsync(outputGate, id, new { }, shutdown).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(method, "tools/list", StringComparison.Ordinal))
                {
                    var result = await bridge.SendAsync("tools/list", new { }, shutdown).ConfigureAwait(false);
                    await WriteResultAsync(outputGate, id, result, shutdown).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(method, "tools/call", StringComparison.Ordinal))
                {
                    if (!id.HasValue)
                    {
                        await WriteErrorAsync(outputGate, null, -32600, "A tool call id is required.", shutdown).ConfigureAwait(false);
                        return;
                    }
                    var key = id.Value.GetRawText();
                    using (var call = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
                    {
                        if (!active.TryAdd(key, call))
                        {
                            await WriteErrorAsync(outputGate, id, -32600, "Duplicate tool call id.", shutdown).ConfigureAwait(false);
                            return;
                        }
                        try
                        {
                            var hostResult = await bridge.SendAsync("tools/call", parameters, call.Token).ConfigureAwait(false);
                            bool success;
                            JsonElement successProperty;
                            success = hostResult.ValueKind == JsonValueKind.Object
                                && hostResult.TryGetProperty("success", out successProperty)
                                && successProperty.ValueKind == JsonValueKind.True;
                            var envelope = hostResult.GetRawText();
                            await WriteResultAsync(outputGate, id, new
                            {
                                content = new[] { new { type = "text", text = envelope } },
                                structuredContent = hostResult,
                                isError = !success
                            }, shutdown).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            await WriteErrorAsync(outputGate, id, -32800, "Tool call cancelled.", shutdown).ConfigureAwait(false);
                        }
                        finally
                        {
                            CancellationTokenSource removed;
                            active.TryRemove(key, out removed);
                        }
                    }
                    return;
                }

                if (id.HasValue)
                    await WriteErrorAsync(outputGate, id, -32601, "Method not supported: " + (method ?? ""), shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Trace("request failed: " + ex.GetType().Name + ": " + ex.Message);
                if (id.HasValue)
                    await WriteErrorAsync(outputGate, id, -32603, ex.Message, shutdown).ConfigureAwait(false);
            }
        }

        private static async Task WriteResultAsync(SemaphoreSlim gate, JsonElement? id, object result, CancellationToken cancellationToken)
        {
            if (!id.HasValue) return;
            await WriteAsync(gate, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Value, result = result }), cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteErrorAsync(SemaphoreSlim gate, JsonElement? id, int code, string message, CancellationToken cancellationToken)
        {
            await WriteAsync(gate, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id, error = new { code = code, message = message ?? "" } }), cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteAsync(SemaphoreSlim gate, string payload, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Console.Out.WriteLineAsync(payload).ConfigureAwait(false);
                await Console.Out.FlushAsync().ConfigureAwait(false);
                Trace("wrote MCP output");
            }
            finally { gate.Release(); }
        }

        private static JsonElement? GetId(JsonElement root)
        {
            JsonElement id;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out id) ? id.Clone() : (JsonElement?)null;
        }

        private static string GetString(JsonElement root, string property)
        {
            JsonElement value;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static string ReadArgument(string[] args, string name)
        {
            for (var index = 0; index + 1 < args.Length; index++)
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return null;
        }
    }

    internal sealed class PipeBridgeClient : IDisposable
    {
        private const int MaximumLineLength = 8 * 1024 * 1024;
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending =
            new ConcurrentDictionary<long, TaskCompletionSource<JsonElement>>();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _disconnected =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _nextId;

        private PipeBridgeClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 65536, true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };
            Task.Run(() => ReadLoopAsync(_shutdown.Token));
        }

        public Task Disconnected { get { return _disconnected.Task; } }

        public static async Task<PipeBridgeClient> ConnectAsync(string pipeName, string token, string threadId, CancellationToken cancellationToken)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            try
            {
                await pipe.ConnectAsync(10000, cancellationToken).ConfigureAwait(false);
                var client = new PipeBridgeClient(pipe);
                await client.SendAsync("auth", new { token = token, threadId = threadId }, cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        public async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextId);
            Program.Trace("sending IDE request " + id + " " + method);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate bridge request id.");
            try
            {
                await WriteAsync(JsonSerializer.Serialize(new { id = id, method = method, @params = parameters }), cancellationToken).ConfigureAwait(false);
                using (cancellationToken.Register(delegate
                {
                    Task.Run(() => WriteAsync(JsonSerializer.Serialize(new { method = "cancel", @params = new { id = id } }), CancellationToken.None));
                    completion.TrySetCanceled();
                }))
                    return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                TaskCompletionSource<JsonElement> removed;
                _pending.TryRemove(id, out removed);
            }
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            Exception failure = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    Program.Trace("received IDE response");
                    if (line.Length > MaximumLineLength) throw new InvalidDataException("IDE bridge response exceeded the eight-megabyte limit.");
                    using (var document = JsonDocument.Parse(line))
                    {
                        var root = document.RootElement;
                        JsonElement idElement;
                        long id;
                        if (!root.TryGetProperty("id", out idElement) || !idElement.TryGetInt64(out id)) continue;
                        TaskCompletionSource<JsonElement> pending;
                        if (!_pending.TryGetValue(id, out pending))
                        {
                            Program.Trace("no pending IDE request for " + id);
                            continue;
                        }
                        JsonElement error;
                        if (root.TryGetProperty("error", out error))
                        {
                            pending.TrySetException(new InvalidOperationException(GetString(error, "message") ?? "IDE bridge error."));
                            continue;
                        }
                        JsonElement result;
                        pending.TrySetResult(root.TryGetProperty("result", out result) ? result.Clone() : EmptyObject());
                        Program.Trace("completed IDE request " + id);
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                var exception = failure ?? new EndOfStreamException("The NEW_CLEVER IDE disconnected from the MCP bridge.");
                foreach (var pending in _pending.Values) pending.TrySetException(exception);
                _disconnected.TrySetResult(true);
            }
        }

        private async Task WriteAsync(string payload, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(payload).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }
        }

        private static JsonElement EmptyObject()
        {
            using (var document = JsonDocument.Parse("{}")) return document.RootElement.Clone();
        }

        private static string GetString(JsonElement root, string property)
        {
            JsonElement value;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            try { _writer.Dispose(); } catch (IOException) { }
            try { _reader.Dispose(); } catch (IOException) { }
            try { _pipe.Dispose(); } catch (IOException) { }
            _writeGate.Dispose();
            _shutdown.Dispose();
        }
    }
}
