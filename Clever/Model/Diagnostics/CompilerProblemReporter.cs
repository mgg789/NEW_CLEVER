using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Clever.Model.Diagnostics
{
    /// <summary>
    /// Converts the legacy compiler string list into persistent structured problems. Compiler
    /// messages that cannot be mapped to BP source intentionally do not receive an underline.
    /// </summary>
    internal static class CompilerProblemReporter
    {
        private static readonly Regex InterpreterError = new Regex(
            @"file:\s*(?<file>.*?)\s+line:\s*(?<line>\d+)\s*\|\s*code:\s*(?<code>\d+)\s*===>\s*(?<message>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static void Replace(IdeProblemSource source, string scopePath, IEnumerable<string> errors)
        {
            var problems = new List<IdeProblem>();
            var index = 0;
            foreach (var error in errors ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(error))
                    continue;
                problems.Add(Parse(source, scopePath, error, index++));
            }
            IdeProblemStore.ReplacePersistent(source, scopePath, problems);
        }

        internal static void Clear(string scopePath)
        {
            IdeProblemStore.ReplacePersistent(IdeProblemSource.Preprocessor, scopePath, new IdeProblem[0]);
            IdeProblemStore.ReplacePersistent(IdeProblemSource.Compiler, scopePath, new IdeProblem[0]);
            IdeProblemStore.ReplacePersistent(IdeProblemSource.Assembler, scopePath, new IdeProblem[0]);
        }

        private static IdeProblem Parse(IdeProblemSource source, string scopePath, string error, int index)
        {
            var match = InterpreterError.Match(error);
            if (!match.Success)
            {
                return new IdeProblem
                {
                    Id = source.ToString().ToLowerInvariant() + ":" + scopePath + ":" + index,
                    Source = source,
                    IsError = true,
                    FilePath = scopePath ?? "",
                    Start = 0,
                    Length = 0,
                    Line = 0,
                    Column = 0,
                    Code = source.ToString(),
                    Message = error,
                    IsFixCandidate = false,
                    IsMappedToSource = false
                };
            }

            int line;
            int.TryParse(match.Groups["line"].Value, out line);
            return new IdeProblem
            {
                Id = source.ToString().ToLowerInvariant() + ":" + scopePath + ":" + index,
                Source = source,
                IsError = true,
                FilePath = match.Groups["file"].Value.Trim(),
                Start = 0,
                Length = 0,
                Line = line,
                Column = 0,
                Code = match.Groups["code"].Value,
                Message = match.Groups["message"].Value.Trim(),
                IsFixCandidate = source == IdeProblemSource.Preprocessor,
                IsMappedToSource = false
            };
        }
    }
}
