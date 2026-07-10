using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Clever.Model.Utils
{
    /// <summary>
    /// Deterministic gate between an untrusted model response and an editor document. It does not
    /// perform I/O and never mutates a supplied source snapshot. The coordinator must re-analyse
    /// the returned texts before committing them to an editor.
    /// </summary>
    internal static class AiBugFixPlanValidator
    {
        private const int MaxTotalChangedCharacters = 4096;
        private const int MaxFilesChanged = 6;
        private static readonly HashSet<string> ReservedIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and", "or", "not", "if", "then", "else", "elseif", "endif", "while", "endwhile", "for", "to", "step", "next",
            "break", "continue", "return", "function", "sub", "module", "include", "import", "folder", "in", "out",
            "number", "string", "logic", "true", "false", "null"
        };

        internal static bool TryApply(AiBugFixRequest request, AiBugFixResponse response, out AiBugFixApplyResult result)
        {
            result = new AiBugFixApplyResult();
            if (request == null)
                return Fail(result, "The repair request is missing.");
            if (response == null || !response.IsValid)
                return Fail(result, response == null ? "The AI repair response is missing." : response.ValidationError ?? "The AI repair response is invalid.");
            if (!response.ShouldApply)
                return Fail(result, string.IsNullOrWhiteSpace(response.Reason) ? "The AI model declined a safe repair." : response.Reason);

            Dictionary<string, AiBugFixFile> files;
            Dictionary<string, AiBugFixDiagnostic> diagnostics;
            string error;
            if (!TryBuildFiles(request.Files, out files, out error))
                return Fail(result, error);
            if (!TryBuildFixableDiagnostics(request.Diagnostics, out diagnostics, out error))
                return Fail(result, error);
            if (!TryValidateDiagnosticIds(response.DiagnosticIds, diagnostics, out error))
                return Fail(result, error);
            if (response.Edits == null || response.Edits.Count == 0 || response.Edits.Count > 50)
                return Fail(result, "The repair plan has an invalid number of edits.");

            var requestedDiagnostics = response.DiagnosticIds.Select(id => diagnostics[id]).ToList();
            var editsByFile = new Dictionary<string, List<AiBugFixEdit>>(StringComparer.OrdinalIgnoreCase);
            var totalChanged = 0;

            foreach (var edit in response.Edits)
            {
                AiBugFixFile file;
                if (!TryValidateEdit(edit, files, requestedDiagnostics, ref totalChanged, out file, out error))
                    return Fail(result, error);

                List<AiBugFixEdit> fileEdits;
                if (!editsByFile.TryGetValue(file.FilePath, out fileEdits))
                {
                    fileEdits = new List<AiBugFixEdit>();
                    editsByFile.Add(file.FilePath, fileEdits);
                }
                fileEdits.Add(edit);
            }

            if (editsByFile.Count > MaxFilesChanged)
                return Fail(result, "The repair plan changes too many files.");

            foreach (var item in editsByFile)
            {
                if (!HasNonOverlappingEdits(item.Value))
                    return Fail(result, "The repair plan contains overlapping edits.");
            }

            foreach (var item in editsByFile)
            {
                var file = files[item.Key];
                var original = file.Text ?? "";
                var ordered = item.Value.OrderByDescending(edit => edit.Start).ThenByDescending(edit => edit.Length).ToList();
                var updated = new StringBuilder(original);
                foreach (var edit in ordered)
                    updated.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.ReplacementText);

                result.Files.Add(new AiBugFixAppliedFile
                {
                    FilePath = file.FilePath,
                    DocumentVersion = file.DocumentVersion,
                    OriginalText = original,
                    UpdatedText = updated.ToString(),
                    Edits = ordered.OrderBy(edit => edit.Start).ToList()
                });
            }

            foreach (var id in response.DiagnosticIds)
                result.AppliedDiagnosticIds.Add(id);
            result.Success = true;
            return true;
        }

        private static bool TryBuildFiles(IList<AiBugFixFile> source, out Dictionary<string, AiBugFixFile> files, out string error)
        {
            files = new Dictionary<string, AiBugFixFile>(StringComparer.OrdinalIgnoreCase);
            error = null;
            if (source == null || source.Count == 0)
            {
                error = "The repair request does not contain source snapshots.";
                return false;
            }

            foreach (var file in source)
            {
                if (file == null || string.IsNullOrWhiteSpace(file.FilePath))
                {
                    error = "A source snapshot has no file path.";
                    return false;
                }
                if (files.ContainsKey(file.FilePath))
                {
                    error = "The repair request contains duplicate source file paths.";
                    return false;
                }
                files.Add(file.FilePath, file);
            }
            return true;
        }

        private static bool TryBuildFixableDiagnostics(IList<AiBugFixDiagnostic> source,
            out Dictionary<string, AiBugFixDiagnostic> diagnostics, out string error)
        {
            diagnostics = new Dictionary<string, AiBugFixDiagnostic>(StringComparer.Ordinal);
            error = null;
            if (source == null || source.Count == 0)
            {
                error = "The repair request does not contain diagnostics.";
                return false;
            }

            foreach (var diagnostic in source)
            {
                if (diagnostic == null || !diagnostic.IsFixable)
                    continue;
                if (string.IsNullOrWhiteSpace(diagnostic.Id) || string.IsNullOrWhiteSpace(diagnostic.FilePath)
                    || diagnostic.Start < 0 || diagnostic.Length < 0 || diagnostics.ContainsKey(diagnostic.Id))
                {
                    error = "The repair request contains an invalid fixable diagnostic.";
                    return false;
                }
                diagnostics.Add(diagnostic.Id, diagnostic);
            }

            if (diagnostics.Count == 0)
            {
                error = "The repair request has no fixable diagnostics.";
                return false;
            }
            return true;
        }

        private static bool TryValidateDiagnosticIds(IList<string> ids, IDictionary<string, AiBugFixDiagnostic> diagnostics, out string error)
        {
            error = null;
            if (ids == null || ids.Count == 0)
            {
                error = "The repair plan does not identify a diagnostic.";
                return false;
            }

            foreach (var id in ids)
            {
                if (string.IsNullOrWhiteSpace(id) || !diagnostics.ContainsKey(id))
                {
                    error = "The repair plan references a diagnostic outside the current request.";
                    return false;
                }
            }
            return true;
        }

        private static bool TryValidateEdit(AiBugFixEdit edit, IDictionary<string, AiBugFixFile> files,
            IList<AiBugFixDiagnostic> diagnostics, ref int totalChanged,
            out AiBugFixFile file, out string error)
        {
            file = null;
            error = null;
            if (edit == null || string.IsNullOrWhiteSpace(edit.FilePath) || edit.ExpectedText == null || edit.ReplacementText == null)
            {
                error = "The repair plan contains an incomplete edit.";
                return false;
            }
            if (!files.TryGetValue(edit.FilePath, out file) || !file.IsEditable || !IsBpFile(file.FilePath))
            {
                error = "The repair plan attempts to edit a file outside the editable BP scope.";
                return false;
            }

            var text = file.Text ?? "";
            if (edit.Start < 0 || edit.Length < 0 || edit.Length != edit.ExpectedText.Length
                || edit.Start > text.Length || edit.Length > text.Length - edit.Start)
            {
                error = "The repair edit offset is outside its source snapshot.";
                return false;
            }
            if (!string.Equals(text.Substring(edit.Start, edit.Length), edit.ExpectedText, StringComparison.Ordinal))
            {
                error = "The repair edit no longer matches its source snapshot.";
                return false;
            }
            if (string.Equals(edit.ExpectedText, edit.ReplacementText, StringComparison.Ordinal))
            {
                error = "The repair plan contains a no-op edit.";
                return false;
            }

            totalChanged += edit.ExpectedText.Length + edit.ReplacementText.Length;
            if (totalChanged > MaxTotalChangedCharacters)
            {
                error = "The repair plan changes too much source text.";
                return false;
            }
            if (TouchesDirectiveLine(text, edit.Start, edit.Length))
            {
                error = "The repair plan attempts to alter an include, import or folder directive.";
                return false;
            }
            if (TouchesQuotedStringOrComment(text, edit.Start, edit.Length))
            {
                error = "The repair plan attempts to alter a string literal or comment.";
                return false;
            }

            if (IsCommentInsertion(text, file.FilePath, edit, diagnostics))
                return true;
            if (IsStructuralPunctuationEdit(edit, diagnostics))
                return true;
            if (IsTargetIdentifierCorrection(edit, text, diagnostics, out error))
                return true;

            if (string.IsNullOrWhiteSpace(error))
                error = "The repair edit is outside the safe syntax-repair allow-list.";
            return false;
        }

        private static bool IsCommentInsertion(string text, string filePath, AiBugFixEdit edit, IList<AiBugFixDiagnostic> diagnostics)
        {
            if (edit.Length != 0 || edit.ExpectedText.Length != 0 || !string.Equals(edit.ReplacementText, "'", StringComparison.Ordinal))
                return false;

            var lineStart = GetLineStart(text, edit.Start);
            for (var index = lineStart; index < edit.Start; index++)
            {
                if (text[index] != ' ' && text[index] != '\t')
                    return false;
            }

            var lineEnd = GetLineEnd(text, edit.Start);
            var line = text.Substring(lineStart, lineEnd - lineStart);
            if (string.IsNullOrWhiteSpace(line) || IsDirectiveLine(line))
                return false;
            return diagnostics.Any(diagnostic => IsPlainTextDiagnostic(diagnostic)
                && string.Equals(diagnostic.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
                && diagnostic.Start >= lineStart && diagnostic.Start <= lineEnd);
        }

        private static bool IsTargetIdentifierCorrection(AiBugFixEdit edit, string text, IList<AiBugFixDiagnostic> diagnostics,
            out string error)
        {
            error = null;
            if (!IsIdentifier(edit.ExpectedText) || !IsIdentifier(edit.ReplacementText)
                || IsReservedIdentifier(edit.ExpectedText) || IsReservedIdentifier(edit.ReplacementText))
                return false;
            var diagnostic = FindExactTargetDiagnostic(edit, diagnostics);
            if (diagnostic == null ||
                (!string.Equals(diagnostic.Code, "BP3001", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(diagnostic.Code, "BP3010", StringComparison.OrdinalIgnoreCase)))
            {
                error = "An identifier replacement is permitted only for the exact unresolved identifier span.";
                return false;
            }
            if (!HasIdentifierBoundary(text, edit.Start, edit.Length))
            {
                error = "An identifier replacement must cover one complete identifier.";
                return false;
            }

            return true;
        }

        private static bool IsStructuralPunctuationEdit(AiBugFixEdit edit, IList<AiBugFixDiagnostic> diagnostics)
        {
            var diagnostic = FindExactTargetDiagnostic(edit, diagnostics);
            if (diagnostic == null ||
                (!string.Equals(diagnostic.Code, "BP2100", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(diagnostic.Code, "BP2101", StringComparison.OrdinalIgnoreCase)))
                return false;

            return IsBracketOnly(edit.ExpectedText) && IsBracketOnly(edit.ReplacementText);
        }

        private static AiBugFixDiagnostic FindExactTargetDiagnostic(AiBugFixEdit edit, IEnumerable<AiBugFixDiagnostic> diagnostics)
        {
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic == null || !string.Equals(diagnostic.FilePath, edit.FilePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (edit.Length > 0 && edit.Start == diagnostic.Start && edit.Length == diagnostic.Length)
                    return diagnostic;
                if (edit.Length == 0 && (edit.Start == diagnostic.Start || edit.Start == diagnostic.Start + diagnostic.Length))
                    return diagnostic;
            }
            return null;
        }

        private static bool IsPlainTextDiagnostic(AiBugFixDiagnostic diagnostic)
        {
            if (diagnostic != null && string.Equals(diagnostic.Code, "BP3004", StringComparison.OrdinalIgnoreCase))
                return true;
            var text = ((diagnostic == null ? "" : diagnostic.Code ?? "") + " "
                + (diagnostic == null ? "" : diagnostic.Message ?? "")).ToLowerInvariant();
            return text.Contains("plain") || text.Contains("unrecognized") || text.Contains("uninterpreted")
                || text.Contains("not code") || text.Contains("invalid text") || text.Contains("random text")
                || text.Contains("не распозн") || text.Contains("некорректный текст") || text.Contains("не программа");
        }

        private static bool IsBracketOnly(string value)
        {
            if (value == null)
                return false;
            foreach (var character in value)
            {
                if (character != '(' && character != ')' && character != '[' && character != ']')
                    return false;
            }
            return true;
        }

        private static bool IsIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || !IsIdentifierStart(value[0]))
                return false;
            for (var index = 1; index < value.Length; index++)
            {
                if (!IsIdentifierPart(value[index]))
                    return false;
            }
            return true;
        }

        private static bool HasIdentifierBoundary(string text, int start, int length)
        {
            var before = start > 0 ? text[start - 1] : '\0';
            var afterIndex = start + length;
            var after = afterIndex < text.Length ? text[afterIndex] : '\0';
            return !IsIdentifierPart(before) && !IsIdentifierPart(after);
        }

        private static bool IsIdentifierStart(char value)
        {
            return value == '_' || (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
        }

        private static bool IsIdentifierPart(char value)
        {
            return IsIdentifierStart(value) || (value >= '0' && value <= '9');
        }

        private static bool IsReservedIdentifier(string value)
        {
            return ReservedIdentifiers.Contains(value ?? "");
        }

        private static bool IsBpFile(string filePath)
        {
            return filePath.EndsWith(".bp", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".bpi", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".bpm", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasNonOverlappingEdits(IList<AiBugFixEdit> edits)
        {
            var ordered = edits.OrderBy(edit => edit.Start).ThenBy(edit => edit.Length).ToList();
            for (var index = 1; index < ordered.Count; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                var previousEnd = previous.Start + previous.Length;
                if (current.Start < previousEnd || (current.Start == previous.Start && (current.Length == 0 || previous.Length == 0)))
                    return false;
            }
            return true;
        }

        private static bool TouchesDirectiveLine(string text, int start, int length)
        {
            var end = Math.Min(text.Length, start + Math.Max(1, length));
            var lineStart = GetLineStart(text, start);
            while (lineStart < text.Length && lineStart <= end)
            {
                var lineEnd = GetLineEnd(text, lineStart);
                if (IsDirectiveLine(text.Substring(lineStart, lineEnd - lineStart)))
                    return true;
                var nextLineStart = lineEnd;
                while (nextLineStart < text.Length && (text[nextLineStart] == '\r' || text[nextLineStart] == '\n'))
                    nextLineStart++;
                if (nextLineStart >= text.Length || nextLineStart > end)
                    break;
                lineStart = nextLineStart;
            }
            return false;
        }

        private static bool IsDirectiveLine(string line)
        {
            var trimmed = (line ?? "").TrimStart();
            return StartsWithKeyword(trimmed, "include") || StartsWithKeyword(trimmed, "import") || StartsWithKeyword(trimmed, "folder");
        }

        private static bool StartsWithKeyword(string text, string keyword)
        {
            if (!text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                return false;
            return text.Length == keyword.Length || char.IsWhiteSpace(text[keyword.Length]) || text[keyword.Length] == '\"';
        }

        private static bool TouchesQuotedStringOrComment(string text, int start, int length)
        {
            var end = start + Math.Max(1, length);
            for (var position = start; position < end; position++)
            {
                if (IsInsideQuotedStringOrComment(text, position))
                    return true;
            }
            return false;
        }

        private static bool IsInsideQuotedStringOrComment(string text, int position)
        {
            var lineStart = GetLineStart(text, position);
            var inString = false;
            for (var index = lineStart; index < position; index++)
            {
                if (text[index] == '\'')
                    return true;
                if (text[index] != '\"')
                    continue;
                if (inString && index + 1 < position && text[index + 1] == '\"')
                {
                    index++;
                    continue;
                }
                inString = !inString;
            }
            return inString;
        }

        private static int GetLineStart(string text, int position)
        {
            position = Math.Max(0, Math.Min(position, text.Length));
            while (position > 0 && text[position - 1] != '\n')
                position--;
            return position;
        }

        private static int GetLineEnd(string text, int position)
        {
            position = Math.Max(0, Math.Min(position, text.Length));
            while (position < text.Length && text[position] != '\n')
                position++;
            if (position > 0 && text[position - 1] == '\r')
                position--;
            return position;
        }

        private static bool Fail(AiBugFixApplyResult result, string reason)
        {
            result.Success = false;
            result.FailureReason = reason;
            result.Files.Clear();
            result.AppliedDiagnosticIds.Clear();
            return false;
        }
    }
}
