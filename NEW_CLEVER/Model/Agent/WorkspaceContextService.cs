using NEW_CLEVER.CommonData;
using NEW_CLEVER.Model.Diagnostics;
using NEW_CLEVER.Model.Utils;
using NEW_CLEVER.ViewModel;
using NEW_CLEVER.AgentCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NEW_CLEVER.Model.Agent
{
    /// <summary>
    /// Captures a bounded, read-only description of the active NEW_CLEVER project. WPF
    /// state is copied on the application dispatcher; filesystem traversal happens
    /// afterwards and never writes into the workspace.
    /// </summary>
    internal sealed class WorkspaceContextService : IWorkspaceContextService
    {
        private const int MaximumTreeEntries = 1500;
        private const int MaximumTreeDepth = 24;
        private static readonly ISet<string> ExcludedDirectoryNames =
            new HashSet<string>(new[] { ".git", "bin", "obj", ".vs" }, StringComparer.OrdinalIgnoreCase);

        public async Task<OperationResult<WorkspaceContextData>> SaveAllAndCaptureAsync(
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Cancelled();

            OperationResult<bool> saveResult;
            try
            {
                saveResult = await InvokeOnDispatcherAsync(SaveAllOnUiThread).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperationResult<WorkspaceContextData>.Fail(
                    "save_all_failed", "Unable to save all open documents: " + ex.Message);
            }

            if (!saveResult.Success)
                return OperationResult<WorkspaceContextData>.Fail(saveResult.Code, saveResult.Message);
            if (cancellationToken.IsCancellationRequested)
                return Cancelled();

            return await CaptureAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<OperationResult<WorkspaceContextData>> CaptureAsync(
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Cancelled();

            OperationResult<UiSnapshot> uiResult;
            try
            {
                uiResult = await InvokeOnDispatcherAsync(CaptureUiState).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperationResult<WorkspaceContextData>.Fail(
                    "workspace_state_failed", "Unable to capture the IDE workspace state: " + ex.Message);
            }

            if (!uiResult.Success)
                return OperationResult<WorkspaceContextData>.Fail(uiResult.Code, uiResult.Message);
            if (cancellationToken.IsCancellationRequested)
                return Cancelled();

            try
            {
                return await Task.Run(
                    () => BuildContext(uiResult.Data, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Cancelled();
            }
            catch (Exception ex)
            {
                return OperationResult<WorkspaceContextData>.Fail(
                    "workspace_read_failed", "Unable to read the active project: " + ex.Message);
            }
        }

        private static OperationResult<bool> SaveAllOnUiThread()
        {
            var app = Application.Current;
            if (app == null || app.MainWindow == null)
                return OperationResult<bool>.Fail(
                    "ide_not_ready", "The NEW_CLEVER main window is not available for Save All.");

            var viewModel = app.MainWindow.DataContext as MainWindowVM;
            if (viewModel == null || viewModel.FileSaveAllCommand == null)
                return OperationResult<bool>.Fail(
                    "save_all_unavailable", "The NEW_CLEVER Save All command is not available.");

            var documents = GetOpenProgramData();
            var unsaved = documents
                .Where(document => string.IsNullOrWhiteSpace(document.FullPath))
                .Select(document => document.Name ?? "untitled")
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (unsaved.Count > 0)
            {
                return OperationResult<bool>.Fail(
                    "save_all_requires_path",
                    "Save All requires a file path for: " + string.Join(", ", unsaved) +
                    ". Save these documents in the IDE before sending a Codex turn.");
            }

            if (documents.Count == 0)
                return OperationResult<bool>.Ok(true);
            if (!viewModel.FileSaveAllCommand.CanExecute(null))
                return OperationResult<bool>.Fail(
                    "save_all_unavailable", "The NEW_CLEVER Save All command cannot execute right now.");

            viewModel.FileSaveAllCommand.Execute(null);

            foreach (var document in documents)
            {
                if (document.TextChange || !string.IsNullOrWhiteSpace(SafeHeaderChange(document)))
                {
                    return OperationResult<bool>.Fail(
                        "save_all_incomplete", "The document is still marked as modified: " + document.Name);
                }
                if (!File.Exists(document.FullPath))
                {
                    return OperationResult<bool>.Fail(
                        "save_all_incomplete", "The saved document does not exist on disk: " + document.FullPath);
                }

                var editorText = document.Editor == null || document.Editor.TextArea == null
                    ? null
                    : document.Editor.TextArea.Text;
                if (editorText != null)
                {
                    var diskText = File.ReadAllText(document.FullPath);
                    if (!string.Equals(diskText, editorText, StringComparison.Ordinal))
                    {
                        return OperationResult<bool>.Fail(
                            "save_all_incomplete", "The editor and disk content differ after Save All: " + document.FullPath);
                    }
                }
            }

            return OperationResult<bool>.Ok(true);
        }

        private static OperationResult<UiSnapshot> CaptureUiState()
        {
            var root = NormalizeDirectory(MainWindowVM.CurrentProjectRoot);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return OperationResult<UiSnapshot>.Fail(
                    "workspace_not_open", "Open a NEW_CLEVER project before using the Codex workspace context.");
            }

            RobotProfile robot;
            string profileError;
            var settings = AiAppSettingsStore.Settings;
            if (!RobotProfileValidator.TryCreate(
                settings.WheelDiameter,
                settings.WheelBaseWidth,
                settings.RobotDescription,
                out robot,
                out profileError))
            {
                return OperationResult<UiSnapshot>.Fail(
                    "robot_profile_invalid", "The configured robot profile is invalid: " + profileError);
            }

            var snapshot = new UiSnapshot
            {
                ProjectRoot = root,
                ProjectName = MainWindowVM.CurrentProjectName,
                Language = NormalizeLanguage(Configurations.Get.Language),
                Robot = robot,
                Ev3 = CaptureEv3State()
            };

            var activeData = MainWindowVM.Project == null || MainWindowVM.CurrentProgram == null
                ? null
                : MainWindowVM.Project.GetProgramData(MainWindowVM.CurrentProgram);
            if (activeData != null && IsInsideRoot(root, activeData.FullPath))
                snapshot.ActiveDocument = GetRelativePath(root, activeData.FullPath);

            foreach (var document in GetOpenProgramData())
            {
                if (!IsInsideRoot(root, document.FullPath))
                    continue;
                snapshot.OpenDocuments.Add(new WorkspaceDocument
                {
                    RelativePath = GetRelativePath(root, document.FullPath),
                    IsActive = ReferenceEquals(document, activeData),
                    IsDirty = document.TextChange || !string.IsNullOrWhiteSpace(SafeHeaderChange(document))
                });
            }

            snapshot.OpenDocuments = snapshot.OpenDocuments
                .OrderBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(document => document.RelativePath, StringComparer.Ordinal)
                .ToList();

            foreach (var problem in IdeProblemStore.GetSnapshot())
            {
                if (!string.IsNullOrWhiteSpace(problem.FilePath) && !IsInsideRoot(root, problem.FilePath))
                    continue;
                snapshot.Diagnostics.Add(new AgentDiagnostic
                {
                    Id = string.IsNullOrWhiteSpace(problem.Id) ? problem.Code ?? "diagnostic" : problem.Id,
                    Severity = problem.IsError ? "error" : "warning",
                    FilePath = string.IsNullOrWhiteSpace(problem.FilePath)
                        ? ""
                        : GetRelativePath(root, problem.FilePath),
                    Line = problem.Line > 0 ? (int?)problem.Line : null,
                    Column = problem.Column > 0 ? (int?)problem.Column : null,
                    Message = problem.Message ?? ""
                });
            }

            return OperationResult<UiSnapshot>.Ok(snapshot);
        }

        private static OperationResult<WorkspaceContextData> BuildContext(
            UiSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IList<WorkspaceFileEntry> files;
            string error;
            if (!TryBuildTree(snapshot.ProjectRoot, cancellationToken, out files, out error))
                return OperationResult<WorkspaceContextData>.Fail("workspace_read_failed", error);

            return OperationResult<WorkspaceContextData>.Ok(new WorkspaceContextData
            {
                ProjectRoot = snapshot.ProjectRoot,
                ProjectName = snapshot.ProjectName,
                Language = snapshot.Language,
                ActiveDocument = snapshot.ActiveDocument ?? "",
                Files = files,
                OpenDocuments = snapshot.OpenDocuments,
                Diagnostics = snapshot.Diagnostics,
                Robot = snapshot.Robot,
                Ev3 = snapshot.Ev3
            });
        }

        private static bool TryBuildTree(
            string root,
            CancellationToken cancellationToken,
            out IList<WorkspaceFileEntry> entries,
            out string error)
        {
            entries = new List<WorkspaceFileEntry>();
            error = "";
            var pending = new Stack<PendingDirectory>();
            pending.Push(new PendingDirectory { Path = root, Depth = 0, IsRoot = true });

            while (pending.Count > 0 && entries.Count < MaximumTreeEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                FileSystemInfo[] children;
                try
                {
                    children = new DirectoryInfo(current.Path).GetFileSystemInfos()
                        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.Name, StringComparer.Ordinal)
                        .ToArray();
                }
                catch (Exception ex)
                {
                    if (current.IsRoot)
                    {
                        error = "Unable to enumerate project root '" + root + "': " + ex.Message;
                        return false;
                    }
                    continue;
                }

                var childDirectories = new List<PendingDirectory>();
                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entries.Count >= MaximumTreeEntries)
                        break;

                    var directory = child as DirectoryInfo;
                    if (directory != null)
                    {
                        if (ExcludedDirectoryNames.Contains(directory.Name))
                            continue;
                        entries.Add(new WorkspaceFileEntry
                        {
                            RelativePath = GetRelativePath(root, directory.FullName),
                            IsDirectory = true,
                            Size = null,
                            ModifiedAt = SafeModifiedAt(directory)
                        });
                        if (current.Depth < MaximumTreeDepth && !IsReparsePoint(directory))
                        {
                            childDirectories.Add(new PendingDirectory
                            {
                                Path = directory.FullName,
                                Depth = current.Depth + 1,
                                IsRoot = false
                            });
                        }
                        continue;
                    }

                    var file = child as FileInfo;
                    if (file == null)
                        continue;
                    entries.Add(new WorkspaceFileEntry
                    {
                        RelativePath = GetRelativePath(root, file.FullName),
                        IsDirectory = false,
                        Size = SafeLength(file),
                        ModifiedAt = SafeModifiedAt(file)
                    });
                }

                for (var index = childDirectories.Count - 1; index >= 0; index--)
                    pending.Push(childDirectories[index]);
            }

            entries = entries
                .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ToList();
            return true;
        }

        private static IList<ProgramData> GetOpenProgramData()
        {
            if (MainWindowVM.Project == null)
                return new List<ProgramData>();
            return MainWindowVM.Project.GetDictionary().Values
                .Where(document => document != null && !document.IsPreview)
                .Distinct()
                .OrderBy(document => document.FullPath ?? document.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(document => document.FullPath ?? document.Name, StringComparer.Ordinal)
                .ToList();
        }

        private static Ev3StatusData CaptureEv3State()
        {
            var service = NEW_CLEVERAgentServices.Current.Ev3;
            var state = service == null ? null : service.State;
            if (state == null)
                return new Ev3StatusData { Connected = false, Busy = false, ProgramRunning = false };
            return new Ev3StatusData
            {
                Connected = state.Connected,
                Busy = state.Busy,
                ProgramRunning = state.ProgramRunning,
                DeviceName = state.DeviceName ?? "",
                ConnectionType = state.ConnectionType ?? "",
                CurrentPath = state.CurrentPath ?? "",
                LastError = state.LastError ?? ""
            };
        }

        private static string SafeHeaderChange(ProgramData document)
        {
            try
            {
                return document.GetHeaderChangeText ?? "";
            }
            catch
            {
                return document.TextChange ? "modified" : "";
            }
        }

        private static DateTimeOffset? SafeModifiedAt(FileSystemInfo item)
        {
            try
            {
                return new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero);
            }
            catch
            {
                return null;
            }
        }

        private static long? SafeLength(FileInfo file)
        {
            try
            {
                return file.Length;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsReparsePoint(FileSystemInfo item)
        {
            try
            {
                return (item.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsInsideRoot(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                var rootPrefix = NormalizeDirectory(root) + Path.DirectorySeparatorChar;
                var normalizedPath = Path.GetFullPath(path);
                return normalizedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string GetRelativePath(string root, string path)
        {
            try
            {
                var rootPrefix = NormalizeDirectory(root) + Path.DirectorySeparatorChar;
                var normalizedPath = Path.GetFullPath(path);
                if (!normalizedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    return "";
                return normalizedPath.Substring(rootPrefix.Length).Replace('\\', '/');
            }
            catch
            {
                return "";
            }
        }

        private static string NormalizeDirectory(string path)
        {
            try
            {
                return Path.GetFullPath(path ?? "")
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return "";
            }
        }

        private static string NormalizeLanguage(string language)
        {
            var value = (language ?? "en").Trim().ToLowerInvariant();
            return value == "ru" || value == "ua" || value == "en" ? value : "en";
        }

        private static Task<T> InvokeOnDispatcherAsync<T>(Func<T> action)
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher == null)
                return Task.FromResult(action());
            var dispatcher = app.Dispatcher;
            if (dispatcher.CheckAccess())
                return Task.FromResult(action());
            return dispatcher.InvokeAsync(action, DispatcherPriority.Send).Task;
        }

        private static OperationResult<WorkspaceContextData> Cancelled()
        {
            return OperationResult<WorkspaceContextData>.Fail(
                "cancelled", "The workspace context operation was cancelled.");
        }

        private sealed class UiSnapshot
        {
            internal UiSnapshot()
            {
                OpenDocuments = new List<WorkspaceDocument>();
                Diagnostics = new List<AgentDiagnostic>();
                ActiveDocument = "";
            }

            internal string ProjectRoot { get; set; }
            internal string ProjectName { get; set; }
            internal string Language { get; set; }
            internal string ActiveDocument { get; set; }
            internal IList<WorkspaceDocument> OpenDocuments { get; set; }
            internal IList<AgentDiagnostic> Diagnostics { get; set; }
            internal RobotProfile Robot { get; set; }
            internal Ev3StatusData Ev3 { get; set; }
        }

        private sealed class PendingDirectory
        {
            internal string Path { get; set; }
            internal int Depth { get; set; }
            internal bool IsRoot { get; set; }
        }
    }
}
