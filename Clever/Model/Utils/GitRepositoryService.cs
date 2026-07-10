using Clever.ViewModel.PanelsVM;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;

namespace Clever.Model.Utils
{
    internal class GitActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }

        internal static GitActionResult Ok(string message)
        {
            return new GitActionResult { Success = true, Message = message };
        }

        internal static GitActionResult Fail(string message)
        {
            return new GitActionResult { Success = false, Message = message };
        }
    }

    internal class GitRepositorySnapshot
    {
        public GitRepositorySnapshot()
        {
            Changes = new List<GitChangeInfo>();
            Branches = new List<GitBranchInfo>();
            Commits = new List<GitCommitVM>();
        }

        public string RepositoryRoot { get; set; }
        public string BranchName { get; set; }
        public string HeadHash { get; set; }
        public GitRemoteInfo Remote { get; set; }
        public int OutgoingCommitCount { get; set; }
        public int IncomingCommitCount { get; set; }
        public List<GitChangeInfo> Changes { get; private set; }
        public List<GitBranchInfo> Branches { get; private set; }
        public List<GitCommitVM> Commits { get; private set; }
    }

    internal class GitChangeInfo
    {
        public string Status { get; set; }
        public string StatusText { get; set; }
        public string RelativePath { get; set; }
        public string OldPath { get; set; }
        public string FullPath { get; set; }
        public bool IsDeleted { get; set; }
        public bool IsUntracked { get; set; }
    }

    internal class GitBranchInfo
    {
        public string Name { get; set; }
        public bool IsActive { get; set; }
    }

    internal enum GitRemoteAuthMode
    {
        LoginPassword,
        LoginToken
    }

    internal class GitRemoteInfo
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string DisplayUrl { get; set; }

        public bool IsConnected
        {
            get { return !string.IsNullOrWhiteSpace(Url); }
        }
    }

    internal class GitBranchRef
    {
        public string Name { get; set; }
        public string Hash { get; set; }
    }

    internal class GitMergeConflictInfo
    {
        public GitMergeConflictInfo()
        {
            Lines = new List<int>();
        }

        public string RelativePath { get; set; }
        public List<int> Lines { get; private set; }

        public string DisplayText
        {
            get
            {
                return Lines.Count == 0
                    ? RelativePath
                    : RelativePath + ": " + string.Join(", ", Lines.Select(line => line.ToString()).ToArray());
            }
        }
    }

    internal class GitMergeResult : GitActionResult
    {
        public GitMergeResult()
        {
            Conflicts = new List<GitMergeConflictInfo>();
        }

        public bool HasConflicts { get; set; }
        public List<GitMergeConflictInfo> Conflicts { get; private set; }

        internal static GitMergeResult MergeOk(string message)
        {
            return new GitMergeResult { Success = true, Message = message };
        }

        internal static GitMergeResult MergeFail(string message)
        {
            return new GitMergeResult { Success = false, Message = message };
        }

        internal static GitMergeResult Conflict(string message, IEnumerable<GitMergeConflictInfo> conflicts)
        {
            var result = new GitMergeResult
            {
                Success = false,
                HasConflicts = true,
                Message = message
            };

            if (conflicts != null)
                result.Conflicts.AddRange(conflicts);

            return result;
        }
    }

    internal class GitRepositoryService
    {
        private const int MaxCommitCount = 50;
        private const int MaxAiPatchLength = 80000;
        private const int MaxAiFileBytes = 256 * 1024;
        private readonly GitCommandRunner _runner;

        internal GitRepositoryService()
        {
            _runner = new GitCommandRunner();
        }

        internal bool IsGitInstalled()
        {
            return _runner.IsGitAvailable();
        }

        internal static string FindRepositoryRoot(string path)
        {
            var directory = ResolveDirectory(path);
            while (!string.IsNullOrEmpty(directory))
            {
                if (Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git")))
                    return directory;

                var parent = Directory.GetParent(directory);
                directory = parent == null ? null : parent.FullName;
            }

            return null;
        }

        internal GitActionResult InitRepository(string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                return GitActionResult.Fail("Git repository folder was not found.");

            var result = _runner.Run(workingDirectory, "init");
            return result.Success
                ? GitActionResult.Ok("Git repository initialized.")
                : GitActionResult.Fail(result.Message);
        }

        internal GitRepositorySnapshot GetSnapshot(string repositoryRoot)
        {
            var snapshot = new GitRepositorySnapshot { RepositoryRoot = repositoryRoot };
            if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
                return snapshot;

            snapshot.BranchName = GetCurrentBranch(repositoryRoot);
            snapshot.HeadHash = GetHeadHash(repositoryRoot);
            snapshot.Remote = GetRemoteInfo(repositoryRoot);
            if (snapshot.Remote.IsConnected)
            {
                snapshot.OutgoingCommitCount = GetUniqueCommitCount(repositoryRoot, "--branches", "--remotes=origin");
                snapshot.IncomingCommitCount = GetUniqueCommitCount(repositoryRoot, "--remotes=origin", "--branches");
            }
            snapshot.Changes.AddRange(GetChanges(repositoryRoot));
            snapshot.Branches.AddRange(GetBranches(repositoryRoot, snapshot.BranchName));
            snapshot.Commits.AddRange(GetCommits(repositoryRoot, snapshot.BranchName, snapshot.HeadHash));

            return snapshot;
        }

        internal List<GitChangeInfo> GetWorkingTreeChanges(string repositoryRoot)
        {
            return string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot)
                ? new List<GitChangeInfo>()
                : GetChanges(repositoryRoot);
        }

        internal string GetWorkingTreePatchForAi(string repositoryRoot, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
                return "";

            cancellationToken.ThrowIfCancellationRequested();
            var changes = GetChanges(repositoryRoot, cancellationToken);
            if (changes.Count == 0)
                return "";

            var hasHead = _runner.Run(repositoryRoot, cancellationToken, "rev-parse", "--verify", "HEAD").Success;
            var patch = new StringBuilder();
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filePatch = change.IsUntracked
                    ? BuildUntrackedFilePatch(change)
                    : GetTrackedFilePatch(repositoryRoot, change, hasHead, cancellationToken);

                if (string.IsNullOrWhiteSpace(filePatch))
                    continue;

                if (!AppendAiPatchPart(patch, filePatch))
                    break;
            }

            return patch.ToString();
        }

        internal GitActionResult CommitAll(string repositoryRoot, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return GitActionResult.Fail("Commit message is empty.");

            var add = _runner.Run(repositoryRoot, "add", "-A");
            if (!add.Success)
                return GitActionResult.Fail(add.Message);

            var commit = _runner.Run(repositoryRoot, "commit", "-m", message);
            if (!commit.Success)
                return GitActionResult.Fail(commit.Message);

            return GitActionResult.Ok(commit.Output.Trim());
        }

        internal GitActionResult SwitchBranch(string repositoryRoot, string branchName)
        {
            var clean = EnsureWorkingTreeClean(repositoryRoot, "Branch switch is available only when the working tree is clean.");
            if (!clean.Success)
                return clean;

            var result = _runner.Run(repositoryRoot, "switch", branchName);
            return result.Success
                ? GitActionResult.Ok("Branch selected.")
                : GitActionResult.Fail(result.Message);
        }

        internal GitActionResult CreateBranch(string repositoryRoot, string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName))
                return GitActionResult.Fail("Branch name is required.");

            var normalizedName = branchName.Trim();
            var check = _runner.Run(repositoryRoot, "check-ref-format", "--branch", normalizedName);
            if (!check.Success)
                return GitActionResult.Fail(check.Message);

            var result = _runner.Run(repositoryRoot, "switch", "-c", normalizedName);
            return result.Success
                ? GitActionResult.Ok("Branch created.")
                : GitActionResult.Fail(result.Message);
        }

        internal GitActionResult ConnectRemote(string repositoryRoot, string remoteUrl, string login, string secret, GitRemoteAuthMode authMode)
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
                return GitActionResult.Fail("Git repository folder was not found.");

            Uri remoteUri;
            var urlValidation = ValidateHttpsRemoteUrl(remoteUrl, out remoteUri);
            if (!urlValidation.Success)
                return urlValidation;

            if (string.IsNullOrWhiteSpace(login))
                return GitActionResult.Fail("Remote login is required.");
            if (string.IsNullOrWhiteSpace(secret))
                return GitActionResult.Fail(authMode == GitRemoteAuthMode.LoginToken ? "Remote token is required." : "Remote password is required.");

            var sanitizedUrl = SanitizeRemoteUrl(remoteUri);
            var existingRemote = GetRemoteInfo(repositoryRoot);
            var hadOrigin = existingRemote.IsConnected;
            if (hadOrigin && !RemoteUrlsEqual(existingRemote.Url, sanitizedUrl))
                return GitActionResult.Fail("Remote origin already points to another URL. Disconnect it first.");

            var usePath = _runner.Run(repositoryRoot, "config", "credential.useHttpPath", "true");
            if (!usePath.Success)
                return GitActionResult.Fail(usePath.Message);

            var credentials = ApproveCredentials(repositoryRoot, remoteUri, login.Trim(), secret);
            if (!credentials.Success)
                return GitActionResult.Fail(credentials.Message);

            GitCommandResult remoteResult;
            if (hadOrigin)
                remoteResult = _runner.Run(repositoryRoot, "remote", "set-url", "origin", sanitizedUrl);
            else
                remoteResult = _runner.Run(repositoryRoot, "remote", "add", "origin", sanitizedUrl);

            if (!remoteResult.Success)
                return GitActionResult.Fail(remoteResult.Message);

            var fetch = FetchOrigin(repositoryRoot);
            if (!fetch.Success)
            {
                if (!hadOrigin)
                {
                    RejectCredentials(repositoryRoot, remoteUri, login.Trim(), secret);
                    RemoveOrigin(repositoryRoot);
                }
                return GitActionResult.Fail(fetch.Message);
            }

            var history = ValidateConnectedHistory(repositoryRoot);
            if (!history.Success)
            {
                if (!hadOrigin)
                {
                    RejectCredentials(repositoryRoot, remoteUri, login.Trim(), secret);
                    RemoveOrigin(repositoryRoot);
                }
                return history;
            }

            return GitActionResult.Ok("Remote connected.");
        }

        internal GitActionResult DisconnectRemote(string repositoryRoot)
        {
            var remote = GetRemoteInfo(repositoryRoot);
            if (!remote.IsConnected)
                return GitActionResult.Ok("Remote disconnected.");

            var result = RemoveOrigin(repositoryRoot);
            return result.Success
                ? GitActionResult.Ok("Remote disconnected.")
                : GitActionResult.Fail(result.Message);
        }

        internal GitActionResult RefreshRemote(string repositoryRoot)
        {
            var remote = GetRemoteInfo(repositoryRoot);
            return remote.IsConnected
                ? FetchOrigin(repositoryRoot)
                : GitActionResult.Ok("No remote connected.");
        }

        internal GitActionResult PushAllBranches(string repositoryRoot)
        {
            if (!GetRemoteInfo(repositoryRoot).IsConnected)
                return GitActionResult.Fail("Connect a remote repository first.");

            var fetch = FetchOrigin(repositoryRoot);
            if (!fetch.Success)
                return fetch;

            var localBranches = GetLocalBranchRefs(repositoryRoot);
            if (localBranches.Count == 0)
                return GitActionResult.Fail("There are no local branches to push.");

            var divergence = FindNonFastForwardPush(repositoryRoot, localBranches, GetRemoteBranchRefs(repositoryRoot));
            if (!divergence.Success)
                return divergence;

            var push = _runner.Run(repositoryRoot, NonInteractiveGitEnvironment(), "push", "-u", "origin", "--all");
            return push.Success
                ? GitActionResult.Ok("Remote push completed.")
                : GitActionResult.Fail(push.Message);
        }

        internal GitActionResult PullAllBranches(string repositoryRoot)
        {
            if (!GetRemoteInfo(repositoryRoot).IsConnected)
                return GitActionResult.Fail("Connect a remote repository first.");

            var clean = EnsureWorkingTreeClean(repositoryRoot, "Pull is available only when the working tree is clean.");
            if (!clean.Success)
                return clean;

            var fetch = FetchOrigin(repositoryRoot);
            if (!fetch.Success)
                return fetch;

            var localBranches = GetLocalBranchRefs(repositoryRoot);
            var remoteBranches = GetRemoteBranchRefs(repositoryRoot);
            var currentBranch = GetCurrentBranch(repositoryRoot);

            foreach (var remote in remoteBranches)
            {
                var local = localBranches.FirstOrDefault(item => string.Equals(item.Name, remote.Name, StringComparison.Ordinal));
                if (local == null)
                {
                    var track = _runner.Run(repositoryRoot, "branch", "--track", remote.Name, "origin/" + remote.Name);
                    if (!track.Success)
                        return GitActionResult.Fail(track.Message);

                    localBranches.Add(new GitBranchRef { Name = remote.Name, Hash = remote.Hash });
                    continue;
                }

                if (string.Equals(local.Hash, remote.Hash, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsAncestor(repositoryRoot, remote.Hash, local.Hash))
                    continue;

                if (!IsAncestor(repositoryRoot, local.Hash, remote.Hash))
                    return GitActionResult.Fail("Remote branch '" + remote.Name + "' has diverged. Resolve it manually before pulling.");

                GitCommandResult update;
                if (string.Equals(currentBranch, local.Name, StringComparison.Ordinal))
                    update = _runner.Run(repositoryRoot, "merge", "--ff-only", "origin/" + local.Name);
                else
                    update = _runner.Run(repositoryRoot, "branch", "-f", local.Name, "origin/" + local.Name);

                if (!update.Success)
                    return GitActionResult.Fail(update.Message);
            }

            return GitActionResult.Ok("Remote pull completed.");
        }

        internal GitMergeResult SoftMerge(string repositoryRoot, string sourceBranch, string targetBranch)
        {
            var validation = ValidateMergeRequest(repositoryRoot, sourceBranch, targetBranch);
            if (!validation.Success)
                return GitMergeResult.MergeFail(validation.Message);

            var clean = EnsureWorkingTreeClean(repositoryRoot, "Merge is available only when the working tree is clean.");
            if (!clean.Success)
                return GitMergeResult.MergeFail(clean.Message);

            var currentBranch = GetCurrentBranch(repositoryRoot);
            if (!string.Equals(currentBranch, targetBranch, StringComparison.Ordinal))
            {
                var branchSwitch = _runner.Run(repositoryRoot, "switch", targetBranch);
                if (!branchSwitch.Success)
                    return GitMergeResult.MergeFail(branchSwitch.Message);
            }

            var merge = _runner.Run(repositoryRoot, "merge", "--no-ff", "--no-commit", sourceBranch);
            if (merge.Success)
                return GitMergeResult.MergeOk("Merge prepared. Review and commit.");

            var conflicts = GetMergeConflicts(repositoryRoot);
            if (conflicts.Count > 0)
                return GitMergeResult.Conflict(merge.Message, conflicts);

            return GitMergeResult.MergeFail(merge.Message);
        }

        internal GitActionResult AbortMerge(string repositoryRoot)
        {
            if (!IsMergeInProgress(repositoryRoot))
                return GitActionResult.Ok("Merge cancelled.");

            var result = _runner.Run(repositoryRoot, "merge", "--abort");
            return result.Success
                ? GitActionResult.Ok("Merge cancelled.")
                : GitActionResult.Fail(result.Message);
        }

        internal GitMergeResult ResolveMergeConflicts(string repositoryRoot, bool takeSourceBranch)
        {
            if (!IsMergeInProgress(repositoryRoot))
                return GitMergeResult.MergeFail("There is no merge in progress.");

            var conflicts = GetMergeConflicts(repositoryRoot);
            if (conflicts.Count == 0)
                return GitMergeResult.MergeOk("There are no merge conflicts.");

            var stage = takeSourceBranch ? 3 : 2;
            var side = takeSourceBranch ? "--theirs" : "--ours";
            foreach (var conflict in conflicts)
            {
                var path = NormalizeGitPath(conflict.RelativePath);
                var sideExists = ConflictStageExists(repositoryRoot, path, stage);
                GitCommandResult checkout;

                if (sideExists)
                    checkout = _runner.Run(repositoryRoot, "checkout", side, "--", path);
                else
                    checkout = _runner.Run(repositoryRoot, "rm", "-f", "--", path);

                if (!checkout.Success)
                    return GitMergeResult.MergeFail(checkout.Message);

                var add = _runner.Run(repositoryRoot, "add", "-A", "--", path);
                if (!add.Success)
                    return GitMergeResult.MergeFail(add.Message);
            }

            return GitMergeResult.MergeOk("Merge conflicts resolved. Review and commit.");
        }

        internal GitActionResult RestoreToCommitSnapshot(string repositoryRoot, string hash)
        {
            var abortRevert = AbortInProgressRevert(repositoryRoot);
            if (!abortRevert.Success)
                return abortRevert;

            var clean = EnsureWorkingTreeClean(repositoryRoot, "Restore is available only when the working tree is clean.");
            if (!clean.Success)
                return clean;

            var result = _runner.Run(repositoryRoot, "restore", "--source", hash, "--staged", "--worktree", ":/");
            return result.Success
                ? GitActionResult.Ok("Files restored to the selected version. Review and commit the rollback.")
                : GitActionResult.Fail(result.Message);
        }

        internal GitDiffDocumentVM GetCommitDiff(string repositoryRoot, GitCommitVM commit)
        {
            if (commit == null || string.IsNullOrWhiteSpace(commit.Hash))
                return null;

            var result = _runner.Run(repositoryRoot, "-c", "core.quotepath=false", "show", "--format=", "--patch", "--find-renames", "--find-copies", commit.Hash);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return null;

            return ParseDiff(commit, result.Output);
        }

        internal static bool TryGetFileText(string repositoryRoot, string relativePath, out string text)
        {
            text = "";
            if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(relativePath))
                return false;

            var result = new GitCommandRunner().Run(repositoryRoot, "show", "HEAD:" + NormalizeGitPath(relativePath));
            if (!result.Success)
                return false;

            text = result.Output;
            return true;
        }

        private GitRemoteInfo GetRemoteInfo(string repositoryRoot)
        {
            var info = new GitRemoteInfo { Name = "origin", Url = "", DisplayUrl = "" };
            if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
                return info;

            var result = _runner.Run(repositoryRoot, "remote", "get-url", "origin");
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return info;

            info.Url = StripRemoteUrlCredentials(result.Output.Trim());
            info.DisplayUrl = FormatRemoteDisplayUrl(info.Url);
            return info;
        }

        private GitActionResult ValidateHttpsRemoteUrl(string remoteUrl, out Uri remoteUri)
        {
            remoteUri = null;
            if (string.IsNullOrWhiteSpace(remoteUrl))
                return GitActionResult.Fail("Remote URL is required.");

            if (!Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out remoteUri)
                || !string.Equals(remoteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(remoteUri.Host))
                return GitActionResult.Fail("Only HTTPS GitHub/GitLab remote URLs are supported.");

            return GitActionResult.Ok("");
        }

        private GitActionResult ApproveCredentials(string repositoryRoot, Uri remoteUri, string login, string secret)
        {
            var input = BuildCredentialInput(remoteUri, login, secret);
            var result = _runner.RunWithInput(repositoryRoot, input, NonInteractiveGitEnvironment(), "credential", "approve");
            return result.Success
                ? GitActionResult.Ok("")
                : GitActionResult.Fail(result.Message);
        }

        private GitActionResult RejectCredentials(string repositoryRoot, Uri remoteUri, string login, string secret)
        {
            var input = BuildCredentialInput(remoteUri, login, secret);
            var result = _runner.RunWithInput(repositoryRoot, input, NonInteractiveGitEnvironment(), "credential", "reject");
            return result.Success
                ? GitActionResult.Ok("")
                : GitActionResult.Fail(result.Message);
        }

        private GitActionResult FetchOrigin(string repositoryRoot)
        {
            var result = _runner.Run(repositoryRoot, NonInteractiveGitEnvironment(), "fetch", "--prune", "origin");
            return result.Success
                ? GitActionResult.Ok("Remote data refreshed.")
                : GitActionResult.Fail(result.Message);
        }

        private GitCommandResult RemoveOrigin(string repositoryRoot)
        {
            return _runner.Run(repositoryRoot, "remote", "remove", "origin");
        }

        private GitActionResult ValidateConnectedHistory(string repositoryRoot)
        {
            var localBranches = GetLocalBranchRefs(repositoryRoot);
            var remoteBranches = GetRemoteBranchRefs(repositoryRoot);
            if (localBranches.Count == 0 || remoteBranches.Count == 0)
                return GitActionResult.Ok("");

            foreach (var local in localBranches)
            {
                foreach (var remote in remoteBranches)
                {
                    if (string.Equals(local.Hash, remote.Hash, StringComparison.OrdinalIgnoreCase)
                        || MergeBaseExists(repositoryRoot, local.Hash, remote.Hash))
                        return GitActionResult.Ok("");
                }
            }

            return GitActionResult.Fail("Remote repository contains unrelated history. Connect an empty or related repository.");
        }

        private int GetUniqueCommitCount(string repositoryRoot, string includeRefs, string excludeRefs)
        {
            var result = _runner.Run(repositoryRoot, "rev-list", "--count", includeRefs, "--not", excludeRefs);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return 0;

            int count;
            return int.TryParse(result.Output.Trim(), out count) ? count : 0;
        }

        private GitActionResult FindNonFastForwardPush(string repositoryRoot, IList<GitBranchRef> localBranches, IList<GitBranchRef> remoteBranches)
        {
            foreach (var local in localBranches)
            {
                var remote = remoteBranches.FirstOrDefault(item => string.Equals(item.Name, local.Name, StringComparison.Ordinal));
                if (remote == null || string.Equals(local.Hash, remote.Hash, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsAncestor(repositoryRoot, remote.Hash, local.Hash))
                    return GitActionResult.Fail("Remote branch '" + local.Name + "' is ahead or diverged. Pull/resolve it before pushing.");
            }

            return GitActionResult.Ok("");
        }

        private List<GitBranchRef> GetLocalBranchRefs(string repositoryRoot)
        {
            var branches = new List<GitBranchRef>();
            var result = _runner.Run(repositoryRoot, "branch", "--format=%(refname:short)%09%(objectname)");
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return branches;

            using (var reader = new StringReader(result.Output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var branch = ParseBranchRefLine(line);
                    if (branch != null)
                        branches.Add(branch);
                }
            }

            return branches;
        }

        private List<GitBranchRef> GetRemoteBranchRefs(string repositoryRoot)
        {
            var branches = new List<GitBranchRef>();
            var result = _runner.Run(repositoryRoot, "for-each-ref", "refs/remotes/origin", "--format=%(refname:short)%09%(objectname)");
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return branches;

            using (var reader = new StringReader(result.Output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var branch = ParseBranchRefLine(line);
                    if (branch == null || string.Equals(branch.Name, "origin/HEAD", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (branch.Name.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
                        branch.Name = branch.Name.Substring("origin/".Length);

                    if (!string.IsNullOrWhiteSpace(branch.Name))
                        branches.Add(branch);
                }
            }

            return branches;
        }

        private static GitBranchRef ParseBranchRefLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            var parts = line.Split(new[] { '\t' }, 2);
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                return null;

            return new GitBranchRef { Name = parts[0].Trim(), Hash = parts[1].Trim() };
        }

        private bool IsAncestor(string repositoryRoot, string ancestor, string descendant)
        {
            if (string.IsNullOrWhiteSpace(ancestor) || string.IsNullOrWhiteSpace(descendant))
                return false;

            var result = _runner.Run(repositoryRoot, "merge-base", "--is-ancestor", ancestor, descendant);
            return result.ExitCode == 0;
        }

        private bool MergeBaseExists(string repositoryRoot, string leftHash, string rightHash)
        {
            var result = _runner.Run(repositoryRoot, "merge-base", leftHash, rightHash);
            return result.Success && !string.IsNullOrWhiteSpace(result.Output);
        }

        private static Dictionary<string, string> NonInteractiveGitEnvironment()
        {
            var environment = new Dictionary<string, string>
            {
                { "GIT_TERMINAL_PROMPT", "0" },
                { "GCM_INTERACTIVE", "never" }
            };

            var askPass = EnsureAskPassScript();
            if (!string.IsNullOrWhiteSpace(askPass))
                environment["GIT_ASKPASS"] = askPass;

            return environment;
        }

        private static string EnsureAskPassScript()
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "clev3r-git-askpass.cmd");
                if (!File.Exists(path))
                    File.WriteAllText(path, "@echo off\r\nexit /b 1\r\n", Encoding.ASCII);
                return path;
            }
            catch
            {
                return "";
            }
        }

        private static string BuildCredentialInput(Uri remoteUri, string login, string secret)
        {
            var builder = new StringBuilder();
            builder.AppendLine("protocol=" + remoteUri.Scheme);
            builder.AppendLine("host=" + GetCredentialHost(remoteUri));
            var path = remoteUri.AbsolutePath.Trim('/');
            if (!string.IsNullOrWhiteSpace(path))
                builder.AppendLine("path=" + path);
            builder.AppendLine("username=" + login);
            builder.AppendLine("password=" + secret);
            builder.AppendLine();
            return builder.ToString();
        }

        private static string GetCredentialHost(Uri remoteUri)
        {
            return remoteUri.IsDefaultPort
                ? remoteUri.Host
                : remoteUri.Host + ":" + remoteUri.Port;
        }

        private static string SanitizeRemoteUrl(Uri remoteUri)
        {
            var builder = new UriBuilder(remoteUri)
            {
                UserName = "",
                Password = ""
            };
            return builder.Uri.AbsoluteUri;
        }

        private static string StripRemoteUrlCredentials(string remoteUrl)
        {
            Uri uri;
            return Uri.TryCreate(remoteUrl, UriKind.Absolute, out uri) && !string.IsNullOrEmpty(uri.UserInfo)
                ? SanitizeRemoteUrl(uri)
                : remoteUrl;
        }

        private static bool RemoteUrlsEqual(string left, string right)
        {
            Uri leftUri;
            Uri rightUri;
            if (Uri.TryCreate(left, UriKind.Absolute, out leftUri) && Uri.TryCreate(right, UriKind.Absolute, out rightUri))
                return string.Equals(SanitizeRemoteUrl(leftUri).TrimEnd('/'), SanitizeRemoteUrl(rightUri).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

            return string.Equals((left ?? "").TrimEnd('/'), (right ?? "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatRemoteDisplayUrl(string remoteUrl)
        {
            Uri uri;
            if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out uri))
                return remoteUrl ?? "";

            var path = uri.AbsolutePath.Trim('/');
            return string.IsNullOrWhiteSpace(path)
                ? uri.Host
                : uri.Host + "/" + path;
        }

        private GitActionResult ValidateMergeRequest(string repositoryRoot, string sourceBranch, string targetBranch)
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
                return GitActionResult.Fail("Git repository folder was not found.");

            if (string.IsNullOrWhiteSpace(sourceBranch) || string.IsNullOrWhiteSpace(targetBranch))
                return GitActionResult.Fail("Select two branches to merge.");

            if (string.Equals(sourceBranch.Trim(), targetBranch.Trim(), StringComparison.Ordinal))
                return GitActionResult.Fail("Select two different branches to merge.");

            if (IsMergeInProgress(repositoryRoot))
                return GitActionResult.Fail("Finish or cancel the current merge before starting a new one.");

            return GitActionResult.Ok("");
        }

        private GitActionResult EnsureWorkingTreeClean(string repositoryRoot, string dirtyMessage)
        {
            var status = GetStatusPorcelain(repositoryRoot);
            if (!status.Success)
                return GitActionResult.Fail(status.Message);

            return string.IsNullOrWhiteSpace(status.Output)
                ? GitActionResult.Ok("")
                : GitActionResult.Fail(dirtyMessage);
        }

        private bool IsMergeInProgress(string repositoryRoot)
        {
            var gitPath = _runner.Run(repositoryRoot, "rev-parse", "--git-path", "MERGE_HEAD");
            if (!gitPath.Success || string.IsNullOrWhiteSpace(gitPath.Output))
                return false;

            var mergeHead = gitPath.Output.Trim();
            if (!Path.IsPathRooted(mergeHead))
                mergeHead = Path.Combine(repositoryRoot, mergeHead);

            return File.Exists(mergeHead);
        }

        private List<GitMergeConflictInfo> GetMergeConflicts(string repositoryRoot)
        {
            var conflicts = new List<GitMergeConflictInfo>();
            var result = _runner.Run(repositoryRoot, "-c", "core.quotepath=false", "diff", "--name-only", "--diff-filter=U");
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return conflicts;

            using (var reader = new StringReader(result.Output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var relativePath = UnquoteGitPath(line.Trim());
                    var conflict = new GitMergeConflictInfo { RelativePath = relativePath };
                    AddConflictMarkerLines(repositoryRoot, conflict);
                    conflicts.Add(conflict);
                }
            }

            return conflicts;
        }

        private static void AddConflictMarkerLines(string repositoryRoot, GitMergeConflictInfo conflict)
        {
            if (conflict == null || string.IsNullOrWhiteSpace(conflict.RelativePath))
                return;

            var fullPath = Path.Combine(repositoryRoot, conflict.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                return;

            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(fullPath, Encoding.UTF8))
                {
                    lineNumber++;
                    if (line.StartsWith("<<<<<<<", StringComparison.Ordinal)
                        || line.StartsWith("=======", StringComparison.Ordinal)
                        || line.StartsWith(">>>>>>>", StringComparison.Ordinal))
                        conflict.Lines.Add(lineNumber);
                }
            }
            catch
            {
            }
        }

        private bool ConflictStageExists(string repositoryRoot, string relativePath, int stage)
        {
            var result = _runner.Run(repositoryRoot, "ls-files", "-u", "--", relativePath);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return false;

            var marker = " " + stage + "\t";
            using (var reader = new StringReader(result.Output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.IndexOf(marker, StringComparison.Ordinal) >= 0)
                        return true;
                }
            }

            return false;
        }

        private GitActionResult AbortInProgressRevert(string repositoryRoot)
        {
            var gitPath = _runner.Run(repositoryRoot, "rev-parse", "--git-path", "REVERT_HEAD");
            if (!gitPath.Success || string.IsNullOrWhiteSpace(gitPath.Output))
                return GitActionResult.Ok("");

            var revertHead = gitPath.Output.Trim();
            if (!Path.IsPathRooted(revertHead))
                revertHead = Path.Combine(repositoryRoot, revertHead);

            if (!File.Exists(revertHead))
                return GitActionResult.Ok("");

            var abort = _runner.Run(repositoryRoot, "revert", "--abort");
            return abort.Success
                ? GitActionResult.Ok("")
                : GitActionResult.Fail(abort.Message);
        }

        private string GetCurrentBranch(string repositoryRoot)
        {
            var branch = _runner.Run(repositoryRoot, "branch", "--show-current");
            if (branch.Success && !string.IsNullOrWhiteSpace(branch.Output))
                return branch.Output.Trim();

            var symbolic = _runner.Run(repositoryRoot, "symbolic-ref", "--short", "HEAD");
            if (symbolic.Success && !string.IsNullOrWhiteSpace(symbolic.Output))
                return symbolic.Output.Trim();

            var detached = _runner.Run(repositoryRoot, "rev-parse", "--short", "HEAD");
            return detached.Success && !string.IsNullOrWhiteSpace(detached.Output)
                ? "HEAD " + detached.Output.Trim()
                : "";
        }

        private string GetHeadHash(string repositoryRoot)
        {
            var result = _runner.Run(repositoryRoot, "rev-parse", "HEAD");
            return result.Success && !string.IsNullOrWhiteSpace(result.Output)
                ? result.Output.Trim()
                : "";
        }

        private List<GitChangeInfo> GetChanges(string repositoryRoot)
        {
            return GetChanges(repositoryRoot, CancellationToken.None);
        }

        private List<GitChangeInfo> GetChanges(string repositoryRoot, CancellationToken cancellationToken)
        {
            var changes = new List<GitChangeInfo>();
            var result = cancellationToken.CanBeCanceled
                ? _runner.Run(repositoryRoot, cancellationToken, "-c", "core.quotepath=false", "status", "--porcelain=v1", "--untracked-files=all")
                : GetStatusPorcelain(repositoryRoot);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return changes;

            using (var reader = new StringReader(result.Output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < 3)
                        continue;

                    var indexStatus = line[0];
                    var workTreeStatus = line[1];
                    var path = UnquoteGitPath(line.Substring(3));
                    var oldPath = "";

                    var renameIndex = path.IndexOf(" -> ", StringComparison.Ordinal);
                    if (renameIndex >= 0)
                    {
                        oldPath = path.Substring(0, renameIndex);
                        path = path.Substring(renameIndex + 4);
                    }

                    var status = GetDisplayStatus(indexStatus, workTreeStatus);
                    changes.Add(new GitChangeInfo
                    {
                        Status = status,
                        StatusText = GetStatusText(status),
                        RelativePath = path,
                        OldPath = oldPath,
                        FullPath = Path.Combine(repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar)),
                        IsDeleted = status == "D",
                        IsUntracked = indexStatus == '?' && workTreeStatus == '?'
                    });
                }
            }

            return changes;
        }

        private List<GitBranchInfo> GetBranches(string repositoryRoot, string currentBranch)
        {
            var branches = new List<GitBranchInfo>();
            var result = _runner.Run(repositoryRoot, "branch", "--format=%(HEAD)%09%(refname:short)");
            if (result.Success)
            {
                using (var reader = new StringReader(result.Output))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        var parts = line.Split(new[] { '\t' }, 2);
                        var active = parts.Length > 0 && parts[0].Trim() == "*";
                        var name = parts.Length > 1 ? parts[1].Trim() : line.Trim().TrimStart('*').Trim();
                        if (name.Length == 0)
                            continue;

                        branches.Add(new GitBranchInfo { Name = name, IsActive = active || name == currentBranch });
                    }
                }
            }

            if (branches.Count == 0 && !string.IsNullOrWhiteSpace(currentBranch))
                branches.Add(new GitBranchInfo { Name = currentBranch, IsActive = true });

            return branches;
        }

        private List<GitCommitVM> GetCommits(string repositoryRoot, string currentBranch, string headHash)
        {
            var commits = new List<GitCommitVM>();
            var format = "%x1e%H%x1f%P%x1f%an%x1f%ar%x1f%D%x1f%s";
            var result = _runner.Run(
                repositoryRoot,
                "-c",
                "core.quotepath=false",
                "log",
                "--max-count=" + MaxCommitCount,
                "--date=relative",
                "--all",
                "--date-order",
                "--decorate=short",
                "--find-renames",
                "--name-status",
                "--shortstat",
                "--pretty=format:" + format);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return commits;

            var records = result.Output.Split(new[] { '\x1e' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawRecord in records)
            {
                using (var reader = new StringReader(rawRecord.Trim('\r', '\n')))
                {
                    var header = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(header))
                        continue;

                    var parts = header.Split(new[] { '\x1f' }, 6);
                    if (parts.Length < 6)
                        continue;

                    var commit = new GitCommitVM
                    {
                        Hash = parts[0],
                        ShortHash = parts[0].Length > 7 ? parts[0].Substring(0, 7) : parts[0],
                        Author = parts[2],
                        TimeText = parts[3],
                        Message = parts[5],
                        Summary = parts[5],
                        IsHead = !string.IsNullOrWhiteSpace(headHash) && string.Equals(parts[0], headHash, StringComparison.OrdinalIgnoreCase)
                    };

                    foreach (var parent in parts[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        commit.ParentHashes.Add(parent);

                    AddDecorations(commit, parts[4], currentBranch);
                    ParseCommitDetailLines(repositoryRoot, commit, reader);

                    if (string.IsNullOrWhiteSpace(commit.StatsText))
                        commit.StatsText = commit.Files.Count == 1 ? "1 file changed" : commit.Files.Count + " files changed";

                    commits.Add(commit);
                }
            }

            return commits;
        }

        private void ParseCommitDetailLines(string repositoryRoot, GitCommitVM commit, StringReader reader)
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var trimmed = line.Trim();
                if (IsShortStatLine(trimmed))
                {
                    commit.StatsText = trimmed;
                    continue;
                }

                var fileParts = line.Split('\t');
                if (fileParts.Length < 2)
                    continue;

                var status = fileParts[0].Length > 0 ? fileParts[0].Substring(0, 1) : "M";
                var path = fileParts[fileParts.Length - 1];
                commit.Files.Add(CreateChangeItem(
                    repositoryRoot,
                    path,
                    status,
                    GetStatusText(status),
                    status == "D",
                    fileParts.Length > 2 ? fileParts[1] : ""));
            }
        }

        private static bool IsShortStatLine(string line)
        {
            return line.IndexOf(" file changed", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" files changed", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" insertion", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" deletion", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AddDecorations(GitCommitVM commit, string decorations, string currentBranch)
        {
            if (string.IsNullOrWhiteSpace(decorations))
                return;

            foreach (var item in decorations.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var decoration = item.Trim();
                var arrowIndex = decoration.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrowIndex >= 0)
                    decoration = decoration.Substring(arrowIndex + 4).Trim();
                if (decoration == "HEAD")
                    continue;
                if (decoration.Equals("origin/HEAD", StringComparison.OrdinalIgnoreCase)
                    || decoration.Equals("upstream/HEAD", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (decoration.StartsWith("tag: ", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (decoration.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
                    || decoration.StartsWith("upstream/", StringComparison.OrdinalIgnoreCase))
                    commit.RemoteRefs.Add(decoration);
                else
                    commit.LocalRefs.Add(decoration);
            }

            if (commit.LocalRefs.Count == 0 && !string.IsNullOrWhiteSpace(currentBranch) && decorations.Contains("HEAD -> " + currentBranch))
                commit.LocalRefs.Add(currentBranch);
        }

        private List<GitChangeItemVM> GetCommitFiles(string repositoryRoot, string hash)
        {
            var files = new List<GitChangeItemVM>();
            var result = _runner.Run(repositoryRoot, "-c", "core.quotepath=false", "show", "--format=", "--name-status", "--find-renames", hash);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return files;

            using (var reader = new StringReader(result.Output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var parts = line.Split('\t');
                    if (parts.Length < 2)
                        continue;

                    var status = parts[0].Length > 0 ? parts[0].Substring(0, 1) : "M";
                    var path = parts[parts.Length - 1];
                    files.Add(CreateChangeItem(repositoryRoot, path, status, GetStatusText(status), status == "D", parts.Length > 2 ? parts[1] : ""));
                }
            }

            return files;
        }

        private string GetShortStat(string repositoryRoot, string hash, int fileCount)
        {
            var result = _runner.Run(repositoryRoot, "show", "--shortstat", "--format=", hash);
            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
                return result.Output.Trim();

            return fileCount == 1 ? "1 file changed" : fileCount + " files changed";
        }

        internal static GitChangeItemVM CreateChangeItem(string repositoryRoot, string relativePath, string status, string statusText, bool isDeleted, string oldPath)
        {
            var fileName = Path.GetFileName(relativePath);
            var folder = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));
            var item = new GitChangeItemVM
            {
                FileName = string.IsNullOrEmpty(fileName) ? relativePath : fileName,
                Folder = folder ?? "",
                Status = status,
                StatusText = statusText,
                StatusBrush = CreateStatusBrush(status),
                RelativePath = relativePath,
                OldPath = oldPath,
                RepositoryRoot = repositoryRoot,
                FullPath = Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                IsDeleted = isDeleted
            };
            item.ApplyFixedFileType();
            return item;
        }

        private static GitDiffDocumentVM ParseDiff(GitCommitVM commit, string patch)
        {
            var document = new GitDiffDocumentVM(commit.ShortHash, "Diff " + commit.ShortHash + ".diff");
            GitDiffFileVM currentFile = null;
            var firstFile = true;
            var previousOldEnd = 0;
            var previousNewEnd = 0;

            using (var reader = new StringReader(patch))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                    {
                        var path = ParseDiffPath(line);
                        currentFile = new GitDiffFileVM(path);
                        document.Files.Add(currentFile);
                        if (!firstFile)
                            currentFile.Lines.Add(new GitDiffLineVM("", GitDiffLineKind.Metadata, ""));

                        currentFile.Lines.Add(new GitDiffLineVM(line, GitDiffLineKind.FileHeader, "===== " + path + " ====="));
                        firstFile = false;
                        previousOldEnd = 0;
                        previousNewEnd = 0;
                        continue;
                    }

                    if (currentFile == null)
                    {
                        currentFile = new GitDiffFileVM(commit.ShortHash);
                        document.Files.Add(currentFile);
                    }

                    if (TryParseHunkRange(line, out var oldStart, out var oldCount, out var newStart, out var newCount))
                    {
                        var hiddenCount = GetHiddenUnchangedCount(previousOldEnd, previousNewEnd, oldStart, newStart);
                        if (hiddenCount > 0)
                        {
                            currentFile.Lines.Add(new GitDiffLineVM(
                                "",
                                GitDiffLineKind.Hidden,
                                "... " + hiddenCount + " unchanged lines hidden ..."));
                        }

                        previousOldEnd = oldStart + Math.Max(oldCount, 1) - 1;
                        previousNewEnd = newStart + Math.Max(newCount, 1) - 1;
                    }

                    currentFile.Lines.Add(CreateDiffLine(line));
                }
            }

            return document;
        }

        private static int GetHiddenUnchangedCount(int previousOldEnd, int previousNewEnd, int oldStart, int newStart)
        {
            if (oldStart <= 0 && newStart <= 0)
                return 0;

            if (previousOldEnd == 0 && previousNewEnd == 0)
                return Math.Max(oldStart, newStart) - 1;

            var oldGap = oldStart <= 0 ? 0 : oldStart - previousOldEnd - 1;
            var newGap = newStart <= 0 ? 0 : newStart - previousNewEnd - 1;
            return Math.Max(oldGap, newGap);
        }

        private static bool TryParseHunkRange(string line, out int oldStart, out int oldCount, out int newStart, out int newCount)
        {
            oldStart = 0;
            oldCount = 0;
            newStart = 0;
            newCount = 0;

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("@@", StringComparison.Ordinal))
                return false;

            var match = Regex.Match(line, @"^@@ -(?<oldStart>\d+)(,(?<oldCount>\d+))? \+(?<newStart>\d+)(,(?<newCount>\d+))? @@");
            if (!match.Success)
                return false;

            oldStart = int.Parse(match.Groups["oldStart"].Value);
            oldCount = match.Groups["oldCount"].Success ? int.Parse(match.Groups["oldCount"].Value) : 1;
            newStart = int.Parse(match.Groups["newStart"].Value);
            newCount = match.Groups["newCount"].Success ? int.Parse(match.Groups["newCount"].Value) : 1;
            return true;
        }

        private static string ParseDiffPath(string diffGitLine)
        {
            var marker = " b/";
            var index = diffGitLine.IndexOf(marker, StringComparison.Ordinal);
            return index >= 0 ? diffGitLine.Substring(index + marker.Length) : diffGitLine;
        }

        private static GitDiffLineVM CreateDiffLine(string line)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal))
                return new GitDiffLineVM(line, GitDiffLineKind.Metadata, "old: " + CleanDiffPath(line.Substring(4)));
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
                return new GitDiffLineVM(line, GitDiffLineKind.Metadata, "new: " + CleanDiffPath(line.Substring(4)));
            if (line.StartsWith("@@", StringComparison.Ordinal))
                return new GitDiffLineVM(line, GitDiffLineKind.Hunk, line);
            if (line.StartsWith("\\ No newline", StringComparison.Ordinal))
                return new GitDiffLineVM(line, GitDiffLineKind.Metadata, "note: no newline at end of file");

            return new GitDiffLineVM(line, GetDiffLineKind(line));
        }

        private static string CleanDiffPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            path = path.Trim();
            if (path == "/dev/null")
                return path;
            if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
                return path.Substring(2);

            return path;
        }

        private static GitDiffLineKind GetDiffLineKind(string line)
        {
            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
                return GitDiffLineKind.Added;
            if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal))
                return GitDiffLineKind.Deleted;
            if (line.StartsWith("@@", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal)
                || line.StartsWith("+++", StringComparison.Ordinal)
                || line.StartsWith("new file mode", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode", StringComparison.Ordinal)
                || line.StartsWith("similarity index", StringComparison.Ordinal)
                || line.StartsWith("rename from", StringComparison.Ordinal)
                || line.StartsWith("rename to", StringComparison.Ordinal))
                return GitDiffLineKind.Metadata;

            return GitDiffLineKind.Context;
        }

        private static string GetDisplayStatus(char indexStatus, char workTreeStatus)
        {
            if (indexStatus == '?' && workTreeStatus == '?')
                return "A";
            if (indexStatus == 'U' || workTreeStatus == 'U')
                return "U";
            if (indexStatus == 'D' || workTreeStatus == 'D')
                return "D";
            if (indexStatus == 'R' || workTreeStatus == 'R')
                return "R";
            if (indexStatus == 'A' || workTreeStatus == 'A')
                return "A";
            if (indexStatus == 'C' || workTreeStatus == 'C')
                return "C";

            return "M";
        }

        private static string GetStatusText(string status)
        {
            switch (status)
            {
                case "A":
                    return "added";
                case "D":
                    return "deleted";
                case "R":
                    return "renamed";
                case "C":
                    return "copied";
                case "U":
                    return "conflict";
                default:
                    return "modified";
            }
        }

        private static System.Windows.Media.Brush CreateStatusBrush(string status)
        {
            var color = "#9B8B65";
            if (status == "A")
                color = "#3F9D64";
            else if (status == "D" || status == "U")
                color = "#D45B55";
            else if (status == "R" || status == "C")
                color = "#0A86FF";

            var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private static string ResolveDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (Directory.Exists(path))
                return Path.GetFullPath(path);

            if (File.Exists(path))
                return Path.GetDirectoryName(Path.GetFullPath(path));

            var extension = Path.GetExtension(path);
            return string.IsNullOrEmpty(extension)
                ? Path.GetFullPath(path)
                : Path.GetDirectoryName(Path.GetFullPath(path));
        }

        private GitCommandResult GetStatusPorcelain(string repositoryRoot)
        {
            return _runner.Run(repositoryRoot, "-c", "core.quotepath=false", "status", "--porcelain=v1", "--untracked-files=all");
        }

        private string GetTrackedFilePatch(string repositoryRoot, GitChangeInfo change, bool hasHead, CancellationToken cancellationToken)
        {
            var paths = new List<string>();
            if (!string.IsNullOrWhiteSpace(change.OldPath))
                paths.Add(NormalizeGitPath(change.OldPath));
            if (!string.IsNullOrWhiteSpace(change.RelativePath)
                && !paths.Contains(NormalizeGitPath(change.RelativePath), StringComparer.Ordinal))
            {
                paths.Add(NormalizeGitPath(change.RelativePath));
            }

            if (paths.Count == 0)
                return "";

            var patch = new StringBuilder();
            if (hasHead)
            {
                AppendSuccessfulDiff(patch, RunFileDiff(repositoryRoot, paths, "HEAD", false, cancellationToken));
            }
            else
            {
                AppendSuccessfulDiff(patch, RunFileDiff(repositoryRoot, paths, null, true, cancellationToken));
                AppendSuccessfulDiff(patch, RunFileDiff(repositoryRoot, paths, null, false, cancellationToken));
            }

            return TruncateUtf8(patch.ToString(), MaxAiFileBytes, "\n[AI diff truncated: per-file limit reached]\n");
        }

        private GitCommandResult RunFileDiff(string repositoryRoot, IList<string> paths, string revision, bool cached, CancellationToken cancellationToken)
        {
            var args = new List<string>
            {
                "-c",
                "core.quotepath=false",
                "--literal-pathspecs",
                "diff",
                "--no-ext-diff",
                "--no-textconv",
                "--no-color",
                "--find-renames",
                "--ignore-submodules=none",
                "--unified=3"
            };

            if (cached)
                args.Add("--cached");
            if (!string.IsNullOrWhiteSpace(revision))
                args.Add(revision);

            args.Add("--");
            args.AddRange(paths);
            return _runner.Run(repositoryRoot, cancellationToken, args.ToArray());
        }

        private static void AppendSuccessfulDiff(StringBuilder target, GitCommandResult result)
        {
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Output))
                return;

            if (target.Length > 0 && target[target.Length - 1] != '\n')
                target.AppendLine();
            target.Append(result.Output);
        }

        private static string BuildUntrackedFilePatch(GitChangeInfo change)
        {
            string text;
            bool wasTruncated;
            if (!TryReadTextFileForAi(change.FullPath, out text, out wasTruncated))
            {
                var path = NormalizeGitPath(change.RelativePath);
                return "diff --git a/" + path + " b/" + path + "\n"
                    + "new file mode 100644\n"
                    + "[Binary or unreadable untracked file omitted]\n";
            }

            var relativePath = NormalizeGitPath(change.RelativePath);
            var normalizedText = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            var builder = new StringBuilder();
            builder.Append("diff --git a/").Append(relativePath).Append(" b/").Append(relativePath).AppendLine();
            builder.AppendLine("new file mode 100644");
            builder.AppendLine("--- /dev/null");
            builder.Append("+++ b/").Append(relativePath).AppendLine();

            if (normalizedText.Length == 0)
            {
                builder.AppendLine("[Empty text file]");
                return builder.ToString();
            }

            var lines = normalizedText.Split(new[] { '\n' });
            if (lines.Length > 0 && lines[lines.Length - 1].Length == 0)
                Array.Resize(ref lines, lines.Length - 1);
            builder.Append("@@ -0,0 +1,").Append(lines.Length).AppendLine(" @@");
            foreach (var line in lines)
                builder.Append('+').AppendLine(line);

            if (wasTruncated)
                builder.AppendLine("[AI file content truncated: per-file limit reached]");

            return TruncateUtf8(builder.ToString(), MaxAiFileBytes, "\n[AI diff truncated: per-file limit reached]\n");
        }

        private static bool TryReadTextFileForAi(string fullPath, out string text, out bool wasTruncated)
        {
            text = "";
            wasTruncated = false;
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                return false;

            try
            {
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    return false;

                byte[] bytes;
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var count = (int)Math.Min(stream.Length, MaxAiFileBytes);
                    bytes = new byte[count];
                    var offset = 0;
                    while (offset < count)
                    {
                        var read = stream.Read(bytes, offset, count - offset);
                        if (read <= 0)
                            break;
                        offset += read;
                    }

                    if (offset != bytes.Length)
                        Array.Resize(ref bytes, offset);
                    wasTruncated = stream.Length > bytes.Length;
                }

                return TryDecodeText(bytes, wasTruncated, out text);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDecodeText(byte[] bytes, bool mayEndMidCharacter, out string text)
        {
            text = "";
            if (bytes == null || bytes.Length == 0)
                return true;

            Encoding encoding;
            var preambleLength = 0;
            var codeUnitSize = 1;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = new UTF8Encoding(false, true);
                preambleLength = 3;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = new UnicodeEncoding(false, true, true);
                preambleLength = 2;
                codeUnitSize = 2;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = new UnicodeEncoding(true, true, true);
                preambleLength = 2;
                codeUnitSize = 2;
            }
            else
            {
                Encoding utf16;
                if (TryDetectUtf16WithoutBom(bytes, out utf16))
                {
                    encoding = utf16;
                    codeUnitSize = 2;
                }
                else
                {
                    if (bytes.Any(value => value == 0))
                        return false;
                    encoding = new UTF8Encoding(false, true);
                }
            }

            var count = bytes.Length - preambleLength;
            if (codeUnitSize > 1)
                count -= count % codeUnitSize;

            for (var trim = 0; trim <= (mayEndMidCharacter ? 4 : 0) && count - trim >= 0; trim++)
            {
                var candidateCount = count - trim;
                if (candidateCount % codeUnitSize != 0)
                    continue;

                try
                {
                    text = encoding.GetString(bytes, preambleLength, candidateCount);
                    return true;
                }
                catch (DecoderFallbackException)
                {
                }
            }

            return false;
        }

        private static bool TryDetectUtf16WithoutBom(byte[] bytes, out Encoding encoding)
        {
            encoding = null;
            if (bytes == null || bytes.Length < 4 || bytes.Length % 2 != 0)
                return false;

            var evenZeros = 0;
            var oddZeros = 0;
            for (var index = 0; index < bytes.Length; index += 2)
            {
                if (bytes[index] == 0)
                    evenZeros++;
                if (bytes[index + 1] == 0)
                    oddZeros++;
            }

            var pairs = bytes.Length / 2;
            if (oddZeros * 5 >= pairs * 2 && evenZeros * 5 < pairs)
                encoding = new UnicodeEncoding(false, false, true);
            else if (evenZeros * 5 >= pairs * 2 && oddZeros * 5 < pairs)
                encoding = new UnicodeEncoding(true, false, true);

            return encoding != null;
        }

        private static string TruncateUtf8(string value, int maxBytes, string marker)
        {
            value = value ?? "";
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
                return value;

            marker = marker ?? "";
            var contentLimit = Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(marker));
            var low = 0;
            var high = value.Length;
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (Encoding.UTF8.GetByteCount(value.Substring(0, middle)) <= contentLimit)
                    low = middle;
                else
                    high = middle - 1;
            }

            if (low > 0 && low < value.Length && char.IsHighSurrogate(value[low - 1]))
                low--;
            return value.Substring(0, low) + marker;
        }

        private static bool AppendAiPatchPart(StringBuilder target, string value)
        {
            if (target.Length > 0 && target[target.Length - 1] != '\n')
                target.AppendLine();

            var remaining = MaxAiPatchLength - target.Length;
            if (remaining <= 0)
                return false;
            if (value.Length <= remaining)
            {
                target.Append(value);
                return true;
            }

            const string marker = "\n[AI diff truncated: total limit reached]\n";
            var contentLength = Math.Max(0, remaining - marker.Length);
            if (contentLength > 0)
                target.Append(value.Substring(0, contentLength));
            if (target.Length < MaxAiPatchLength)
                target.Append(marker.Substring(0, Math.Min(marker.Length, MaxAiPatchLength - target.Length)));
            return false;
        }

        private static string NormalizeGitPath(string path)
        {
            return (path ?? "").Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static string UnquoteGitPath(string path)
        {
            if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
                return path.Substring(1, path.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");

            return path;
        }
    }
}
