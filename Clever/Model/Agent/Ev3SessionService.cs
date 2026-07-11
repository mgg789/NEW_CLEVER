using Clev3r.AgentCore;
using Clever.Brick;
using Clever.Brick.Communication;
using Clever.ViewModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Clever.Model.Agent
{
    internal sealed class Ev3SessionService : IEv3SessionService, IDisposable
    {
        private const string DefaultProjectPath = "/home/root/lms2012/prjs/";
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _transferSync = new object();
        private readonly IBpBuildService _buildService;
        private EV3Connection _connection;
        private Ev3StatusData _state;
        private bool _disposed;
        private CancellationTokenSource _activeTransfer;
        private int _stopRequested;

        internal Ev3SessionService(IBpBuildService buildService)
        {
            _buildService = buildService ?? throw new ArgumentNullException("buildService");
            _state = new Ev3StatusData { CurrentPath = DefaultProjectPath };
        }

        public Ev3StatusData State
        {
            get { return CloneState(_state); }
        }

        public event EventHandler StateChanged;

        internal EV3Connection CurrentConnection
        {
            get { return _connection; }
        }

        internal void AttachExistingConnection(EV3Connection connection, string currentPath)
        {
            _connection = connection;
            _state.Connected = connection != null && connection.IsOpen();
            _state.ConnectionType = connection == null ? "" : connection.GetType().Name;
            _state.CurrentPath = NormalizeDirectoryPath(currentPath);
            if (_state.Connected)
                _state.DeviceName = connection.GetEV3DeviceName();
            RaiseStateChanged();
        }

        public Task<OperationResult<Ev3StatusData>> ConnectAsync(CancellationToken cancellationToken)
        {
            return RunExclusiveAsync("connect", cancellationToken, () =>
            {
                if (IsConnected())
                    return OperationResult<Ev3StatusData>.Ok(State, "EV3 is already connected.");

                EV3Connection connection = null;
                var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    connection = dispatcher.Invoke(() => ConnectionFinder.CreateConnection(true, true));
                else
                    connection = ConnectionFinder.CreateConnection(true, true);

                _connection = connection;
                _state.Connected = IsConnected();
                _state.ConnectionType = connection == null ? "" : connection.GetType().Name;
                _state.DeviceName = _state.Connected ? connection.GetEV3DeviceName() : "";
                _state.CurrentPath = DefaultProjectPath;
                _state.LastError = "";
                RaiseStateChanged();
                return _state.Connected
                    ? OperationResult<Ev3StatusData>.Ok(State, "EV3 connected.")
                    : OperationResult<Ev3StatusData>.Fail("ev3_not_found", "No EV3 connection was established.");
            });
        }

        public Task<OperationResult<Ev3StatusData>> DisconnectAsync(CancellationToken cancellationToken)
        {
            return RunExclusiveAsync("disconnect", cancellationToken, () =>
            {
                CloseConnection();
                return OperationResult<Ev3StatusData>.Ok(State, "EV3 disconnected.");
            });
        }

        public Task<OperationResult<Ev3StatusData>> RefreshStatusAsync(CancellationToken cancellationToken)
        {
            return RunExclusiveAsync("status", cancellationToken, () =>
            {
                if (!IsConnected())
                {
                    MarkDisconnected();
                    return OperationResult<Ev3StatusData>.Ok(State, "EV3 is not connected.");
                }

                _state.Connected = true;
                _state.DeviceName = _connection.GetEV3DeviceName();
                _state.ConnectionType = _connection.GetType().Name;
                _state.ProgramRunning = IsProgramRunningCore();
                _state.LastError = "";
                RaiseStateChanged();
                return OperationResult<Ev3StatusData>.Ok(State);
            });
        }

        public Task<OperationResult<IList<Ev3FileEntry>>> ListAsync(string path, CancellationToken cancellationToken)
        {
            return RunExclusiveAsync("list", cancellationToken, () =>
            {
                EnsureConnected();
                var normalized = NormalizeDirectoryPath(path);
                var entries = ListCore(normalized);
                _state.CurrentPath = normalized;
                RaiseStateChanged();
                return OperationResult<IList<Ev3FileEntry>>.Ok(entries);
            });
        }

        public Task<OperationResult<Ev3FileContent>> ReadAsync(string path, int maxBytes, CancellationToken cancellationToken)
        {
            return RunExclusiveAsync("read", cancellationToken, () =>
            {
                EnsureConnected();
                var normalized = NormalizeFilePath(path);
                var data = _connection.ReadEV3File(ToInternalPath(normalized));
                if (data == null)
                    return OperationResult<Ev3FileContent>.Fail("ev3_read_failed", "EV3 returned no file data.");

                maxBytes = maxBytes <= 0 ? 262144 : Math.Min(maxBytes, 1048576);
                var length = Math.Min(data.Length, maxBytes);
                var binary = IsBinaryPath(normalized);
                var content = binary
                    ? Convert.ToBase64String(data, 0, length)
                    : Encoding.UTF8.GetString(data, 0, length);
                return OperationResult<Ev3FileContent>.Ok(new Ev3FileContent
                {
                    Path = normalized,
                    Encoding = binary ? "base64" : "utf-8",
                    Content = content,
                    Size = data.LongLength,
                    Truncated = length < data.Length
                });
            });
        }

        public Task<OperationResult<bool>> UploadAsync(string localPath, string destinationPath, CancellationToken cancellationToken)
        {
            return RunExclusiveAsync("upload", cancellationToken, () =>
            {
                EnsureConnected();
                var source = RequireProjectFile(localPath);
                var target = NormalizeFilePath(destinationPath);
                return RunTransfer(cancellationToken, transferToken =>
                {
                    UploadFileCore(source, target, transferToken);
                    return OperationResult<bool>.Ok(true, "File uploaded to EV3.");
                });
            });
        }

        public Task<OperationResult<bool>> CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            return RunExclusiveAsync("mkdir", cancellationToken, () =>
            {
                EnsureConnected();
                CreateDirectoryCore(NormalizeDirectoryPath(path));
                return OperationResult<bool>.Ok(true, "EV3 directory created.");
            });
        }

        public Task<OperationResult<bool>> DeleteAsync(string path, CancellationToken cancellationToken)
        {
            return RunExclusiveAsync("delete", cancellationToken, () =>
            {
                EnsureConnected();
                var normalized = NormalizeFilePath(path);
                if (string.Equals(normalized, "/", StringComparison.Ordinal) || string.Equals(normalized, DefaultProjectPath.TrimEnd('/'), StringComparison.Ordinal))
                    return OperationResult<bool>.Fail("ev3_delete_forbidden", "The EV3 root and project root cannot be deleted.");

                var buffer = new BinaryBuffer();
                buffer.AppendZeroTerminated(ToInternalPath(normalized));
                var response = _connection.SystemCommand(EV3Connection.DELETE_FILE, buffer);
                EnsureSystemSuccess(response, "DELETE_FILE");
                return OperationResult<bool>.Ok(true, "EV3 path deleted.");
            });
        }

        public Task<OperationResult<bool>> SetDeviceNameAsync(string name, CancellationToken cancellationToken)
        {
            return RunExclusiveAsync("set_name", cancellationToken, () =>
            {
                EnsureConnected();
                name = (name ?? "").Trim();
                if (name.Length == 0 || name.Length > 32)
                    return OperationResult<bool>.Fail("ev3_name_invalid", "EV3 name must contain 1 to 32 characters.");

                var command = new ByteCodeBuffer();
                command.OP(0xD4);
                command.CONST(0x08);
                command.STRING(name);
                _connection.DirectCommand(command, 0, 0);
                _state.DeviceName = _connection.GetEV3DeviceName();
                RaiseStateChanged();
                return OperationResult<bool>.Ok(true, "EV3 name updated.");
            });
        }

        public Task<OperationResult<Ev3DeploymentResult>> DeployAsync(Ev3DeploymentRequest request, CancellationToken cancellationToken)
        {
            return RunExclusiveAsync("deploy", cancellationToken, () =>
            {
                EnsureConnected();
                if (request == null)
                    return OperationResult<Ev3DeploymentResult>.Fail("deploy_request_missing", "A deployment request is required.");

                var buildLookup = _buildService.GetBuild(request.BuildId);
                if (!buildLookup.Success || buildLookup.Data == null)
                    return OperationResult<Ev3DeploymentResult>.Fail(buildLookup.Code, buildLookup.Message);

                return RunTransfer(cancellationToken, transferToken =>
                {
                    var build = buildLookup.Data;
                    var destination = ResolveDestination(build, request.DestinationDirectory);
                    var deployed = new Ev3DeploymentResult();
                    CreateDirectoryCore(destination);
                    UploadBuildFile(build.ArtifactPath, destination + build.TargetFileName, deployed, transferToken);
                    UploadGroup(build.MediaFiles, destination + "Media/", deployed, transferToken);
                    UploadGroup(build.FileAssets, destination + "Files/", deployed, transferToken);
                    UploadGroup(build.SourceFiles, destination + "Source/", deployed, transferToken);
                    deployed.ProgramPath = destination + build.TargetFileName;
                    return OperationResult<Ev3DeploymentResult>.Ok(deployed, "Build deployed to EV3.");
                });
            });
        }

        public Task<OperationResult<bool>> RunAsync(string path, CancellationToken cancellationToken)
        {
            return RunExclusiveAsync("run", cancellationToken, () =>
            {
                EnsureConnected();
                var normalized = NormalizeFilePath(path);
                if (!normalized.EndsWith(".rbf", StringComparison.OrdinalIgnoreCase))
                    return OperationResult<bool>.Fail("ev3_program_invalid", "Only .rbf files can be run on EV3.");
                if (IsProgramRunningCore())
                    return OperationResult<bool>.Fail("ev3_program_running", "An EV3 program is already running.");

                var command = new ByteCodeBuffer();
                command.OP(0xC0);
                command.CONST(0x08);
                command.CONST(1);
                command.STRING(ToInternalPath(normalized));
                command.GLOBVAR(0);
                command.GLOBVAR(4);
                command.OP(0x03);
                command.CONST(1);
                command.GLOBVAR(0);
                command.GLOBVAR(4);
                command.CONST(0);
                _connection.DirectCommand(command, 10, 0);
                _state.ProgramRunning = true;
                RaiseStateChanged();
                return OperationResult<bool>.Ok(true, "EV3 program started.");
            });
        }

        public async Task<OperationResult<bool>> StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stopRequested);
            CancelActiveTransfer();
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await Task.Run(() =>
                    {
                        EnsureConnected();
                        if (!IsProgramRunningCore())
                            return OperationResult<bool>.Ok(true, "No EV3 program is running.");
                        var command = new ByteCodeBuffer();
                        command.OP(0x02);
                        command.CONST(1);
                        _connection.DirectCommand(command, 0, 0);
                        _state.ProgramRunning = false;
                        RaiseStateChanged();
                        return OperationResult<bool>.Ok(true, "EV3 program stopped.");
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _state.LastError = ex.Message;
                    _state.Connected = IsConnected();
                    RaiseStateChanged();
                    return OperationResult<bool>.Fail("ev3_stop_failed", ex.Message);
                }
                finally { _gate.Release(); }
            }
            finally { Interlocked.Decrement(ref _stopRequested); }
        }

        private async Task<OperationResult<T>> RunExclusiveAsync<T>(string operation, CancellationToken cancellationToken, Func<OperationResult<T>> action, bool markBusy = true)
        {
            ThrowIfDisposed();
            await WaitForRegularOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (markBusy)
                {
                    _state.Busy = true;
                    RaiseStateChanged();
                }
                return await Task.Run(action, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _state.LastError = ex.Message;
                _state.Connected = IsConnected();
                RaiseStateChanged();
                return OperationResult<T>.Fail("ev3_" + operation + "_failed", ex.Message);
            }
            finally
            {
                _state.Busy = false;
                RaiseStateChanged();
                _gate.Release();
            }
        }

        private async Task WaitForRegularOperationAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _stopRequested) == 0) return;
                _gate.Release();
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }

        private T RunTransfer<T>(CancellationToken cancellationToken, Func<CancellationToken, T> action)
        {
            var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_transferSync) _activeTransfer = transfer;
            try { return action(transfer.Token); }
            finally
            {
                lock (_transferSync)
                    if (ReferenceEquals(_activeTransfer, transfer)) _activeTransfer = null;
                transfer.Dispose();
            }
        }

        private void CancelActiveTransfer()
        {
            CancellationTokenSource transfer;
            lock (_transferSync) transfer = _activeTransfer;
            if (transfer != null)
                try { transfer.Cancel(); } catch { }
        }

        private IList<Ev3FileEntry> ListCore(string directory)
        {
            using (var data = new MemoryStream())
            {
                var buffer = new BinaryBuffer();
                buffer.Append16(500);
                buffer.AppendZeroTerminated(ToInternalPath(directory));
                var response = _connection.SystemCommand(EV3Connection.LIST_FILES, buffer);
                if (response == null || response.Length < 6)
                    throw new InvalidDataException("EV3 returned an invalid LIST_FILES response.");
                EnsureListSuccess(response, "LIST_FILES");
                var handle = response[5] & 0xff;
                data.Write(response, 6, response.Length - 6);

                while (response[0] != EV3Connection.END_OF_FILE)
                {
                    buffer.Clear();
                    buffer.Append8(handle);
                    buffer.Append16(500);
                    response = _connection.SystemCommand(EV3Connection.CONTINUE_LIST_FILES, buffer);
                    if (response == null || response.Length < 2)
                        throw new InvalidDataException("EV3 returned an invalid CONTINUE_LIST_FILES response.");
                    EnsureListSuccess(response, "CONTINUE_LIST_FILES");
                    data.Write(response, 2, response.Length - 2);
                }

                data.Position = 0;
                var entries = new List<Ev3FileEntry>();
                using (var reader = new StreamReader(data, Encoding.GetEncoding("iso-8859-1"), false, 1024, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.EndsWith("/", StringComparison.Ordinal))
                        {
                            var name = line.TrimEnd('/');
                            if (name != "." && name != "..")
                                entries.Add(new Ev3FileEntry { Name = name, Path = directory + name + "/", IsDirectory = true });
                            continue;
                        }

                        var first = line.IndexOf(' ');
                        var second = first < 0 ? -1 : line.IndexOf(' ', first + 1);
                        if (first < 0 || second < 0)
                            continue;
                        long size;
                        long.TryParse(line.Substring(first, second - first).Trim(), System.Globalization.NumberStyles.HexNumber, null, out size);
                        var fileName = line.Substring(second + 1);
                        entries.Add(new Ev3FileEntry
                        {
                            Name = fileName,
                            Path = directory + fileName,
                            Size = size,
                            IsRunnable = fileName.EndsWith(".rbf", StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
                return entries.OrderBy(item => item.IsDirectory ? 0 : 1).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        private void UploadGroup(IEnumerable<string> files, string destination, Ev3DeploymentResult result, CancellationToken cancellationToken)
        {
            var existing = (files ?? Enumerable.Empty<string>()).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (existing.Count == 0)
                return;
            CreateDirectoryCore(destination);
            foreach (var file in existing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UploadBuildFile(file, destination + Path.GetFileName(file), result, cancellationToken);
            }
        }

        private void UploadBuildFile(string localPath, string destination, Ev3DeploymentResult result, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UploadFileCore(localPath, NormalizeFilePath(destination), cancellationToken);
            result.UploadedPaths.Add(NormalizeFilePath(destination));
        }

        private void UploadFileCore(string localPath, string destination, CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > int.MaxValue)
                    throw new IOException("EV3 uploads larger than 2 GiB are not supported.");
                _connection.CreateEV3File(ToInternalPath(destination), stream, (int)stream.Length, cancellationToken);
            }
        }

        private void CreateDirectoryCore(string path)
        {
            path = NormalizeDirectoryPath(path);
            if (path == "/")
                return;
            var buffer = new BinaryBuffer();
            buffer.AppendZeroTerminated(ToInternalPath(path.TrimEnd('/')));
            var response = _connection.SystemCommand(EV3Connection.CREATE_DIR, buffer);
            if (response != null && response.Length > 0 && response[0] != EV3Connection.SUCCESS && response[0] != EV3Connection.FILE_EXITS)
                throw new IOException("Unexpected status at CREATE_DIR: " + response[0]);
        }

        private static string ResolveDestination(BpBuildResult build, string requested)
        {
            if (string.Equals(build.DestinationKind, "SD", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(build.DestinationName))
                return NormalizeDirectoryPath(DefaultProjectPath + "SD_Card/" + build.DestinationName);
            if (string.Equals(build.DestinationKind, "PRJS", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(build.DestinationName))
                return NormalizeDirectoryPath(DefaultProjectPath + build.DestinationName);
            return NormalizeDirectoryPath(string.IsNullOrWhiteSpace(requested) ? DefaultProjectPath : requested);
        }

        private bool IsProgramRunningCore()
        {
            var command = new ByteCodeBuffer();
            command.OP(0x0C);
            command.CONST(0x18);
            command.CONST(1);
            command.GLOBVAR(0);
            var response = _connection.DirectCommand(command, 1, 0);
            return response != null && response.Length > 0 && response[0] == 1;
        }

        private bool IsConnected()
        {
            try { return _connection != null && _connection.IsOpen(); }
            catch { return false; }
        }

        private void EnsureConnected()
        {
            if (!IsConnected())
                throw new InvalidOperationException("EV3 is not connected.");
        }

        private string RequireProjectFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A local file path is required.");
            var fullPath = Path.GetFullPath(path);
            if (!MainWindowVM.IsPathInsideCurrentProject(fullPath))
                throw new UnauthorizedAccessException("Only files inside the active project can be uploaded.");
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Local file was not found.", fullPath);
            return fullPath;
        }

        private static string NormalizeDirectoryPath(string path)
        {
            path = NormalizeFilePath(string.IsNullOrWhiteSpace(path) ? DefaultProjectPath : path);
            return path == "/" ? path : path.TrimEnd('/') + "/";
        }

        private static string NormalizeFilePath(string path)
        {
            path = (path ?? "").Trim().Replace('\\', '/');
            if (!path.StartsWith("/", StringComparison.Ordinal))
                path = DefaultProjectPath + path;
            var parts = new List<string>();
            foreach (var part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".")
                    continue;
                if (part == "..")
                    throw new ArgumentException("EV3 paths cannot contain '..'.");
                parts.Add(part);
            }
            var normalized = "/" + string.Join("/", parts);
            if (normalized.StartsWith("/proc", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The /proc directory is not available through CLEV3R tools.");
            return normalized;
        }

        private static string ToInternalPath(string absolutePath)
        {
            if (absolutePath.StartsWith("/home/root/lms2012/", StringComparison.Ordinal))
                return ".." + absolutePath.Substring(18);
            return "/." + absolutePath;
        }

        private static bool IsBinaryPath(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".rbf" || extension == ".rgf" || extension == ".rsf" || extension == ".bin";
        }

        private static void EnsureListSuccess(byte[] response, string operation)
        {
            if (response[0] != EV3Connection.SUCCESS && response[0] != EV3Connection.END_OF_FILE)
                throw new IOException("Unexpected status at " + operation + ": " + response[0]);
        }

        private static void EnsureSystemSuccess(byte[] response, string operation)
        {
            if (response == null || response.Length == 0)
                throw new IOException("No response to " + operation + ".");
            if (response[0] != EV3Connection.SUCCESS && response[0] != EV3Connection.END_OF_FILE)
                throw new IOException("Unexpected status at " + operation + ": " + response[0]);
        }

        private void CloseConnection()
        {
            if (_connection != null)
            {
                try { _connection.Close(); } catch { }
                _connection = null;
            }
            MarkDisconnected();
        }

        private void MarkDisconnected()
        {
            _state.Connected = false;
            _state.Busy = false;
            _state.ProgramRunning = false;
            _state.DeviceName = "";
            _state.ConnectionType = "";
            RaiseStateChanged();
        }

        private static Ev3StatusData CloneState(Ev3StatusData state)
        {
            return new Ev3StatusData
            {
                Connected = state.Connected,
                Busy = state.Busy,
                ProgramRunning = state.ProgramRunning,
                DeviceName = state.DeviceName ?? "",
                ConnectionType = state.ConnectionType ?? "",
                CurrentPath = state.CurrentPath ?? DefaultProjectPath,
                LastError = state.LastError ?? ""
            };
        }

        private void RaiseStateChanged()
        {
            var handler = StateChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("Ev3SessionService");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CancelActiveTransfer();
            CloseConnection();
            _gate.Dispose();
        }
    }
}
