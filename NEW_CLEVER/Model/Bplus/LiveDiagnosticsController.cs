using NEW_CLEVER.Model.Diagnostics;
using NEW_CLEVER.Model.Utils;
using NEW_CLEVER.ViewModel;
using Interpreter.LiveDiagnostics;
using ScintillaNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FormsTimer = System.Windows.Forms.Timer;

namespace NEW_CLEVER.Model.Bplus
{
    /// <summary>
    /// Bridges immutable BP workspace snapshots to Scintilla indicators. The controller never
    /// reads Scintilla from a worker thread and never persists a document while analysing it.
    /// </summary>
    internal sealed class LiveDiagnosticsController : IDisposable
    {
        internal const int ErrorIndicator = 1;
        internal const int WarningIndicator = 2;

        private const int DebounceMilliseconds = 600;
        private static readonly Color ErrorUnderlineColor = Color.FromArgb(255, 0, 0);
        private static readonly Color WarningUnderlineColor = Color.FromArgb(230, 150, 0);
        // The interpreter analyzer is CPU-heavy for large workspaces. A shared gate
        // prevents several editor tabs from analysing simultaneously while typing.
        private static readonly SemaphoreSlim AnalysisGate = new SemaphoreSlim(1, 1);
        private static readonly Regex DependencyRegex = new Regex(
            @"^\s*(include|import)\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly TextEditor _editor;
        private readonly Scintilla _textArea;
        private readonly FormsTimer _timer;
        private CancellationTokenSource _cancellation;
        private bool _attached;
        private bool _disposed;

        internal LiveDiagnosticsController(TextEditor editor, Scintilla textArea)
        {
            _editor = editor;
            _textArea = textArea;
            _timer = new FormsTimer { Interval = DebounceMilliseconds };
            _timer.Tick += Timer_Tick;
            ConfigureIndicators();
        }

        internal void Attach()
        {
            if (_attached || _disposed || _textArea == null)
                return;
            _attached = true;
            _textArea.TextChanged += TextArea_TextChanged;
            RequestAnalysis();
        }

        internal void Detach()
        {
            if (!_attached)
                return;
            _attached = false;
            _textArea.TextChanged -= TextArea_TextChanged;
            _timer.Stop();
            CancelActiveRequest();
        }

        internal void RequestAnalysis()
        {
            if (_disposed || !_attached)
                return;
            CancelActiveRequest();
            _timer.Stop();
            _timer.Start();
        }

        private void TextArea_TextChanged(object sender, EventArgs e)
        {
            RequestAnalysis();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _timer.Stop();
            RunAnalysisAsync();
        }

        private async void RunAnalysisAsync()
        {
            if (_disposed || !_attached)
                return;

            BpWorkspaceSnapshot snapshot;
            string currentFile;
            try
            {
                snapshot = BuildSnapshot(out currentFile);
            }
            catch (Exception ex)
            {
                IdeProblemStore.ReportIdeError("live-analysis", ex.Message, "");
                return;
            }

            if (snapshot == null || string.IsNullOrWhiteSpace(currentFile))
            {
                ClearIndicators();
                return;
            }

            var requestVersion = IdeProblemStore.CreateLiveRevision();
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            var enteredAnalysisGate = false;
            try
            {
                await AnalysisGate.WaitAsync(cancellation.Token);
                enteredAnalysisGate = true;
                if (_disposed || cancellation.IsCancellationRequested || _cancellation != cancellation)
                    return;

                var result = await Task.Run(() => BpLiveAnalyzer.Analyze(snapshot), cancellation.Token);
                if (_disposed || cancellation.IsCancellationRequested || _cancellation != cancellation)
                    return;

                Publish(result, snapshot, currentFile, requestVersion);
                SyncIndicators(currentFile);
                IdeProblemStore.ClearIdeError("live-analysis");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                IdeProblemStore.ReportIdeError("live-analysis", ex.Message, currentFile);
            }
            finally
            {
                if (enteredAnalysisGate)
                    AnalysisGate.Release();
                if (ReferenceEquals(_cancellation, cancellation))
                    _cancellation = null;
                cancellation.Dispose();
            }
        }

        private static void Publish(BpAnalysisResult result, BpWorkspaceSnapshot snapshot, string currentFile, int requestVersion)
        {
            var allDiagnostics = result == null || result.Diagnostics == null
                ? new List<BpDiagnostic>()
                : result.Diagnostics.ToList();
            var source = (snapshot.Files ?? Enumerable.Empty<BpSourceFile>())
                .FirstOrDefault(file => file != null && SamePath(file.Path, currentFile));
            if (source == null)
                return;

            var fileDiagnostics = allDiagnostics
                .Where(diagnostic => diagnostic != null && SamePath(diagnostic.FilePath, source.Path))
                .Select(diagnostic => ToIdeProblem(diagnostic, source.Text))
                .ToList();
            IdeProblemStore.ReplaceLive(source.Path, requestVersion, fileDiagnostics);
        }

        private static IdeProblem ToIdeProblem(BpDiagnostic diagnostic, string sourceText)
        {
            var isError = diagnostic.Severity == BpDiagnosticSeverity.Error;
            var span = diagnostic.Span;
            var line = 0;
            var column = 0;
            GetLineAndColumn(sourceText, span.Start, out line, out column);
            return new IdeProblem
            {
                Id = diagnostic.Id,
                Source = IdeProblemSource.Live,
                IsError = isError,
                FilePath = diagnostic.FilePath,
                Start = span.Start,
                Length = span.Length,
                Line = line,
                Column = column,
                Code = diagnostic.Code,
                Message = diagnostic.Message,
                IsFixCandidate = diagnostic.IsFixable,
                IsMappedToSource = span.Length > 0
            };
        }

        private BpWorkspaceSnapshot BuildSnapshot(out string currentFile)
        {
            currentFile = "";
            if (_editor == null || _textArea == null || MainWindowVM.Project == null)
                return null;

            var allPrograms = MainWindowVM.Project.GetDictionary().Values
                .Where(data => data != null && data.Editor != null && data.Editor.TextArea != null &&
                    !data.IsPreview && MainWindowVM.IsBasicPlusExtension(Path.GetExtension(data.FullPath ?? data.Name)))
                .ToList();
            var currentData = allPrograms.FirstOrDefault(data => ReferenceEquals(data.Editor, _editor));
            if (currentData == null || string.IsNullOrWhiteSpace(currentData.FullPath))
                return null;

            currentFile = NormalizePath(currentData.FullPath);
            var files = new Dictionary<string, BpSourceFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var data in allPrograms)
            {
                var path = NormalizePath(data.FullPath);
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                files[path] = new BpSourceFile
                {
                    Path = path,
                    Text = data.Editor.TextArea.Text ?? "",
                    IsReadOnly = !MainWindowVM.IsPathInsideCurrentProject(path)
                };
            }

            var libraryRoot = AppDomain.CurrentDomain.BaseDirectory;
            AddClosedDependencies(files, currentFile, libraryRoot);
            var snapshot = new BpWorkspaceSnapshot
            {
                RootFilePath = currentFile,
                LibraryRoot = libraryRoot
            };
            foreach (var file in files.Values)
                snapshot.Files.Add(file);
            return snapshot;
        }

