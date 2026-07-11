using Clev3r.AgentCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Clever.Model.Agent
{
    /// <summary>
    /// Reads the optional user-authored example library once during an IDE process.
    /// Markdown is exposed as untrusted reference data and never interpreted here.
    /// </summary>
    internal sealed class ExampleLibraryService : IExampleLibraryService
    {
        private const int DefaultLimit = 8;
        private const int MaximumLimit = 20;
        private const int MaximumExampleBytes = 256 * 1024;
        private static readonly Regex WordRegex = new Regex(@"[\p{L}\p{Nd}_]+", RegexOptions.Compiled);
        private static readonly Regex WhiteSpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        private readonly object _syncRoot = new object();
        private readonly string _rootOverride;
        private ExampleCatalog _catalog;

        internal ExampleLibraryService()
            : this(null)
        {
        }

        internal ExampleLibraryService(string root)
        {
            _rootOverride = root;
        }

        public void Warmup()
        {
            EnsureCatalog();
        }

        public Task<OperationResult<IList<DocumentationSearchHit>>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Result(OperationResult<IList<DocumentationSearchHit>>.Fail("operation_cancelled", "The example search was cancelled."));
            if (string.IsNullOrWhiteSpace(query))
                return Result(OperationResult<IList<DocumentationSearchHit>>.Fail("invalid_examples_query", "Example search query must not be empty."));

            var catalog = EnsureCatalog();
            var normalizedQuery = Normalize(query);
            var tokens = Tokenize(normalizedQuery);
            var actualLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaximumLimit);
            var hits = catalog.Documents
                .Select(document => new { Document = document, Score = Score(document, normalizedQuery, tokens) })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Document.Topic.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Document.Topic.SourcePath, StringComparer.Ordinal)
                .Take(actualLimit)
                .Select(item => new DocumentationSearchHit
                {
                    Id = item.Document.Topic.Id,
                    Kind = "human_example",
                    Title = item.Document.Topic.Title,
                    Summary = item.Document.Summary,
                    SourcePath = item.Document.Topic.SourcePath,
                    Score = item.Score
                })
                .ToList();

            return Result(OperationResult<IList<DocumentationSearchHit>>.Ok(hits));
        }

        public Task<OperationResult<DocumentationTopic>> ReadAsync(string id, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Result(OperationResult<DocumentationTopic>.Fail("operation_cancelled", "The example read was cancelled."));
            if (string.IsNullOrWhiteSpace(id))
                return Result(OperationResult<DocumentationTopic>.Fail("invalid_example_id", "Example identifier must not be empty."));

            DocumentationTopic topic;
            if (!EnsureCatalog().Topics.TryGetValue(id.Trim(), out topic))
                return Result(OperationResult<DocumentationTopic>.Fail("example_not_found", "Example was not found: " + id.Trim()));
            return Result(OperationResult<DocumentationTopic>.Ok(Clone(topic)));
        }

        private ExampleCatalog EnsureCatalog()
        {
            lock (_syncRoot)
            {
                if (_catalog == null)
                    _catalog = BuildCatalog(NormalizeDirectory(_rootOverride ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai-examples")));
                return _catalog;
            }
        }

        private static ExampleCatalog BuildCatalog(string root)
        {
            var catalog = new ExampleCatalog();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return catalog;

            string[] files;
            try
            {
                files = EnumerateMarkdownFiles(root)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch
            {
                return catalog;
            }

            foreach (var path in files)
            {
                try
                {
                    var file = new FileInfo(path);
                    if (file.Length > MaximumExampleBytes)
                        continue;
                    var markdown = File.ReadAllText(path, Encoding.UTF8);
                    var relativePath = RelativePath(root, path);
                    var topic = new DocumentationTopic
                    {
                        Id = "example:" + StableId(relativePath),
                        Title = FirstHeading(markdown) ?? Path.GetFileNameWithoutExtension(path),
                        Markdown = markdown,
                        SourcePath = relativePath
                    };
                    catalog.Topics[topic.Id] = topic;
                    catalog.Documents.Add(new ExampleDocument
                    {
                        Topic = topic,
                        Summary = FirstParagraph(markdown),
                        SearchText = Normalize(topic.Title + " " + topic.SourcePath + " " + markdown)
                    });
                }
                catch
                {
                    // A malformed user example must not prevent the assistant from starting.
                }
            }
            return catalog;
        }

        private static IEnumerable<string> EnumerateMarkdownFiles(string root)
        {
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string[] children;
                try { children = Directory.GetFileSystemEntries(pending.Pop()); }
                catch { continue; }
                foreach (var child in children)
                {
                    try
                    {
                        var attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                            pending.Push(child);
                        else if (string.Equals(Path.GetExtension(child), ".md", StringComparison.OrdinalIgnoreCase))
                            files.Add(child);
                    }
                    catch { }
                }
            }
            return files;
        }

        private static double Score(ExampleDocument document, string normalizedQuery, IList<string> tokens)
        {
            if (document == null || tokens == null || tokens.Count == 0)
                return 0;
            var score = document.SearchText.Contains(normalizedQuery) ? 100 : 0;
            foreach (var token in tokens)
            {
                var titleMatches = Count(Normalize(document.Topic.Title), token);
                var pathMatches = Count(Normalize(document.Topic.SourcePath), token);
                var contentMatches = Count(document.SearchText, token);
                score += titleMatches * 24 + pathMatches * 10 + contentMatches;
            }
            return score;
        }

        private static int Count(string text, string token)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return 0;
            var count = 0;
            var start = 0;
            while ((start = text.IndexOf(token, start, StringComparison.Ordinal)) >= 0)
            {
                count++;
                start += token.Length;
            }
            return count;
        }

        private static string Normalize(string value)
        {
            return WhiteSpaceRegex.Replace(string.Join(" ", Tokenize(value)), " ").Trim().ToLowerInvariant();
        }

        private static IList<string> Tokenize(string value)
        {
            return WordRegex.Matches(value ?? "").Cast<Match>().Select(match => match.Value.ToLowerInvariant()).Where(word => word.Length > 1).ToList();
        }

        private static string FirstHeading(string markdown)
        {
            return (markdown ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("#", StringComparison.Ordinal))
                .Select(line => line.TrimStart('#').Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

        private static string FirstParagraph(string markdown)
        {
            var paragraph = (markdown ?? "").Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.None)
                .Select(value => value.Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("#", StringComparison.Ordinal));
            return WhiteSpaceRegex.Replace(paragraph ?? "", " ").Trim();
        }

        private static string StableId(string value)
        {
            using (var hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }
        }

        private static string NormalizeDirectory(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string RelativePath(string root, string path)
        {
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(normalizedRoot.Length).Replace(Path.DirectorySeparatorChar, '/')
                : Path.GetFileName(path);
        }

        private static DocumentationTopic Clone(DocumentationTopic topic)
        {
            return new DocumentationTopic { Id = topic.Id, Title = topic.Title, Markdown = topic.Markdown, SourcePath = topic.SourcePath };
        }

        private static Task<OperationResult<T>> Result<T>(OperationResult<T> result)
        {
            return Task.FromResult(result);
        }

        private sealed class ExampleCatalog
        {
            internal ExampleCatalog()
            {
                Topics = new Dictionary<string, DocumentationTopic>(StringComparer.Ordinal);
                Documents = new List<ExampleDocument>();
            }

            internal Dictionary<string, DocumentationTopic> Topics { get; private set; }
            internal List<ExampleDocument> Documents { get; private set; }
        }

        private sealed class ExampleDocument
        {
            internal DocumentationTopic Topic { get; set; }
            internal string Summary { get; set; }
            internal string SearchText { get; set; }
        }
    }
}
