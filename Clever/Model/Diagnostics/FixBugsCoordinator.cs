using Clever.Model.Bplus;
using Clever.Model.Utils;
using Clever.View.Dialogs;
using Clever.ViewModel;
using Interpreter.LiveDiagnostics;
using ScintillaNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Clever.Model.Diagnostics
{
    /// <summary>
    /// Orchestrates the user-confirmed Fix bugs workflow.  It owns the allowed workspace
    /// boundary and applies only already validated in-memory text results to open editor buffers.
    /// The AI service cannot open files, invoke tools, or persist any change.
    /// </summary>
    internal static class FixBugsCoordinator
    {
        private const int MaxAttempts = 50;
        private static readonly Regex DependencyRegex = new Regex(
            @"^\s*(include|import)\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static bool _isRunning;

        internal static void Open()
        {
            if (_isRunning)
                return;

            var initial = IdeProblemStore.GetSnapshot();
            if (initial.Count == 0)
                return;

            var window = new FixBugsWindow(initial.Select(ToDialogItem));
            if (Application.Current != null && Application.Current.MainWindow != null)
                window.Owner = Application.Current.MainWindow;
            window.FixAllRequested += async (sender, args) => await RunAsync(window, initial);
            window.ShowDialog();
        }

        private static async Task RunAsync(FixBugsWindow window, IList<IdeProblem> initialProblems)
        {
            if (_isRunning)
                return;
            _isRunning = true;
            window.BeginProgress();
            var stopwatch = Stopwatch.StartNew();
            var unresolved = new Dictionary<string, FixBugsDialogItem>(StringComparer.Ordinal);
            var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fixedCount = 0;
            var seenStates = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                foreach (var problem in initialProblems.Where(problem => problem != null &&
                    (!problem.IsFixCandidate || problem.Source != IdeProblemSource.Live)))
                    AddUnresolved(unresolved, problem, GetNonFixableReason(problem));

                for (var attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    WorkspaceScope scope;
                    string scopeError;
                    if (!TryBuildScope(out scope, out scopeError))
                    {
                        AddUnresolved(unresolved, new IdeProblem { Id = "scope", Message = scopeError, Code = "Scope" }, scopeError);
                        break;
                    }

                    var before = BpLiveAnalyzer.Analyze(scope.Snapshot);
                    PublishLiveAnalysis(before, scope);
                    var target = ChooseNextDiagnostic(before, scope, seenStates);
                    if (target == null)
                        break;

                    var state = target.Id + "|" + GetTextHash(scope.GetText(target.FilePath));
                    seenStates.Add(state);
                    var request = BuildRequest(scope, target);
                    AiBugFixResponse response;
                    using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
                    {
                        try
                        {
                            response = await OpenAiTextGenerationService.Instance.GenerateBugFixAsync(request, cancellation.Token);
                        }
                        catch (Exception ex)
                        {
                            AddUnresolved(unresolved, ToIdeProblem(target), "AI не вернул безопасный план: " + ex.Message);
                            continue;
                        }
                    }

                    AiBugFixApplyResult simulated;
                    if (!AiBugFixPlanValidator.TryApply(request, response, out simulated))
                    {
                        AddUnresolved(unresolved, ToIdeProblem(target), simulated.FailureReason);
                        continue;
                    }

                    var candidate = scope.WithSimulatedChanges(simulated);
                    var after = BpLiveAnalyzer.Analyze(candidate.Snapshot);
                    if (!IsSafeProgress(before, after, target))
                    {
                        AddUnresolved(unresolved, ToIdeProblem(target), "Предложенная правка не устраняет целевую ошибку без новых красных ошибок.");
                        continue;
                    }

                    string applyError;
                    if (!TryApplyToEditors(simulated, out applyError))
                    {
                        AddUnresolved(unresolved, ToIdeProblem(target), applyError);
                        continue;
                    }

                    fixedCount += simulated.AppliedDiagnosticIds.Count;
                    foreach (var changedFile in simulated.Files)
                        changedPaths.Add(NormalizePath(changedFile.FilePath));
                    scope = candidate;
                    PublishLiveAnalysis(after, scope);
                    RequestLiveRefresh(scope);
                }

                WorkspaceScope finalScope;
                string finalScopeError;
                if (TryBuildScope(out finalScope, out finalScopeError))
                {
                    var finalResult = BpLiveAnalyzer.Analyze(finalScope.Snapshot);
                    PublishLiveAnalysis(finalResult, finalScope);
                    foreach (var diagnostic in finalResult.Diagnostics.Where(item => item.IsFixable))
                        AddUnresolved(unresolved, ToIdeProblem(diagnostic), "Безопасная точечная правка не найдена.");

                    if (finalResult.Diagnostics.Count == 0)
                        FormatChangedEditors(changedPaths);
                }
                else
                {
                    AddUnresolved(unresolved, new IdeProblem { Id = "scope-final", Message = finalScopeError, Code = "Scope" }, finalScopeError);
                }
            }
            finally
            {
                stopwatch.Stop();
                _isRunning = false;
                window.ShowReport(fixedCount, stopwatch.Elapsed, unresolved.Values);
            }
        }

        private static AiBugFixRequest BuildRequest(WorkspaceScope scope, BpDiagnostic target)
        {
            var request = new AiBugFixRequest();
            foreach (var file in scope.Files.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
            {
                request.Files.Add(new AiBugFixFile
                {
                    FilePath = file.Path,
                    Text = file.Text,
                    IsEditable = file.IsEditable,
                    DocumentVersion = GetTextHash(file.Text)
                });
            }

            request.Diagnostics.Add(new AiBugFixDiagnostic
            {
                Id = target.Id,
                FilePath = target.FilePath,
                Message = target.Message,
                Code = target.Code,
                Start = target.Span.Start,
                Length = target.Span.Length,
                Severity = target.Severity == BpDiagnosticSeverity.Error ? "error" : "warning",
                IsFixable = target.IsFixable
            });

            var data = scope.CurrentProgram;
            var editor = data == null ? null : data.Editor;
            var source = scope.GetText(target.FilePath);
            var references = new BpAiReferenceProvider();
            request.LanguageReference = references.BuildLanguageReference(source, "");
            request.LinkedContext = editor == null ? "" : references.BuildLinkedContext(editor, source, "");
            return request;
        }

        private static BpDiagnostic ChooseNextDiagnostic(BpAnalysisResult result, WorkspaceScope scope, ISet<string> seenStates)
        {
            if (result == null || result.Diagnostics == null)
                return null;

            return result.Diagnostics
                .Where(item => item != null && item.IsFixable && scope.IsEditable(item.FilePath))
                .OrderBy(item => item.Severity == BpDiagnosticSeverity.Error ? 0 : 1)
                .ThenBy(item => item.FilePath ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Span.Start)
                .FirstOrDefault(item => !seenStates.Contains(item.Id + "|" + GetTextHash(scope.GetText(item.FilePath))));
        }

        private static bool IsSafeProgress(BpAnalysisResult before, BpAnalysisResult after, BpDiagnostic target)
        {
            if (after == null || before == null)
                return false;

            var equivalentRemains = after.Diagnostics.Any(item =>
                SamePath(item.FilePath, target.FilePath) && string.Equals(item.Code, target.Code, StringComparison.Ordinal)
                && string.Equals(item.Message, target.Message, StringComparison.Ordinal));
            if (equivalentRemains)
                return false;

            var beforeErrors = before.Diagnostics.Count(item => item.Severity == BpDiagnosticSeverity.Error);
            var afterErrors = after.Diagnostics.Count(item => item.Severity == BpDiagnosticSeverity.Error);
            return afterErrors <= beforeErrors;
        }

        private static bool TryApplyToEditors(AiBugFixApplyResult result, out string error)
        {
            error = "";
            var appliedTextAreas = new List<Scintilla>();
            foreach (var file in result.Files)
            {
                ProgramData data;
                if (!TryGetOrOpenProgram(file.FilePath, out data, out error))
                    return false;
                if (data.Editor == null || data.Editor.TextArea == null ||
                    !string.Equals(data.Editor.TextArea.Text ?? "", file.OriginalText ?? "", StringComparison.Ordinal))
                {
                    error = "Файл изменился во время запроса AI; правка не применена.";
                    return false;
                }
            }

            foreach (var file in result.Files)
            {
                ProgramData data;
                if (!TryGetOrOpenProgram(file.FilePath, out data, out error))
                    return false;
                var textArea = data.Editor.TextArea;
                var appliedCorrectly = false;
                textArea.BeginUndoAction();
                try
                {
                    foreach (var edit in (file.Edits ?? new List<AiBugFixEdit>())
                        .OrderByDescending(edit => edit.Start).ThenByDescending(edit => edit.Length))
                    {
                        textArea.SetTargetRange(edit.Start, edit.Start + edit.Length);
                        textArea.ReplaceTarget(edit.ReplacementText ?? "");
                    }
                    appliedCorrectly = string.Equals(textArea.Text ?? "", file.UpdatedText ?? "", StringComparison.Ordinal);
                    if (appliedCorrectly)
                        data.TextChange = true;
                }
                finally
                {
                    textArea.EndUndoAction();
                }
                if (!appliedCorrectly)
                {
                    textArea.Undo();
                    for (var appliedIndex = appliedTextAreas.Count - 1; appliedIndex >= 0; appliedIndex--)
                        appliedTextAreas[appliedIndex].Undo();
                    error = "IDE не смогла применить проверенный набор точечных правок.";
                    return false;
                }
                appliedTextAreas.Add(textArea);
            }
            return true;
        }

        private static bool TryGetOrOpenProgram(string path, out ProgramData program, out string error)
        {
            program = null;
            error = "";
            if (string.IsNullOrWhiteSpace(path) || !MainWindowVM.IsPathInsideCurrentProject(path))
            {
                error = "AI может менять только файлы текущего проекта.";
                return false;
            }

            program = MainWindowVM.Project.GetDictionary().Values.FirstOrDefault(item =>
                item != null && SamePath(item.FullPath, path));
            if (program != null)
                return true;

            if (!File.Exists(path))
            {
                error = "Файл для исправления больше не существует.";
                return false;
            }

            try
            {
                MainWindowVM.FileNew(path, false);
                program = MainWindowVM.Project.GetDictionary().Values.FirstOrDefault(item =>
                    item != null && SamePath(item.FullPath, path));
                if (program == null)
                {
                    error = "IDE не смогла открыть файл для исправления.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Не удалось открыть файл для исправления: " + ex.Message;
                return false;
            }
        }

        private static void FormatChangedEditors(IEnumerable<string> changedPaths)
        {
            foreach (var path in changedPaths ?? Enumerable.Empty<string>())
            {
                var program = MainWindowVM.Project.GetDictionary().Values.FirstOrDefault(item =>
                    item != null && SamePath(item.FullPath, path));
                if (program == null || program.Editor == null)
                    continue;
                var textArea = program.Editor.TextArea;
                textArea.BeginUndoAction();
                try
                {
                    program.Editor.Lexer.Format();
                    program.TextChange = true;
                }
                finally
                {
                    textArea.EndUndoAction();
                }
            }
        }

        private static void RequestLiveRefresh(WorkspaceScope scope)
        {
            foreach (var file in scope.Files.Values.Where(file => file.IsEditable))
            {
                var program = MainWindowVM.Project.GetDictionary().Values.FirstOrDefault(item =>
                    item != null && SamePath(item.FullPath, file.Path));
                if (program != null && program.Editor != null)
                    program.Editor.LiveDiagnostics.RequestAnalysis();
            }
        }

        private static void PublishLiveAnalysis(BpAnalysisResult result, WorkspaceScope scope)
        {
            var revision = IdeProblemStore.CreateLiveRevision();
            foreach (var file in scope.Files.Values)
            {
                var text = file.Text;
                var problems = (result == null ? new List<BpDiagnostic>() : result.Diagnostics)
                    .Where(diagnostic => diagnostic != null && SamePath(diagnostic.FilePath, file.Path))
                    .Select(diagnostic => ToIdeProblem(diagnostic, text))
                    .ToList();
                IdeProblemStore.ReplaceLive(file.Path, revision, problems);
            }
        }

        private static bool TryBuildScope(out WorkspaceScope scope, out string error)
        {
            scope = null;
            error = "";
            if (MainWindowVM.Project == null || MainWindowVM.CurrentProgram == null)
            {
                error = "Нет активного BP-файла для исправления.";
                return false;
            }

            var current = MainWindowVM.Project.GetProgramData(MainWindowVM.CurrentProgram);
            if (current == null || string.IsNullOrWhiteSpace(current.FullPath) ||
                !MainWindowVM.IsBasicPlusExtension(Path.GetExtension(current.FullPath)))
            {
                error = "Fix bugs доступен только для сохранённого BP, BPI или BPM файла.";
                return false;
            }

            var root = MainWindowVM.CurrentProjectRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                root = Path.GetDirectoryName(current.FullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                error = "Не определён корень проекта.";
                return false;
            }

            var scopeBuilder = new WorkspaceScope(current, root);
            foreach (var path in EnumerateProjectSources(root))
                scopeBuilder.AddDiskOrOpenFile(path, true);
            scopeBuilder.AddDiskOrOpenFile(current.FullPath, true);
            scopeBuilder.AddReachableExternalLibraries();
            scopeBuilder.ReduceToConnectedComponent(current.FullPath);
            if (!scopeBuilder.Contains(current.FullPath))
            {
                error = "Не удалось сформировать снимок исходного файла.";
                return false;
            }

            scopeBuilder.BuildSnapshot();
            scope = scopeBuilder;
            return true;
        }

        private static IEnumerable<string> EnumerateProjectSources(string root)
        {
            var extensions = new[] { "*.bp", "*.bpi", "*.bpm" };
            foreach (var extension in extensions)
            {
                string[] paths;
                try { paths = Directory.GetFiles(root, extension, SearchOption.AllDirectories); }
                catch { paths = new string[0]; }
                foreach (var path in paths)
                    yield return path;
            }
        }

        private static FixBugsDialogItem ToDialogItem(IdeProblem problem)
        {
            var kind = string.IsNullOrWhiteSpace(problem.Code) ? problem.Source.ToString() : problem.Code;
            var range = problem.Line > 0 ? "строка " + problem.Line + (problem.Column > 0 ? ", столбец " + problem.Column : "") : "без точной позиции";
            return new FixBugsDialogItem(kind, Path.GetFileName(problem.FilePath ?? ""), range, problem.Message, problem.IsFixCandidate);
        }

        private static IdeProblem ToIdeProblem(BpDiagnostic diagnostic)
        {
            return ToIdeProblem(diagnostic, "");
        }

        private static IdeProblem ToIdeProblem(BpDiagnostic diagnostic, string text)
        {
            var line = 0;
            var column = 0;
            GetLineAndColumn(text, diagnostic.Span.Start, out line, out column);
            return new IdeProblem
            {
                Id = diagnostic.Id,
                Source = IdeProblemSource.Live,
                IsError = diagnostic.Severity == BpDiagnosticSeverity.Error,
                FilePath = diagnostic.FilePath,
                Start = diagnostic.Span.Start,
                Length = diagnostic.Span.Length,
                Line = line,
                Column = column,
                Code = diagnostic.Code,
                Message = diagnostic.Message,
                IsFixCandidate = diagnostic.IsFixable,
                IsMappedToSource = diagnostic.Span.Length > 0
            };
        }

        private static string GetNonFixableReason(IdeProblem problem)
        {
            return problem.Source == IdeProblemSource.Ide
                ? "Это ошибка среды IDE, устройства, сети или сохранения, а не исходного BP-кода."
                : "Для этой проблемы нет безопасной исходной позиции или разрешённой точечной правки.";
        }

        private static void AddUnresolved(IDictionary<string, FixBugsDialogItem> unresolved, IdeProblem problem, string reason)
        {
            if (problem == null)
                return;
            var key = string.IsNullOrWhiteSpace(problem.Id) ? Guid.NewGuid().ToString("N") : problem.Id;
            if (!unresolved.ContainsKey(key))
            {
                var item = ToDialogItem(problem);
                unresolved.Add(key, new FixBugsDialogItem(item.Kind, item.File, item.Range, item.Message, false, reason));
            }
        }

        private static long GetTextHash(string text)
        {
            unchecked
            {
                var hash = 17;
                foreach (var character in text ?? "")
                    hash = hash * 31 + character;
                return ((long)(text ?? "").Length << 32) ^ (uint)hash;
            }
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            try { return Path.GetFullPath(path ?? ""); }
            catch { return path ?? ""; }
        }

        private static void GetLineAndColumn(string text, int offset, out int line, out int column)
        {
            text = text ?? "";
            offset = Math.Max(0, Math.Min(offset, text.Length));
            line = 1;
            column = 1;
            for (var index = 0; index < offset; index++)
            {
                if (text[index] == '\n') { line++; column = 1; }
                else column++;
            }
        }

        private sealed class WorkspaceFile
        {
            internal string Path;
            internal string Text;
            internal bool IsEditable;
            internal bool WasChanged;
            internal readonly HashSet<string> Links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class WorkspaceScope
        {
            private readonly ProgramData _currentProgram;
            private readonly string _projectRoot;
            internal readonly Dictionary<string, WorkspaceFile> Files = new Dictionary<string, WorkspaceFile>(StringComparer.OrdinalIgnoreCase);
            internal BpWorkspaceSnapshot Snapshot { get; private set; }
            internal ProgramData CurrentProgram { get { return _currentProgram; } }

            internal WorkspaceScope(ProgramData currentProgram, string projectRoot)
            {
                _currentProgram = currentProgram;
                _projectRoot = NormalizePath(projectRoot);
            }

            internal bool Contains(string path) { return Files.ContainsKey(NormalizePath(path)); }
            internal bool IsEditable(string path)
            {
                WorkspaceFile file;
                return Files.TryGetValue(NormalizePath(path), out file) && file.IsEditable;
            }
            internal string GetText(string path)
            {
                WorkspaceFile file;
                return Files.TryGetValue(NormalizePath(path), out file) ? file.Text : "";
            }

            internal void AddDiskOrOpenFile(string path, bool requireProjectFile)
            {
                path = NormalizePath(path);
                if (Files.ContainsKey(path) || string.IsNullOrWhiteSpace(path))
                    return;
                var isEditable = IsInsideProject(path);
                if (requireProjectFile && !isEditable)
                    return;

                var open = MainWindowVM.Project.GetDictionary().Values.FirstOrDefault(item => item != null && SamePath(item.FullPath, path));
                string text;
                if (open != null && open.Editor != null && open.Editor.TextArea != null)
                    text = open.Editor.TextArea.Text ?? "";
                else
                {
                    try { text = File.ReadAllText(path); }
                    catch { return; }
                }
                Files.Add(path, new WorkspaceFile { Path = path, Text = text, IsEditable = isEditable });
            }

            internal void AddReachableExternalLibraries()
            {
                var queue = new Queue<WorkspaceFile>(Files.Values.ToList());
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (queue.Count > 0)
                {
                    var file = queue.Dequeue();
                    if (!visited.Add(file.Path))
                        continue;
                    foreach (Match match in DependencyRegex.Matches(file.Text ?? ""))
                    {
                        var target = ResolveDependency(file.Path, match.Groups[2].Value, match.Groups[1].Value);
                        if (string.IsNullOrWhiteSpace(target))
                            continue;
                        file.Links.Add(target);
                        WorkspaceFile existing;
                        if (!Files.TryGetValue(target, out existing))
                        {
                            AddDiskOrOpenFile(target, false);
                            Files.TryGetValue(target, out existing);
                        }
                        if (existing != null)
                        {
                            existing.Links.Add(file.Path);
                            queue.Enqueue(existing);
                        }
                    }
                }
            }

            internal void ReduceToConnectedComponent(string rootPath)
            {
                rootPath = NormalizePath(rootPath);
                var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<string>();
                queue.Enqueue(rootPath);
                while (queue.Count > 0)
                {
                    var path = queue.Dequeue();
                    if (!retained.Add(path))
                        continue;
                    WorkspaceFile file;
                    if (!Files.TryGetValue(path, out file))
                        continue;
                    foreach (var link in file.Links)
                        queue.Enqueue(link);
                }
                foreach (var path in Files.Keys.Where(path => !retained.Contains(path)).ToList())
                    Files.Remove(path);
            }

            internal void BuildSnapshot()
            {
                var snapshot = new BpWorkspaceSnapshot
                {
                    RootFilePath = _currentProgram.FullPath,
                    LibraryRoot = AppDomain.CurrentDomain.BaseDirectory
                };
                foreach (var file in Files.Values)
                    snapshot.Files.Add(new BpSourceFile(file.Path, file.Text, !file.IsEditable));
                Snapshot = snapshot;
            }

            internal WorkspaceScope WithSimulatedChanges(AiBugFixApplyResult result)
            {
                var clone = new WorkspaceScope(_currentProgram, _projectRoot);
                foreach (var source in Files.Values)
                {
                    clone.Files.Add(source.Path, new WorkspaceFile
                    {
                        Path = source.Path,
                        Text = source.Text,
                        IsEditable = source.IsEditable,
                        WasChanged = source.WasChanged
                    });
                    foreach (var link in source.Links)
                        clone.Files[source.Path].Links.Add(link);
                }
                foreach (var changed in result.Files)
                {
                    WorkspaceFile file;
                    if (clone.Files.TryGetValue(NormalizePath(changed.FilePath), out file))
                    {
                        file.Text = changed.UpdatedText;
                        file.WasChanged = true;
                    }
                }
                clone.BuildSnapshot();
                return clone;
            }

            private bool IsInsideProject(string path)
            {
                try
                {
                    var root = _projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    return NormalizePath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }

            private static string ResolveDependency(string caller, string name, string kind)
            {
                if (string.IsNullOrWhiteSpace(caller) || string.IsNullOrWhiteSpace(name))
                    return "";
                var extension = string.Equals(kind, "include", StringComparison.OrdinalIgnoreCase) ? ".bpi" : ".bpm";
                var relative = name.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                string candidate;
                if (relative.StartsWith("Clev3r:" + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("Clever:" + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    var folder = string.Equals(kind, "include", StringComparison.OrdinalIgnoreCase) ? "Includes" : "Modules";
                    var separatorIndex = relative.IndexOf(new string(Path.DirectorySeparatorChar, 2), StringComparison.Ordinal);
                    candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lib", folder, relative.Substring(separatorIndex + 2));
                }
                else
                {
                    candidate = Path.Combine(Path.GetDirectoryName(caller) ?? "", relative);
                }
                if (!candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    candidate += extension;
                return NormalizePath(candidate);
            }
        }
    }
}
