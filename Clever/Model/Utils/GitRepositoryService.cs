using Clever.ViewModel.PanelsVM;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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
    }

    internal class GitBranchInfo
    {
        public string Name { get; set; }
        public bool IsActive { get; set; }
    }

    internal class GitRepositoryService
    {
        private const int MaxCommitCount = 50;
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
            snapshot.Changes.AddRange(GetChanges(repositoryRoot));
            snapshot.Branches.AddRange(GetBranches(repositoryRoot, snapshot.BranchName));
            snapshot.Commits.AddRange(GetCommits(repositoryRoot, snapshot.BranchName));

            return snapshot;
        }

        internal List<GitChangeInfo> GetWorkingTreeChanges(string repositoryRoot)
        {
            return string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot)
                ? new List<GitChangeInfo>()
                : GetChanges(repositoryRoot);
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

        private GitActionResult EnsureWorkingTreeClean(string repositoryRoot, string dirtyMessage)
        {
            var status = GetStatusPorcelain(repositoryRoot);
            if (!status.Success)
                return GitActionResult.Fail(status.Message);

            return string.IsNullOrWhiteSpace(status.Output)
                ? GitActionResult.Ok("")
                : GitActionResult.Fail(dirtyMessage);
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

        private List<GitChangeInfo> GetChanges(string repositoryRoot)
        {
            var changes = new List<GitChangeInfo>();
            var result = GetStatusPorcelain(repositoryRoot);
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
                        IsDeleted = status == "D"
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

        private List<GitCommitVM> GetCommits(string repositoryRoot, string currentBranch)
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
                        Summary = parts[5]
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
                if (decoration.StartsWith("HEAD -> ", StringComparison.Ordinal))
                    decoration = decoration.Substring("HEAD -> ".Length);
                if (decoration == "HEAD")
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
                        continue;
                    }

                    if (currentFile == null)
                    {
                        currentFile = new GitDiffFileVM(commit.ShortHash);
                        document.Files.Add(currentFile);
                    }

                    currentFile.Lines.Add(CreateDiffLine(line));
                }
            }

            return document;
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
