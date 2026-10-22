using NEW_CLEVER.CommonData;
using NEW_CLEVER.AgentCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace NEW_CLEVER.Model.Agent
{
    /// <summary>
    /// Builds a read-only, in-memory search index from the documentation shipped with
    /// NEW_CLEVER. Documentation text is data: it is never executed or interpreted as an
    /// instruction by this service.
    /// </summary>
    internal sealed class DocumentationSearchService : IDocumentationSearchService
    {
        private const int DefaultLimit = 10;
        private const int MaximumLimit = 50;
        private const int MaximumExampleBytes = 128 * 1024;
        private const int MaximumGuideCharacters = 12000;
        private static readonly Regex WordRegex = new Regex(@"[\p{L}\p{Nd}_]+", RegexOptions.Compiled);
        private static readonly Regex WhiteSpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        private readonly object _syncRoot = new object();
        private readonly string _languageOverride;
        private readonly string _helpRootOverride;
        private readonly string _examplesRootOverride;
        private readonly string _guidePathOverride;
        private DocumentationCatalog _catalog;
        private string _catalogKey;

        internal DocumentationSearchService()
            : this(null, null, null, null)
        {
        }

        internal DocumentationSearchService(
            string language,
            string helpRoot,
            string examplesRoot,
            string guidePath)
        {
            _languageOverride = language;
            _helpRootOverride = helpRoot;
            _examplesRootOverride = examplesRoot;
            _guidePathOverride = guidePath;
        }

        public Task<OperationResult<IList<DocumentationSearchHit>>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Cancelled<IList<DocumentationSearchHit>>();
            if (string.IsNullOrWhiteSpace(query))
                return Result(OperationResult<IList<DocumentationSearchHit>>.Fail(
                    "invalid_documentation_query",
                    "Documentation search query must not be empty."));

            DocumentationCatalog catalog;
            string error;
            if (!TryGetCatalog(out catalog, out error))
                return Result(OperationResult<IList<DocumentationSearchHit>>.Fail(
                    "documentation_unavailable", error));

            var normalizedQuery = NormalizeSearchText(query);
            var tokens = Tokenize(normalizedQuery);
            var actualLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaximumLimit);
            var hits = catalog.SearchDocuments
                .Select(document => new { Document = document, Score = Score(document, normalizedQuery, tokens) })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Document.Topic.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Document.Topic.Title, StringComparer.Ordinal)
                .ThenBy(item => item.Document.Topic.Id, StringComparer.Ordinal)
                .Take(actualLimit)
                .Select(item => new DocumentationSearchHit
                {
                    Id = item.Document.Topic.Id,
                    Kind = item.Document.Kind,
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
                return Cancelled<DocumentationTopic>();
            if (string.IsNullOrWhiteSpace(id))
                return Result(OperationResult<DocumentationTopic>.Fail(
                    "invalid_documentation_id", "Documentation topic id must not be empty."));

            DocumentationCatalog catalog;
            string error;
            if (!TryGetCatalog(out catalog, out error))
                return Result(OperationResult<DocumentationTopic>.Fail(
                    "documentation_unavailable", error));

            DocumentationTopic topic;
            if (!catalog.Topics.TryGetValue(id.Trim(), out topic))
                return Result(OperationResult<DocumentationTopic>.Fail(
                    "documentation_topic_not_found", "Documentation topic was not found: " + id.Trim()));

            return Result(OperationResult<DocumentationTopic>.Ok(Clone(topic)));
        }

        public string GetCompactGuide()
        {
            DocumentationCatalog catalog;
            string error;
            if (!TryGetCatalog(out catalog, out error))
                return "";
            return Limit(catalog.CompactGuide, MaximumGuideCharacters);
        }

        private bool TryGetCatalog(out DocumentationCatalog catalog, out string error)
        {
            var language = NormalizeLanguage(_languageOverride ?? Configurations.Get.Language);
            var baseDirectory = GetBaseDirectory();
            var helpRoot = NormalizeDirectory(_helpRootOverride ?? Path.Combine(baseDirectory, "Help"));
            var examplesRoot = NormalizeDirectory(_examplesRootOverride ?? Path.Combine(baseDirectory, "Examples"));
            var guidePath = NormalizeFile(_guidePathOverride ?? Path.Combine(baseDirectory, "Resources", "Codex", "BP_GUIDE.md"));
            var selectedHelpDirectory = SelectHelpDirectory(helpRoot, language);
            var key = string.Join("|", language, helpRoot, selectedHelpDirectory, examplesRoot, guidePath);

            lock (_syncRoot)
            {
                if (_catalog != null && string.Equals(_catalogKey, key, StringComparison.Ordinal))
                {
                    catalog = _catalog;
                    error = _catalog.Error;
                    return string.IsNullOrEmpty(error);
                }

                _catalog = BuildCatalog(selectedHelpDirectory, examplesRoot, guidePath);
                _catalogKey = key;
                catalog = _catalog;
                error = catalog.Error;
                return string.IsNullOrEmpty(error);
            }
        }

        private static DocumentationCatalog BuildCatalog(
            string helpDirectory,
            string examplesRoot,
            string guidePath)
        {
            var catalog = new DocumentationCatalog();
            var readableSources = 0;

            if (!string.IsNullOrWhiteSpace(guidePath) && File.Exists(guidePath))
            {
                try
                {
                    var markdown = File.ReadAllText(guidePath, Encoding.UTF8);
                    catalog.CompactGuide = Limit(markdown.Trim(), MaximumGuideCharacters);
                    AddTopic(catalog, new DocumentationTopic
                    {
                        Id = "guide:bp",
                        Title = "NEW_CLEVER Basic Plus compact guide",
                        Markdown = markdown,
                        SourcePath = "Resources/Codex/BP_GUIDE.md"
                    }, "guide", FirstParagraph(markdown));
                    readableSources++;
                }
                catch (Exception ex)
                {
                    catalog.Errors.Add("BP guide: " + ex.Message);
                }
            }

            if (!string.IsNullOrWhiteSpace(helpDirectory) && Directory.Exists(helpDirectory))
            {
                var operatorsPath = Path.Combine(helpDirectory, "Operators.txt");
                if (File.Exists(operatorsPath))
                {
                    try
                    {
                        var operators = File.ReadAllText(operatorsPath, Encoding.UTF8).Trim();
                        AddTopic(catalog, new DocumentationTopic
                        {
                            Id = "operators:bp",
                            Title = "Basic Plus operators",
                            Markdown = "# Basic Plus operators\n\n```text\n" + operators + "\n```",
                            SourcePath = HelpSourcePath(helpDirectory, "Operators.txt")
                        }, "operators", FirstParagraph(operators));
                        readableSources++;
                    }
                    catch (Exception ex)
                    {
                        catalog.Errors.Add("Operators.txt: " + ex.Message);
                    }
                }

                string[] xmlFiles;
                try
                {
                    xmlFiles = Directory.GetFiles(helpDirectory, "*.xml", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(path => path, StringComparer.Ordinal)
                        .ToArray();
                }
                catch (Exception ex)
                {
                    catalog.Errors.Add("Help directory: " + ex.Message);
                    xmlFiles = new string[0];
                }

                foreach (var path in xmlFiles)
                {
                    try
                    {
                        IndexHelpFile(catalog, helpDirectory, path);
                        readableSources++;
                    }
                    catch (Exception ex)
                    {
                        catalog.Errors.Add(Path.GetFileName(path) + ": " + ex.Message);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(examplesRoot) && Directory.Exists(examplesRoot))
            {
                string[] examples;
                try
                {
                    examples = Directory.GetFiles(examplesRoot, "*.*", SearchOption.AllDirectories)
                        .Where(IsBpExample)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(path => path, StringComparer.Ordinal)
                        .ToArray();
                }
                catch (Exception ex)
                {
                    catalog.Errors.Add("Examples directory: " + ex.Message);
                    examples = new string[0];
                }

                foreach (var path in examples)
                {
                    try
                    {
                        var file = new FileInfo(path);
                        if (file.Length > MaximumExampleBytes)
                        {
                            catalog.Errors.Add("Example is too large to index: " + file.Name);
                            continue;
                        }
                        IndexExample(catalog, examplesRoot, path);
                        readableSources++;
                    }
                    catch (Exception ex)
                    {
                        catalog.Errors.Add(Path.GetFileName(path) + ": " + ex.Message);
                    }
                }
            }

            if (catalog.Topics.Count == 0 || readableSources == 0)
            {
                catalog.Error = "NEW_CLEVER documentation could not be loaded from the runtime Help, Examples, or BP guide directories.";
                if (catalog.Errors.Count > 0)
                    catalog.Error += " " + string.Join("; ", catalog.Errors.Take(3));
            }

            return catalog;
        }

        private static void IndexHelpFile(DocumentationCatalog catalog, string helpDirectory, string path)
        {
            var document = XDocument.Load(path, LoadOptions.None);
            var fileName = Path.GetFileNameWithoutExtension(path) ?? "help";
            var sourcePath = HelpSourcePath(helpDirectory, Path.GetFileName(path));
            foreach (var member in document.Descendants("member"))
            {
                var memberName = ((string)member.Attribute("name") ?? "").Trim();
                if (memberName.Length < 3 || memberName[1] != ':')
                    continue;

                var kind = GetMemberKind(memberName[0]);
                var localName = memberName.Substring(2).Trim();
                var parameters = member.Elements("param")
                    .Select(item => ((string)item.Attribute("name") ?? "").Trim())
                    .Where(item => item.Length > 0)
                    .ToList();
                var title = BuildMemberTitle(fileName, kind, localName, parameters);
                var summary = CompactText(ElementValue(member.Element("summary")));
                var markdown = BuildMemberMarkdown(title, kind, member, summary);
                var stableKey = fileName.ToLowerInvariant() + "|" + memberName.ToLowerInvariant();
                var id = "help:" + Slug(fileName) + ":" + Slug(kind) + ":" + StableHash(stableKey);

                AddTopic(catalog, new DocumentationTopic
                {
                    Id = id,
                    Title = title,
                    Markdown = markdown,
                    SourcePath = sourcePath
                }, kind, summary);
            }
        }

        private static void IndexExample(DocumentationCatalog catalog, string examplesRoot, string path)
        {
            var relativePath = GetRelativePath(examplesRoot, path);
            var source = File.ReadAllText(path, Encoding.UTF8);
            var id = "example:" + Slug(Path.GetFileNameWithoutExtension(path)) + ":" +
                StableHash(relativePath.Replace('\\', '/').ToLowerInvariant());
            AddTopic(catalog, new DocumentationTopic
            {
                Id = id,
                Title = "Example: " + relativePath.Replace('\\', '/'),
                Markdown = "# Example: " + relativePath.Replace('\\', '/') + "\n\n```basic\n" + source.TrimEnd() + "\n```",
                SourcePath = "Examples/" + relativePath.Replace('\\', '/')
            }, "example", CompactText(source));
        }

        private static string BuildMemberMarkdown(string title, string kind, XElement member, string summary)
        {
            var builder = new StringBuilder();
            builder.Append("# ").AppendLine(title).AppendLine();
            builder.Append("Kind: ").AppendLine(kind).AppendLine();
            if (!string.IsNullOrWhiteSpace(summary))
                builder.AppendLine(summary).AppendLine();

            var parameters = member.Elements("param").ToList();
            if (parameters.Count > 0)
            {
                builder.AppendLine("## Parameters").AppendLine();
                foreach (var parameter in parameters)
                {
                    var name = ((string)parameter.Attribute("name") ?? "parameter").Trim();
                    builder.Append("- `").Append(name).Append("`: ")
                        .AppendLine(CompactText(parameter.Value));
                }
                builder.AppendLine();
            }

            var returns = CompactText(ElementValue(member.Element("returns")));
            if (!string.IsNullOrWhiteSpace(returns))
                builder.AppendLine("## Returns").AppendLine().AppendLine(returns).AppendLine();

            foreach (var example in member.Elements("example"))
            {
                builder.AppendLine("## Example").AppendLine();
                var text = CompactText(ElementValue(example.Element("text")));
                if (!string.IsNullOrWhiteSpace(text))
                    builder.AppendLine(text).AppendLine();
                var code = NormalizeLines(ElementValue(example.Element("code"))).Trim();
                if (!string.IsNullOrWhiteSpace(code))
                    builder.AppendLine("```basic").AppendLine(code).AppendLine("```").AppendLine();
            }
            return builder.ToString().TrimEnd();
        }

        private static string BuildMemberTitle(string objectName, string kind, string localName, IList<string> parameters)
        {
            if (kind == "object")
                return localName;
            if (kind == "keyword")
                return localName;
            var qualifiedName = localName.IndexOf('.') >= 0 ? localName : objectName + "." + localName;
            if (kind == "method")
                return qualifiedName + "(" + string.Join(", ", parameters) + ")";
            return qualifiedName;
        }

        private static void AddTopic(
            DocumentationCatalog catalog,
            DocumentationTopic topic,
            string kind,
            string summary)
        {
            if (topic == null || string.IsNullOrWhiteSpace(topic.Id) || catalog.Topics.ContainsKey(topic.Id))
                return;
            topic.Markdown = topic.Markdown ?? "";
            topic.SourcePath = topic.SourcePath ?? "";
            catalog.Topics.Add(topic.Id, topic);
            catalog.SearchDocuments.Add(new SearchDocument
            {
                Topic = topic,
                Kind = kind ?? "documentation",
                Summary = Limit(CompactText(summary), 280),
                NormalizedTitle = NormalizeSearchText(topic.Title),
                NormalizedBody = NormalizeSearchText(topic.Markdown)
            });
        }

        private static double Score(SearchDocument document, string query, ISet<string> tokens)
        {
            var score = 0d;
            if (string.Equals(document.NormalizedTitle, query, StringComparison.Ordinal))
                score += 150;
            else if (document.NormalizedTitle.StartsWith(query, StringComparison.Ordinal))
                score += 90;
            else if (document.NormalizedTitle.Contains(query))
                score += 55;
            if (document.NormalizedBody.Contains(query))
                score += 24;

            foreach (var token in tokens)
            {
                if (ContainsWord(document.NormalizedTitle, token))
                    score += 18;
                else if (document.NormalizedTitle.Contains(token))
                    score += 8;
                var bodyOccurrences = CountOccurrences(document.NormalizedBody, token, 8);
                score += bodyOccurrences * 2;
            }
            return score;
        }

        private static bool ContainsWord(string text, string word)
        {
            return Tokenize(text).Contains(word);
        }

        private static int CountOccurrences(string text, string value, int maximum)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
                return 0;
            var count = 0;
            var index = 0;
            while (count < maximum && (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

        private static ISet<string> Tokenize(string value)
        {
            return new HashSet<string>(WordRegex.Matches(value ?? "")
                .Cast<Match>()
                .Select(match => match.Value), StringComparer.Ordinal);
        }

        private static string NormalizeSearchText(string value)
        {
            return WhiteSpaceRegex.Replace((value ?? "").Normalize(NormalizationForm.FormKC), " ")
                .Trim()
                .ToLowerInvariant();
        }

        private static string GetMemberKind(char prefix)
        {
            switch (char.ToUpperInvariant(prefix))
            {
                case 'T': return "object";
                case 'M': return "method";
                case 'P': return "property";
                case 'K': return "keyword";
                default: return "documentation";
            }
        }

        private static bool IsBpExample(string path)
        {
            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".bp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bpi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bpm", StringComparison.OrdinalIgnoreCase);
        }

        private static string SelectHelpDirectory(string helpRoot, string language)
        {
            if (string.IsNullOrWhiteSpace(helpRoot))
                return "";
            var selected = Path.Combine(helpRoot, language);
            if (HasHelpFiles(selected))
                return selected;
            var english = Path.Combine(helpRoot, "en");
            return HasHelpFiles(english) ? english : selected;
        }

        private static bool HasHelpFiles(string directory)
        {
            try
            {
                return Directory.Exists(directory) &&
                    (Directory.EnumerateFiles(directory, "*.xml").Any() || File.Exists(Path.Combine(directory, "Operators.txt")));
            }
            catch
            {
                return false;
            }
        }

        private static string HelpSourcePath(string helpDirectory, string fileName)
        {
            var language = new DirectoryInfo(helpDirectory).Name;
            return "Help/" + language + "/" + fileName;
        }

        private static string GetBaseDirectory()
        {
            try
            {
                var assembly = System.Windows.Application.ResourceAssembly ?? Assembly.GetExecutingAssembly();
                var location = assembly.Location;
                if (!string.IsNullOrWhiteSpace(location))
                    return Path.GetDirectoryName(location);
            }
            catch
            {
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static string NormalizeLanguage(string language)
        {
            var value = (language ?? "en").Trim().ToLowerInvariant();
            return value == "ru" || value == "ua" || value == "en" ? value : "en";
        }

        private static string NormalizeDirectory(string path)
        {
            try
            {
                return Path.GetFullPath(path ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path ?? "";
            }
        }

        private static string NormalizeFile(string path)
        {
            try
            {
                return Path.GetFullPath(path ?? "");
            }
            catch
            {
                return path ?? "";
            }
        }

        private static string GetRelativePath(string root, string path)
        {
            var normalizedRoot = NormalizeDirectory(root) + Path.DirectorySeparatorChar;
            var normalizedPath = NormalizeFile(path);
            if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return normalizedPath.Substring(normalizedRoot.Length);
            return Path.GetFileName(path);
        }

        private static string ElementValue(XElement element)
        {
            return element == null ? "" : element.Value ?? "";
        }

        private static string CompactText(string value)
        {
            return WhiteSpaceRegex.Replace(value ?? "", " ").Trim();
        }

        private static string NormalizeLines(string value)
        {
            return (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static string FirstParagraph(string value)
        {
            var normalized = NormalizeLines(value).Trim();
            var split = normalized.IndexOf("\n\n", StringComparison.Ordinal);
            return CompactText(split >= 0 ? normalized.Substring(0, split) : normalized);
        }

        private static string Slug(string value)
        {
            var builder = new StringBuilder();
            var dash = false;
            foreach (var character in (value ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character) || character == '_')
                {
                    builder.Append(character);
                    dash = false;
                }
                else if (!dash && builder.Length > 0)
                {
                    builder.Append('-');
                    dash = true;
                }
            }
            return builder.ToString().Trim('-');
        }

        private static string StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in value ?? "")
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        private static string Limit(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximum)
                return value ?? "";
            return value.Substring(0, maximum).TrimEnd() + "…";
        }

        private static DocumentationTopic Clone(DocumentationTopic topic)
        {
            return new DocumentationTopic
            {
                Id = topic.Id,
                Title = topic.Title,
                Markdown = topic.Markdown,
                SourcePath = topic.SourcePath
            };
        }

        private static Task<OperationResult<T>> Cancelled<T>()
        {
            return Result(OperationResult<T>.Fail("cancelled", "The documentation operation was cancelled."));
        }

        private static Task<OperationResult<T>> Result<T>(OperationResult<T> result)
        {
            return Task.FromResult(result);
        }

        private sealed class DocumentationCatalog
        {
            internal DocumentationCatalog()
            {
                Topics = new Dictionary<string, DocumentationTopic>(StringComparer.Ordinal);
                SearchDocuments = new List<SearchDocument>();
                Errors = new List<string>();
                CompactGuide = "";
                Error = "";
            }

            internal IDictionary<string, DocumentationTopic> Topics { get; private set; }
            internal IList<SearchDocument> SearchDocuments { get; private set; }
            internal IList<string> Errors { get; private set; }
            internal string CompactGuide { get; set; }
            internal string Error { get; set; }
        }

        private sealed class SearchDocument
        {
            internal DocumentationTopic Topic { get; set; }
            internal string Kind { get; set; }
            internal string Summary { get; set; }
            internal string NormalizedTitle { get; set; }
            internal string NormalizedBody { get; set; }
        }
    }
}
