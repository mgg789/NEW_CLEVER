using NEW_CLEVER.AgentCore;
using NEW_CLEVER.Model.Utils;
using NEW_CLEVER.ViewModel;
using NEW_CLEVER.ViewModel.PanelsVM;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NEW_CLEVER.Model.Agent
{
    internal sealed class GitWorkspaceService : IGitWorkspaceService
    {
        private static readonly SemaphoreSlim Operations = new SemaphoreSlim(1, 1);
        private readonly IWorkspaceContextService _workspace;
        private readonly GitRepositoryService _git = new GitRepositoryService();

        internal GitWorkspaceService(IWorkspaceContextService workspace)
        {
            _workspace = workspace ?? throw new ArgumentNullException("workspace");
        }

        public async Task<OperationResult<GitWorkspaceStatus>> GetStatusAsync(CancellationToken cancellationToken)
        {
            var root = await GetRepositoryRootAsync(false, cancellationToken).ConfigureAwait(false);
            if (!root.Success)
                return OperationResult<GitWorkspaceStatus>.Fail(root.Code, root.Message);

            await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = GitOperationCoordinator.Run(() => _git.GetSnapshot(root.Data));
                return OperationResult<GitWorkspaceStatus>.Ok(new GitWorkspaceStatus
                {
                    RepositoryRoot = snapshot.RepositoryRoot,
                    CurrentBranch = snapshot.BranchName,
                    HeadHash = snapshot.HeadHash,
                    Remote = snapshot.Remote == null ? "" : snapshot.Remote.DisplayUrl,
                    OutgoingCommitCount = snapshot.OutgoingCommitCount,
                    IncomingCommitCount = snapshot.IncomingCommitCount,
                    Branches = snapshot.Branches.Select(item => item.Name).ToList(),
                    ChangedFiles = snapshot.Changes.Select(item => item.RelativePath).ToList()
                });
            }
            finally { Operations.Release(); }
        }

        public Task<OperationResult<GitWorkspaceAction>> CommitAsync(string message, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(message))
                return Task.FromResult(OperationResult<GitWorkspaceAction>.Fail("commit_message_required", "Commit message is required."));
            return RunActionAsync(true, cancellationToken, root => _git.CommitAll(root, message.Trim()), false);
        }

        public Task<OperationResult<GitWorkspaceAction>> PushAsync(CancellationToken cancellationToken)
        {
            return RunActionAsync(false, cancellationToken, root => _git.PushAllBranches(root), false);
        }

        public Task<OperationResult<GitWorkspaceAction>> PullAsync(CancellationToken cancellationToken)
        {
            return RunActionAsync(false, cancellationToken, root => _git.PullAllBranches(root), true);
        }

        public Task<OperationResult<GitWorkspaceAction>> CreateBranchAsync(string name, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Task.FromResult(OperationResult<GitWorkspaceAction>.Fail("branch_name_required", "Branch name is required."));
            return RunActionAsync(false, cancellationToken, root => _git.CreateBranch(root, name.Trim()), true);
        }

        public Task<OperationResult<GitWorkspaceAction>> SwitchBranchAsync(string name, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Task.FromResult(OperationResult<GitWorkspaceAction>.Fail("branch_name_required", "Branch name is required."));
            return RunActionAsync(false, cancellationToken, root => _git.SwitchBranch(root, name.Trim()), true);
        }

        public async Task<OperationResult<GitWorkspaceAction>> MergeAsync(string sourceBranch, string targetBranch, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sourceBranch) || string.IsNullOrWhiteSpace(targetBranch))
                return OperationResult<GitWorkspaceAction>.Fail("branch_name_required", "Source and target branch names are required.");

            var root = await GetRepositoryRootAsync(false, cancellationToken).ConfigureAwait(false);
            if (!root.Success)
                return OperationResult<GitWorkspaceAction>.Fail(root.Code, root.Message);
            var saved = await EnsureNoUnsavedEditorChangesAsync(root.Data).ConfigureAwait(false);
            if (!saved.Success)
                return OperationResult<GitWorkspaceAction>.Fail(saved.Code, saved.Message);

            await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = GitOperationCoordinator.Run(() => _git.SoftMerge(root.Data, sourceBranch.Trim(), targetBranch.Trim()));
                if (result.Success || result.HasConflicts)
                    await RefreshEditorsAndGitPanelAsync(root.Data).ConfigureAwait(false);
                return ToMergeResult(result, root.Data);
            }
            finally { Operations.Release(); }
        }

        public async Task<OperationResult<GitWorkspaceAction>> ResolveMergeAsync(string take, CancellationToken cancellationToken)
        {
            var takeSource = string.Equals(take, "source", StringComparison.OrdinalIgnoreCase);
            var takeTarget = string.Equals(take, "target", StringComparison.OrdinalIgnoreCase);
            if (!takeSource && !takeTarget)
                return OperationResult<GitWorkspaceAction>.Fail("invalid_merge_side", "Choose either 'source' or 'target' for conflict resolution.");

            var root = await GetRepositoryRootAsync(false, cancellationToken).ConfigureAwait(false);
            if (!root.Success)
                return OperationResult<GitWorkspaceAction>.Fail(root.Code, root.Message);
            var saved = await EnsureNoUnsavedEditorChangesAsync(root.Data).ConfigureAwait(false);
            if (!saved.Success)
                return OperationResult<GitWorkspaceAction>.Fail(saved.Code, saved.Message);
            await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = GitOperationCoordinator.Run(() => _git.ResolveMergeConflicts(root.Data, takeSource));
                if (result.Success || result.HasConflicts)
                    await RefreshEditorsAndGitPanelAsync(root.Data).ConfigureAwait(false);
                return ToMergeResult(result, root.Data);
            }
            finally { Operations.Release(); }
        }

        public async Task<OperationResult<GitWorkspaceAction>> AbortMergeAsync(CancellationToken cancellationToken)
        {
            var root = await GetRepositoryRootAsync(false, cancellationToken).ConfigureAwait(false);
            if (!root.Success)
                return OperationResult<GitWorkspaceAction>.Fail(root.Code, root.Message);
            var saved = await EnsureNoUnsavedEditorChangesAsync(root.Data).ConfigureAwait(false);
            if (!saved.Success)
                return OperationResult<GitWorkspaceAction>.Fail(saved.Code, saved.Message);
            return await RunActionWithRootAsync(root.Data, cancellationToken, currentRoot => _git.AbortMerge(currentRoot), true).ConfigureAwait(false);
        }

        private async Task<OperationResult<GitWorkspaceAction>> RunActionAsync(bool saveEditors, CancellationToken cancellationToken, Func<string, GitActionResult> action, bool reloadEditors)
        {
            var root = await GetRepositoryRootAsync(saveEditors, cancellationToken).ConfigureAwait(false);
            if (!root.Success)
                return OperationResult<GitWorkspaceAction>.Fail(root.Code, root.Message);
            if (!saveEditors)
            {
                var saved = await EnsureNoUnsavedEditorChangesAsync(root.Data).ConfigureAwait(false);
                if (!saved.Success)
                    return OperationResult<GitWorkspaceAction>.Fail(saved.Code, saved.Message);
            }
            return await RunActionWithRootAsync(root.Data, cancellationToken, action, reloadEditors).ConfigureAwait(false);
        }

        private async Task<OperationResult<GitWorkspaceAction>> RunActionWithRootAsync(string root, CancellationToken cancellationToken, Func<string, GitActionResult> action, bool reloadEditors)
        {
            await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = GitOperationCoordinator.Run(() => action(root));
                if (result.Success)
                {
                    if (reloadEditors)
                        await RefreshEditorsAndGitPanelAsync(root).ConfigureAwait(false);
                    else
                        await NotifyGitPanelAsync().ConfigureAwait(false);
                }
                return ToActionResult(result, root);
            }
            finally { Operations.Release(); }
        }

        private async Task<OperationResult<string>> GetRepositoryRootAsync(bool saveEditors, CancellationToken cancellationToken)
        {
            var context = saveEditors
                ? await _workspace.SaveAllAndCaptureAsync(cancellationToken).ConfigureAwait(false)
                : await _workspace.CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (!context.Success || context.Data == null || string.IsNullOrWhiteSpace(context.Data.ProjectRoot))
                return OperationResult<string>.Fail(context.Code ?? "project_missing", context.Message ?? "Open a project first.");

            if (!_git.IsGitInstalled())
                return OperationResult<string>.Fail("git_missing", "Git is not installed. Install Git for Windows, then refresh.");

            var root = GitRepositoryService.FindRepositoryRoot(context.Data.ProjectRoot);
            if (string.IsNullOrWhiteSpace(root))
                return OperationResult<string>.Fail("git_repository_missing", "Initialize a Git repository for the active project first.");
            return OperationResult<string>.Ok(root);
        }

        private static async Task<OperationResult<bool>> EnsureNoUnsavedEditorChangesAsync(string repositoryRoot)
        {
            var hasChanges = await InvokeOnDispatcherAsync(() => MainWindowVM.HasUnsavedProjectEditorChanges(repositoryRoot)).ConfigureAwait(false);
            return hasChanges
                ? OperationResult<bool>.Fail("unsaved_editor_changes", "Save open project files before this Git action.")
                : OperationResult<bool>.Ok(true);
        }

        private static async Task RefreshEditorsAndGitPanelAsync(string repositoryRoot)
        {
            await InvokeOnDispatcherAsync(() =>
            {
                MainWindowVM.ReloadOpenProjectFilesFromDisk(repositoryRoot);
                GitPanelVM.NotifyRepositoryChanged();
                return true;
            }).ConfigureAwait(false);
        }

        private static Task NotifyGitPanelAsync()
        {
            return InvokeOnDispatcherAsync(() =>
            {
                GitPanelVM.NotifyRepositoryChanged();
                return true;
            });
        }

        private static OperationResult<GitWorkspaceAction> ToActionResult(GitActionResult result, string root)
        {
            var snapshot = result.Success ? GitOperationCoordinator.Run(() => new GitRepositoryService().GetSnapshot(root)) : null;
            return result.Success
                ? OperationResult<GitWorkspaceAction>.Ok(new GitWorkspaceAction { Message = result.Message, CurrentBranch = snapshot.BranchName }, result.Message)
                : OperationResult<GitWorkspaceAction>.Fail("git_action_failed", result.Message);
        }

        private static OperationResult<GitWorkspaceAction> ToMergeResult(GitMergeResult result, string root)
        {
            var snapshot = GitOperationCoordinator.Run(() => new GitRepositoryService().GetSnapshot(root));
            if (!result.Success && !result.HasConflicts)
                return OperationResult<GitWorkspaceAction>.Fail("git_merge_failed", result.Message);
            return OperationResult<GitWorkspaceAction>.Ok(new GitWorkspaceAction
            {
                Message = result.Message,
                CurrentBranch = snapshot.BranchName,
                MergePrepared = result.Success,
                HasConflicts = result.HasConflicts,
                Conflicts = result.Conflicts.Select(item => item.DisplayText).ToList()
            }, result.Message);
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
