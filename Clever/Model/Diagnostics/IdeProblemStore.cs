using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Clever.Model.Diagnostics
{
    internal enum IdeProblemSource
    {
        Live,
        Preprocessor,
        Compiler,
        Assembler,
        Ide
    }

    internal sealed class IdeProblem
    {
        internal string Id { get; set; }
        internal IdeProblemSource Source { get; set; }
        internal string ScopeKey { get; set; }
        internal bool IsError { get; set; }
        internal string FilePath { get; set; }
        internal int Start { get; set; }
        internal int Length { get; set; }
        internal int Line { get; set; }
        internal int Column { get; set; }
        internal string Code { get; set; }
        internal string Message { get; set; }
        internal bool IsFixCandidate { get; set; }
        internal bool IsMappedToSource { get; set; }

        internal IdeProblem Clone()
        {
            return (IdeProblem)MemberwiseClone();
        }
    }

    /// <summary>
    /// Holds structured diagnostics independently from the text status log. Live entries are
    /// replaced atomically per document revision; compilation and IDE entries remain until their
    /// producer explicitly reports a successful retry.
    /// </summary>
    internal static class IdeProblemStore
    {
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, IdeProblem> Problems =
            new Dictionary<string, IdeProblem>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LiveRevisions =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static int NextLiveRevision;

        internal static event EventHandler Changed;

        internal static int CreateLiveRevision()
        {
            return Interlocked.Increment(ref NextLiveRevision);
        }

        internal static IList<IdeProblem> GetSnapshot()
        {
            lock (SyncRoot)
            {
                return Problems.Values
                    .Select(problem => problem.Clone())
                    .OrderBy(problem => problem.FilePath ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(problem => problem.Start)
                    .ThenBy(problem => problem.Message ?? "", StringComparer.Ordinal)
                    .ToList();
            }
        }

        internal static bool HasActiveProblems
        {
            get
            {
                lock (SyncRoot)
                    return Problems.Count > 0;
            }
        }

        internal static void ReplaceLive(string filePath, int revision, IEnumerable<IdeProblem> diagnostics)
        {
            filePath = NormalizeFileKey(filePath);
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            lock (SyncRoot)
            {
                int currentRevision;
                if (LiveRevisions.TryGetValue(filePath, out currentRevision) && revision < currentRevision)
                    return;

                LiveRevisions[filePath] = revision;
                RemoveWhere(problem => problem.Source == IdeProblemSource.Live &&
                    string.Equals(NormalizeFileKey(problem.FilePath), filePath, StringComparison.OrdinalIgnoreCase));

                foreach (var diagnostic in diagnostics ?? Enumerable.Empty<IdeProblem>())
                {
                    if (diagnostic == null)
                        continue;
                    var copy = diagnostic.Clone();
                    copy.Source = IdeProblemSource.Live;
                    copy.FilePath = string.IsNullOrWhiteSpace(copy.FilePath) ? filePath : copy.FilePath;
                    copy.Id = EnsureId(copy);
                    Problems[copy.Id] = copy;
                }
            }

            RaiseChanged();
        }

        internal static void ReplacePersistent(IdeProblemSource source, string scopeKey, IEnumerable<IdeProblem> diagnostics)
        {
            var normalizedScope = NormalizeFileKey(scopeKey);
            lock (SyncRoot)
            {
                RemoveWhere(problem => problem.Source == source &&
                    string.Equals(problem.ScopeKey ?? "", normalizedScope, StringComparison.OrdinalIgnoreCase));

                foreach (var diagnostic in diagnostics ?? Enumerable.Empty<IdeProblem>())
                {
                    if (diagnostic == null)
                        continue;
                    var copy = diagnostic.Clone();
                    copy.Source = source;
                    copy.ScopeKey = normalizedScope;
                    if (string.IsNullOrWhiteSpace(copy.FilePath))
                        copy.FilePath = normalizedScope;
                    copy.Id = EnsureId(copy);
                    Problems[copy.Id] = copy;
                }
            }

            RaiseChanged();
        }

        internal static void ReportIdeError(string operationId, string message, string filePath)
        {
            if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(message))
                return;

            var problem = new IdeProblem
            {
                Id = "ide:" + operationId,
                Source = IdeProblemSource.Ide,
                IsError = true,
                FilePath = filePath ?? "",
                Start = 0,
                Length = 0,
                Line = 0,
                Column = 0,
                Code = operationId,
                Message = message,
                IsFixCandidate = false,
                IsMappedToSource = false
            };

            lock (SyncRoot)
                Problems[problem.Id] = problem;
            RaiseChanged();
        }

        internal static void ClearIdeError(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                return;

            lock (SyncRoot)
                Problems.Remove("ide:" + operationId);
            RaiseChanged();
        }

        internal static void ClearForFile(string filePath)
        {
            var normalizedPath = NormalizeFileKey(filePath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
                return;

            lock (SyncRoot)
            {
                RemoveWhere(problem => string.Equals(NormalizeFileKey(problem.FilePath), normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
                LiveRevisions.Remove(normalizedPath);
            }
            RaiseChanged();
        }

        private static void RemoveWhere(Func<IdeProblem, bool> predicate)
        {
            var keys = Problems.Where(pair => predicate(pair.Value)).Select(pair => pair.Key).ToList();
            foreach (var key in keys)
                Problems.Remove(key);
        }

        private static string EnsureId(IdeProblem problem)
        {
            if (!string.IsNullOrWhiteSpace(problem.Id))
                return problem.Id;

            return string.Join("|", new[]
            {
                problem.Source.ToString(),
                NormalizeFileKey(problem.FilePath),
                problem.Start.ToString(),
                problem.Length.ToString(),
                problem.Code ?? "",
                problem.Message ?? ""
            });
        }

        private static string NormalizeFileKey(string value)
        {
            return (value ?? "").Trim();
        }

        private static void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null)
                handler(null, EventArgs.Empty);
        }
    }
}
