using Clev3r.AgentCore;
using Clever.ViewModel;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Clever.Model.Agent
{
    internal sealed class EditorNavigationService : IEditorNavigationService
    {
        private readonly IWorkspaceContextService _workspace;

        internal EditorNavigationService(IWorkspaceContextService workspace)
        {
            _workspace = workspace ?? throw new ArgumentNullException("workspace");
        }

        public async Task<OperationResult<EditorNavigationResult>> OpenAsync(string path, int? line, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path))
                return OperationResult<EditorNavigationResult>.Fail("path_required", "A project-relative file path is required.");
            if (line.HasValue && line.Value < 1)
                return OperationResult<EditorNavigationResult>.Fail("invalid_line", "Line must be at least 1.");

            var context = await _workspace.CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (!context.Success || context.Data == null || string.IsNullOrWhiteSpace(context.Data.ProjectRoot))
                return OperationResult<EditorNavigationResult>.Fail(context.Code ?? "project_missing", context.Message ?? "Open a project first.");

            string fullPath;
            string relativePath;
            string error;
            if (!TryResolveProjectFile(context.Data.ProjectRoot, path, out fullPath, out relativePath, out error))
                return OperationResult<EditorNavigationResult>.Fail("invalid_project_path", error);

            cancellationToken.ThrowIfCancellationRequested();
            return await InvokeOnDispatcherAsync(() =>
            {
                if (!string.Equals(NormalizeDirectory(MainWindowVM.CurrentProjectRoot), NormalizeDirectory(context.Data.ProjectRoot), StringComparison.OrdinalIgnoreCase))
                    return OperationResult<EditorNavigationResult>.Fail("workspace_changed", "The active project changed before the editor could open the file.");
                bool alreadyOpen;
                int? selectedLine;
                string navigationError;
                if (!MainWindowVM.TryOpenProjectFileAtLine(fullPath, line, out alreadyOpen, out selectedLine, out navigationError))
                    return OperationResult<EditorNavigationResult>.Fail("editor_navigation_failed", navigationError);
                return OperationResult<EditorNavigationResult>.Ok(new EditorNavigationResult
                {
                    RelativePath = relativePath,
                    Line = selectedLine,
                    AlreadyOpen = alreadyOpen
                }, alreadyOpen ? "Existing editor tab activated." : "File opened in the editor.");
            }).ConfigureAwait(false);
        }

        private static bool TryResolveProjectFile(string root, string path, out string fullPath, out string relativePath, out string error)
        {
            fullPath = "";
            relativePath = "";
            error = "";
            try
            {
                var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var candidate = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(normalizedRoot, path));
                var prefix = normalizedRoot + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    error = "The requested file must stay inside the active project.";
                    return false;
                }
                var file = new FileInfo(candidate);
                if (!file.Exists || IsReparsePoint(file) || HasReparsePointInProjectPath(normalizedRoot, candidate))
                {
                    error = "The requested project file is unavailable.";
                    return false;
                }
                if (!MainWindowVM.IsSupportedEditorExtension(file.Extension))
                {
                    error = "The requested file type cannot be opened in the CLEV3R editor.";
                    return false;
                }
                fullPath = candidate;
                relativePath = candidate.Substring(prefix.Length).Replace('\\', '/');
                return true;
            }
            catch
            {
                error = "The requested project path is invalid.";
                return false;
            }
        }

        private static bool IsReparsePoint(FileSystemInfo item)
        {
            try { return (item.Attributes & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        private static string NormalizeDirectory(string path)
        {
            try { return Path.GetFullPath(path ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return ""; }
        }

        private static bool HasReparsePointInProjectPath(string root, string fullPath)
        {
            try
            {
                var relative = fullPath.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var current = root;
                foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, segment);
                    if (IsReparsePoint(new FileInfo(current)))
                        return true;
                }
                return false;
            }
            catch { return true; }
        }

        private static Task<T> InvokeOnDispatcherAsync<T>(Func<T> action)
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher == null || app.Dispatcher.CheckAccess())
                return Task.FromResult(action());
            return app.Dispatcher.InvokeAsync(action, DispatcherPriority.Send).Task;
        }
    }
}
