using NEW_CLEVER.AgentCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEW_CLEVER.Model.Agent
{
    /// <summary>Bounded read-only project search. It resolves every requested path under the active project root.</summary>
    internal sealed class ProjectSourceSearchService : IProjectSourceSearchService
    {
        private const int MaximumFileBytes = 256 * 1024;
        private const int MaximumReadCharacters = 64 * 1024;
        private const int MaximumReadLines = 400;
        private static readonly HashSet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".bp", ".bpi", ".bpm", ".md", ".txt", ".csv", ".json", ".xml"
        };
        private static readonly HashSet<string> ExcludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", "bin", "obj", "ai-examples"
        };

        private readonly IWorkspaceContextService _workspace;

        internal ProjectSourceSearchService(IWorkspaceContextService workspace)
        {
            _workspace = workspace ?? throw new ArgumentNullException("workspace");
        }

        public async Task<OperationResult<IList<ProjectSearchHit>>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
                return OperationResult<IList<ProjectSearchHit>>.Fail("invalid_project_query", "Project search query must not be empty.");
            var context = await _workspace.CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (!context.Success) return OperationResult<IList<ProjectSearchHit>>.Fail(context.Code, context.Message);
            var root = NormalizeRoot(context.Data == null ? null : context.Data.ProjectRoot);
            if (root == null) return OperationResult<IList<ProjectSearchHit>>.Fail("project_not_open", "Open a project before searching its files.");

            var terms = query.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var actualLimit = Math.Max(1, Math.Min(20, limit <= 0 ? 8 : limit));
            var hits = new List<ProjectSearchHit>();
            foreach (var path in EnumerateTextFiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = ToRelativePath(root, path);
                var lineNumber = 0;
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    lineNumber++;
                    var score = Score(line, terms);
                    if (score <= 0) continue;
                    hits.Add(new ProjectSearchHit { RelativePath = relativePath, Line = lineNumber, Snippet = Limit(line.Trim(), 500), Score = score });
                }
            }
            return OperationResult<IList<ProjectSearchHit>>.Ok(hits
                .OrderByDescending(hit => hit.Score)
                .ThenBy(hit => hit.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(hit => hit.Line)
                .Take(actualLimit)
                .ToList());
        }

        public async Task<OperationResult<ProjectFileContent>> ReadAsync(string path, int? startLine, int? endLine, CancellationToken cancellationToken)
        {
            var context = await _workspace.CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (!context.Success) return OperationResult<ProjectFileContent>.Fail(context.Code, context.Message);
            var root = NormalizeRoot(context.Data == null ? null : context.Data.ProjectRoot);
            if (root == null) return OperationResult<ProjectFileContent>.Fail("project_not_open", "Open a project before reading its files.");
            var resolved = ResolveProjectFile(root, path);
            if (resolved == null) return OperationResult<ProjectFileContent>.Fail("invalid_project_path", "The requested path is outside the active project or is not a supported text file.");
            if (!File.Exists(resolved)) return OperationResult<ProjectFileContent>.Fail("project_file_not_found", "Project file was not found: " + path);
            if (new FileInfo(resolved).Length > MaximumFileBytes) return OperationResult<ProjectFileContent>.Fail("project_file_too_large", "Project file is too large to read safely.");
            if (!LooksLikeTextFile(resolved)) return OperationResult<ProjectFileContent>.Fail("project_file_binary", "Project file is binary and cannot be read as source text.");

            var requestedStart = Math.Max(1, startLine ?? 1);
            var requestedEnd = Math.Max(requestedStart, endLine ?? (requestedStart + MaximumReadLines - 1));
            var lines = new List<string>();
            var lineNumber = 0;
            var characters = 0;
            var truncated = false;
            foreach (var line in File.ReadLines(resolved, Encoding.UTF8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                if (lineNumber < requestedStart) continue;
                if (lineNumber > requestedEnd || lines.Count >= MaximumReadLines || characters + line.Length + Environment.NewLine.Length > MaximumReadCharacters)
                {
                    truncated = true;
                    break;
                }
                lines.Add(line);
                characters += line.Length + Environment.NewLine.Length;
            }
            return OperationResult<ProjectFileContent>.Ok(new ProjectFileContent
            {
                RelativePath = ToRelativePath(root, resolved),
                StartLine = requestedStart,
                EndLine = requestedStart + Math.Max(0, lines.Count - 1),
                Content = string.Join(Environment.NewLine, lines),
                Truncated = truncated
            });
        }

        private static IEnumerable<string> EnumerateTextFiles(string root)
        {
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                string[] children;
                try { children = Directory.GetFileSystemEntries(directory); }
                catch { continue; }
                foreach (var child in children)
                {
                    try
                    {
                        var attributes = File.GetAttributes(child);
                        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (!ExcludedDirectories.Contains(Path.GetFileName(child))) pending.Push(child);
                        }
                        else if (AllowedExtensions.Contains(Path.GetExtension(child)) && new FileInfo(child).Length <= MaximumFileBytes && LooksLikeTextFile(child))
                        {
                            files.Add(child);
                        }
                    }
                    catch { }
                }
            }
            return files;
        }

        private static string ResolveProjectFile(string root, string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath)) return null;
            string fullPath;
            try { fullPath = Path.GetFullPath(Path.IsPathRooted(requestedPath) ? requestedPath : Path.Combine(root, requestedPath)); }
            catch { return null; }
            if (!IsInsideRoot(root, fullPath) || !AllowedExtensions.Contains(Path.GetExtension(fullPath))) return null;
            var relative = ToRelativePath(root, fullPath);
            if (relative.Split('/').Any(part => ExcludedDirectories.Contains(part))) return null;
            return fullPath;
        }

        private static bool LooksLikeTextFile(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buffer = new byte[Math.Min(4096, (int)Math.Min(stream.Length, 4096))];
                    var count = stream.Read(buffer, 0, buffer.Length);
                    for (var index = 0; index < count; index++)
                    {
                        var value = buffer[index];
                        if (value == 0) return false;
                        if (value < 32 && value != 9 && value != 10 && value != 12 && value != 13) return false;
                    }
                    return true;
                }
            }
            catch { return false; }
        }

        private static string NormalizeRoot(string root)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
                return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return null; }
        }

        private static bool IsInsideRoot(string root, string path)
        {
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string ToRelativePath(string root, string path)
        {
            return path.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static double Score(string line, IEnumerable<string> terms)
        {
            var score = 0d;
            foreach (var term in terms)
            {
                var index = 0;
                var found = false;
                while ((index = line.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    found = true;
                    score++;
                    index += term.Length;
                }
                if (!found) return 0;
            }
            return score;
        }

        private static string Limit(string value, int maximum)
        {
            value = value ?? "";
            return value.Length <= maximum ? value : value.Substring(0, maximum) + "…";
        }
    }
}
