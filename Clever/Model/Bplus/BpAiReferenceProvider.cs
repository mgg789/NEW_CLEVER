using Clever.Model.Bplus.BPInterpreter;
using Clever.Model.Intellisense;
using Clever.Model.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Clever.Model.Bplus
{
    internal sealed class BpAiReferenceProvider
    {
        private const int LanguageReferenceLimit = 8000;
        private const int LinkedContextLimit = 12000;
        private const int SummaryLimit = 220;
        private const int SourceSectionLimit = 2600;
        private static readonly Regex IdentifierRegex = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
        private static readonly Regex ObjectAccessRegex = new Regex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\.", RegexOptions.Compiled);
        private static readonly Regex BlockStartRegex = new Regex(
            @"^\s*(?:private\s+)?(?:Sub|Function)\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BlockEndRegex = new Regex(
            @"^\s*End(?:Sub|Function)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DeclarationRegex = new Regex(
            @"^\s*(?:private\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _helpDirectoryOverride;
        private readonly object _catalogLock = new object();
        private HelpCatalog _catalog;
        private string _catalogPath = "";

        internal BpAiReferenceProvider(string helpDirectoryOverride = null)
        {
            _helpDirectoryOverride = helpDirectoryOverride;
        }

        internal string BuildLanguageReference(string prefix, string suffix)
        {
            var catalog = GetCatalog();
            var builder = new StringBuilder(LanguageReferenceLimit);
            AppendLimited(builder, "CLEV3R BP runtime reference (English).\n", LanguageReferenceLimit);

            if (!string.IsNullOrWhiteSpace(catalog.Operators))
            {
                AppendLimited(builder, "\nOPERATORS\n", LanguageReferenceLimit);
                AppendLimited(builder, catalog.Operators, LanguageReferenceLimit);
                AppendLimited(builder, "\n", LanguageReferenceLimit);
            }

            var nearbyObjects = FindNearbyObjects(prefix, suffix, catalog.Objects.Keys);
            foreach (var objectName in nearbyObjects)
            {
                HelpObject helpObject;
                if (!catalog.Objects.TryGetValue(objectName, out helpObject))
                    continue;

                var section = FormatHelpObject(helpObject);
                if (string.IsNullOrWhiteSpace(section))
                    continue;
                if (!AppendLimited(builder, "\n" + Limit(section, 2400), LanguageReferenceLimit))
                    break;
            }

            if (catalog.Keywords.Count > 0)
            {
                AppendLimited(builder, "\nKEYWORDS\n", LanguageReferenceLimit);
                foreach (var keyword in catalog.Keywords)
                {
                    if (!AppendLimited(builder, keyword + "\n", LanguageReferenceLimit))
                        break;
                }
            }

            return Limit(builder.ToString(), LanguageReferenceLimit);
        }

        internal string BuildLinkedContext(TextEditor editor, string prefix, string suffix)
        {
            if (editor == null)
                return "";

            ProgramData currentData;
            ProgramData[] openPrograms;
            if (!TryGetProgramSnapshot(editor, out currentData, out openPrograms) || currentData.Map == null)
                return "";

            var symbols = FindRelevantSymbols(prefix, suffix);
            var accessedObjects = FindObjectAccesses(prefix, suffix);
            var linkedFiles = GetDirectLinkedFiles(currentData.Map);
            var builder = new StringBuilder(LinkedContextLimit);

            foreach (var linkedFile in linkedFiles)
            {
                if (builder.Length >= LinkedContextLimit)
                    break;

                var source = ReadLinkedSource(linkedFile, openPrograms);
                if (string.IsNullOrWhiteSpace(source))
                    continue;

                var moduleName = Path.GetFileNameWithoutExtension(linkedFile.Name) ?? "";
                var includeFullOutline = linkedFile.IsModule && accessedObjects.Contains(moduleName);
                var section = ExtractRelevantSections(source, symbols, includeFullOutline);
                if (string.IsNullOrWhiteSpace(section))
                    continue;

                AppendLimited(builder, "FILE " + linkedFile.Name + "\n", LinkedContextLimit);
                AppendLimited(builder, section.Trim() + "\n\n", LinkedContextLimit);
            }

            return Limit(builder.ToString().TrimEnd(), LinkedContextLimit);
        }

        private HelpCatalog GetCatalog()
        {
            var helpDirectory = GetHelpDirectory();
            lock (_catalogLock)
            {
                if (_catalog != null && string.Equals(_catalogPath, helpDirectory, StringComparison.OrdinalIgnoreCase))
                    return _catalog;

                _catalog = LoadCatalog(helpDirectory);
                _catalogPath = helpDirectory;
                return _catalog;
            }
        }

        private string GetHelpDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_helpDirectoryOverride))
                return _helpDirectoryOverride;

            var assembly = System.Windows.Application.ResourceAssembly ?? Assembly.GetExecutingAssembly();
            var baseDirectory = Path.GetDirectoryName(assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDirectory, "Help", "en");
        }

        private static HelpCatalog LoadCatalog(string helpDirectory)
        {
            var catalog = new HelpCatalog();
            if (string.IsNullOrWhiteSpace(helpDirectory) || !Directory.Exists(helpDirectory))
                return catalog;

            try
            {
                var operatorsPath = Path.Combine(helpDirectory, "Operators.txt");
                if (File.Exists(operatorsPath))
                    catalog.Operators = CompactOperators(File.ReadAllText(operatorsPath));
            }
            catch
            {
                catalog.Operators = "";
            }

            string[] xmlFiles;
            try
            {
                xmlFiles = Directory.GetFiles(helpDirectory, "*.xml", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return catalog;
            }

            foreach (var path in xmlFiles)
            {
                try
                {
                    var document = XDocument.Load(path, LoadOptions.None);
                    var members = document.Descendants("member").ToList();
                    if (string.Equals(Path.GetFileName(path), "Keywords.xml", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var member in members)
                        {
                            var name = (string)member.Attribute("name") ?? "";
                            if (!name.StartsWith("K:", StringComparison.OrdinalIgnoreCase))
                                continue;
                            var summary = CompactText(member.Element("summary") == null ? "" : member.Element("summary").Value);
                            catalog.Keywords.Add(name.Substring(2) + ": " + Limit(summary, SummaryLimit));
                        }
                        continue;
                    }

                    var objectName = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrWhiteSpace(objectName))
                        continue;

                    var helpObject = new HelpObject { Name = objectName };
                    foreach (var member in members)
                    {
                        var memberName = (string)member.Attribute("name") ?? "";
                        var summary = CompactText(member.Element("summary") == null ? "" : member.Element("summary").Value);
                        if (memberName.StartsWith("T:", StringComparison.OrdinalIgnoreCase))
                        {
                            helpObject.Summary = Limit(summary, SummaryLimit);
                            continue;
                        }

                        if (!memberName.StartsWith("M:", StringComparison.OrdinalIgnoreCase) &&
                            !memberName.StartsWith("P:", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var parameters = member.Elements("param")
                            .Select(item => (string)item.Attribute("name"))
                            .Where(item => !string.IsNullOrWhiteSpace(item));
                        var signature = memberName.Substring(2) + "(" + string.Join(", ", parameters) + ")";
                        helpObject.Members.Add(signature + ": " + Limit(summary, SummaryLimit));
                    }

                    if (helpObject.Members.Count > 0 || !string.IsNullOrWhiteSpace(helpObject.Summary))
                        catalog.Objects[objectName] = helpObject;
                }
                catch
                {
                }
            }

            return catalog;
        }

        private static string FormatHelpObject(HelpObject helpObject)
        {
            var builder = new StringBuilder();
            builder.Append("OBJECT ").Append(helpObject.Name);
            if (!string.IsNullOrWhiteSpace(helpObject.Summary))
                builder.Append(": ").Append(helpObject.Summary);
            builder.Append('\n');
            foreach (var member in helpObject.Members)
                builder.Append(helpObject.Name).Append('.').Append(member).Append('\n');
            return builder.ToString();
        }

        private static IList<string> FindNearbyObjects(string prefix, string suffix, ICollection<string> objectNames)
        {
            var result = new List<string>();
            var knownObjects = new HashSet<string>(objectNames, StringComparer.OrdinalIgnoreCase);
            var context = Tail(prefix, 4000) + Head(suffix, 1000);
            var accessMatches = ObjectAccessRegex.Matches(context);
            for (var index = accessMatches.Count - 1; index >= 0; index--)
            {
                var name = accessMatches[index].Groups[1].Value;
                if (knownObjects.Contains(name) && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                    result.Add(knownObjects.First(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)));
            }

            var identifiers = IdentifierRegex.Matches(context);
            for (var index = identifiers.Count - 1; index >= 0 && result.Count < 6; index--)
            {
                var name = identifiers[index].Value;
                if (knownObjects.Contains(name) && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                    result.Add(knownObjects.First(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)));
            }

            return result.Take(6).ToList();
        }

        private static bool TryGetProgramSnapshot(TextEditor editor, out ProgramData currentData, out ProgramData[] programs)
        {
            currentData = null;
            programs = new ProgramData[0];
            try
            {
                var data = IntellisenseParser.Data;
                if (data == null)
                    return false;

                programs = data.Values.Where(item => item != null).Distinct().ToArray();
                currentData = programs.FirstOrDefault(item => ReferenceEquals(item.Editor, editor));
                if (currentData == null)
                {
                    currentData = programs.FirstOrDefault(item =>
                        string.Equals(item.Name, editor.ProgramName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(item.ParseName, editor.ProgramName, StringComparison.OrdinalIgnoreCase));
                }
                return currentData != null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static IList<LinkedFile> GetDirectLinkedFiles(ProgramMap map)
        {
            var files = new List<LinkedFile>();
            try
            {
                foreach (var include in map.Includes.Values)
                {
                    files.Add(new LinkedFile
                    {
                        Name = include.OriginName,
                        FullPath = Path.Combine(include.Path ?? "", include.OriginName ?? ""),
                        IsModule = false
                    });
                }

                foreach (var module in map.Imports.Values)
                {
                    files.Add(new LinkedFile
                    {
                        Name = module.OriginName,
                        FullPath = Path.Combine(module.Path ?? "", module.OriginName ?? ""),
                        IsModule = true
                    });
                }
            }
            catch (InvalidOperationException)
            {
            }

            return files
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static string ReadLinkedSource(LinkedFile linkedFile, IEnumerable<ProgramData> openPrograms)
        {
            var openProgram = openPrograms.FirstOrDefault(item =>
                item.Editor != null &&
                (string.Equals(item.Name, linkedFile.Name, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.ParseName, linkedFile.Name, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(item.FullPath) &&
                  string.Equals(NormalizePath(item.FullPath), NormalizePath(linkedFile.FullPath), StringComparison.OrdinalIgnoreCase))));
            if (openProgram != null && openProgram.Editor != null && openProgram.Editor.TextArea != null)
                return openProgram.Editor.TextArea.Text ?? "";

            try
            {
                return File.Exists(linkedFile.FullPath) ? File.ReadAllText(linkedFile.FullPath) : "";
            }
            catch
            {
                return "";
            }
        }

        private static string ExtractRelevantSections(string source, ISet<string> relevantSymbols, bool includeFullOutline)
        {
            var lines = NormalizeLines(source).Split('\n');
            var builder = new StringBuilder();
            var blocks = FindBlocks(lines);
            var includedBlocks = new HashSet<int>();

            for (var index = 0; index < blocks.Count; index++)
            {
                var block = blocks[index];
                if (relevantSymbols.Contains(block.Name))
                {
                    AppendSourceSection(builder, lines, block.Start, block.End, SourceSectionLimit);
                    includedBlocks.Add(index);
                }
            }

            var declarationCount = 0;
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (blocks.Any(block => lineIndex >= block.Start && lineIndex <= block.End))
                    continue;

                var declaration = DeclarationRegex.Match(lines[lineIndex]);
                if (!declaration.Success)
                    continue;
                var name = declaration.Groups[1].Value;
                if (relevantSymbols.Contains(name) || (includeFullOutline && declarationCount < 40))
                {
                    builder.AppendLine(lines[lineIndex].TrimEnd());
                    declarationCount++;
                }
            }

            var hasRelevantContent = builder.Length > 0 || declarationCount > 0;
            var outlineCount = 0;
            for (var index = 0; index < blocks.Count; index++)
            {
                if (includedBlocks.Contains(index))
                    continue;
                if (hasRelevantContent && !includeFullOutline)
                    continue;
                if (outlineCount >= 40)
                    break;
                builder.AppendLine(lines[blocks[index].Start].TrimEnd());
                outlineCount++;
            }

            return Limit(builder.ToString(), LinkedContextLimit);
        }

        private static IList<SourceBlock> FindBlocks(string[] lines)
        {
            var blocks = new List<SourceBlock>();
            for (var index = 0; index < lines.Length; index++)
            {
                var start = BlockStartRegex.Match(lines[index]);
                if (!start.Success)
                    continue;

                var end = index;
                while (end + 1 < lines.Length)
                {
                    end++;
                    if (BlockEndRegex.IsMatch(lines[end]))
                        break;
                }
                blocks.Add(new SourceBlock { Name = start.Groups[1].Value, Start = index, End = end });
                index = end;
            }
            return blocks;
        }

        private static void AppendSourceSection(StringBuilder builder, string[] lines, int start, int end, int limit)
        {
            var section = new StringBuilder();
            for (var index = start; index <= end && index < lines.Length; index++)
            {
                var line = lines[index].TrimEnd();
                if (section.Length + line.Length + 1 > limit)
                    break;
                section.AppendLine(line);
            }
            builder.Append(section);
        }

        private static ISet<string> FindRelevantSymbols(string prefix, string suffix)
        {
            var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var context = Tail(prefix, 5000) + Head(suffix, 1200);
            foreach (Match match in IdentifierRegex.Matches(context))
            {
                if (match.Value.Length > 1)
                    symbols.Add(match.Value);
            }
            return symbols;
        }

        private static ISet<string> FindObjectAccesses(string prefix, string suffix)
        {
            var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var context = Tail(prefix, 5000) + Head(suffix, 1200);
            foreach (Match match in ObjectAccessRegex.Matches(context))
                objects.Add(match.Groups[1].Value);
            return objects;
        }

        private static string CompactOperators(string text)
        {
            var lines = NormalizeLines(text).Split('\n');
            return string.Join("\n", lines.Select(line => line.Trim()).Where(line => line.Length > 0));
        }

        private static string CompactText(string text)
        {
            return Regex.Replace(text ?? "", @"\s+", " ").Trim();
        }

        private static string NormalizeLines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static string NormalizePath(string path)
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

        private static string Tail(string value, int length)
        {
            value = value ?? "";
            return value.Length <= length ? value : value.Substring(value.Length - length);
        }

        private static string Head(string value, int length)
        {
            value = value ?? "";
            return value.Length <= length ? value : value.Substring(0, length);
        }

        private static string Limit(string value, int length)
        {
            value = value ?? "";
            return value.Length <= length ? value : value.Substring(0, length);
        }

        private static bool AppendLimited(StringBuilder builder, string value, int limit)
        {
            if (builder.Length >= limit)
                return false;

            value = value ?? "";
            var remaining = limit - builder.Length;
            builder.Append(value.Length <= remaining ? value : value.Substring(0, remaining));
            return value.Length <= remaining;
        }

        private sealed class HelpCatalog
        {
            internal string Operators = "";
            internal readonly IList<string> Keywords = new List<string>();
            internal readonly IDictionary<string, HelpObject> Objects =
                new Dictionary<string, HelpObject>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class HelpObject
        {
            internal string Name = "";
            internal string Summary = "";
            internal readonly IList<string> Members = new List<string>();
        }

        private sealed class LinkedFile
        {
            internal string Name = "";
            internal string FullPath = "";
            internal bool IsModule;
        }

        private sealed class SourceBlock
        {
            internal string Name = "";
            internal int Start;
            internal int End;
        }
    }
}