        private static void AddClosedDependencies(IDictionary<string, BpSourceFile> files, string rootFile, string libraryRoot)
        {
            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            queue.Enqueue(rootFile);
            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                if (!visited.Add(path))
                    continue;

                BpSourceFile file;
                if (!files.TryGetValue(path, out file) || file == null)
                    continue;

                foreach (Match match in DependencyRegex.Matches(file.Text ?? ""))
                {
                    var kind = match.Groups[1].Value;
                    var target = ResolveDependency(path, match.Groups[2].Value, kind, libraryRoot);
                    if (string.IsNullOrWhiteSpace(target) || files.ContainsKey(target) || !File.Exists(target))
                        continue;

                    try
                    {
                        files[target] = new BpSourceFile
                        {
                            Path = target,
                            Text = File.ReadAllText(target),
                            IsReadOnly = !MainWindowVM.IsPathInsideCurrentProject(target)
                        };
                        queue.Enqueue(target);
                    }
                    catch
                    {
                        // The analyzer will report the unresolved dependency from its immutable snapshot.
                    }
                }
            }
        }

        private static string ResolveDependency(string callerPath, string name, string kind, string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(callerPath) || string.IsNullOrWhiteSpace(name))
                return "";

            var extension = string.Equals(kind, "include", StringComparison.OrdinalIgnoreCase) ? ".bpi" : ".bpm";
            var normalizedName = name.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            string candidate;
            if (normalizedName.StartsWith("NEW_CLEVER:" + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalizedName.StartsWith("Clev3r:" + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalizedName.StartsWith("Clever:" + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var folder = string.Equals(kind, "include", StringComparison.OrdinalIgnoreCase) ? "Includes" : "Modules";
                var doubleSeparator = new string(Path.DirectorySeparatorChar, 2);
                var relative = normalizedName.Substring(normalizedName.IndexOf(doubleSeparator, StringComparison.Ordinal) + 2);
                candidate = Path.Combine(libraryRoot ?? "", "Lib", folder, relative);
            }
            else
            {
                candidate = Path.Combine(Path.GetDirectoryName(callerPath) ?? "", normalizedName);
            }

            if (!candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                candidate += extension;
            return NormalizePath(candidate);
        }

        private void ConfigureIndicators()
        {
            if (_textArea == null)
                return;

            // A native two-pixel underline remains stable while typing, scrolling and
            // selecting text, and is markedly more legible than Scintilla's thin wave.
            _textArea.Indicators[ErrorIndicator].Style = IndicatorStyle.CompositionThick;
            _textArea.Indicators[ErrorIndicator].ForeColor = ErrorUnderlineColor;
            _textArea.Indicators[WarningIndicator].Style = IndicatorStyle.CompositionThick;
            _textArea.Indicators[WarningIndicator].ForeColor = WarningUnderlineColor;
        }

        private void SyncIndicators(string currentFile)
        {
            if (_disposed || _textArea == null)
                return;

            ClearIndicators();
            var diagnostics = IdeProblemStore.GetSnapshot()
                .Where(problem => problem.IsMappedToSource && SamePath(problem.FilePath, currentFile))
                .ToList();
            foreach (var problem in diagnostics)
            {
                var start = Math.Max(0, Math.Min(problem.Start, _textArea.TextLength));
                var length = Math.Max(1, Math.Min(problem.Length, _textArea.TextLength - start));
                if (length <= 0)
                    continue;
                _textArea.IndicatorCurrent = problem.IsError ? ErrorIndicator : WarningIndicator;
                _textArea.IndicatorFillRange(start, length);
            }
        }

        private void ClearIndicators()
        {
            if (_textArea == null)
                return;
            _textArea.IndicatorCurrent = ErrorIndicator;
            _textArea.IndicatorClearRange(0, _textArea.TextLength);
            _textArea.IndicatorCurrent = WarningIndicator;
            _textArea.IndicatorClearRange(0, _textArea.TextLength);
        }

        private void CancelActiveRequest()
        {
            if (_cancellation == null)
                return;
            _cancellation.Cancel();
            _cancellation = null;
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            try
            {
                return Path.GetFullPath(value);
            }
            catch
            {
                return value;
            }
        }

        private static void GetLineAndColumn(string text, int offset, out int line, out int column)
        {
            text = text ?? "";
            offset = Math.Max(0, Math.Min(offset, text.Length));
            line = 1;
            column = 1;
            for (var index = 0; index < offset; index++)
            {
                if (text[index] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Detach();
            _timer.Dispose();
        }
    }
}
