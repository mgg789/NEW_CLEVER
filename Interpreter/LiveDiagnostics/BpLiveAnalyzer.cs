using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Interpreter.LiveDiagnostics
{
    /// <summary>
    /// Tolerant, side-effect-free BP analyser intended for live editor use.
    /// It deliberately does not invoke the legacy static preprocessor/compiler:
    /// a partially typed document must not mutate their global compilation state.
    /// </summary>
    public static class BpLiveAnalyzer
    {
        private static readonly Regex DirectiveRegex = new Regex(
            @"^\s*(include|import)\b\s*(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DeclarationRegex = new Regex(
            @"^\s*(number(?:\[\])?|string(?:\[\])?)\s+(@?[A-Za-z_][A-Za-z0-9_]*)\b(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex FunctionRegex = new Regex(
            @"^\s*function\s+([A-Za-z_][A-Za-z0-9_]*)\b(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SubRegex = new Regex(
            @"^\s*sub\s+([A-Za-z_][A-Za-z0-9_]*)\b(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AssignmentRegex = new Regex(
            @"^\s*(@?[A-Za-z_][A-Za-z0-9_]*)(?:\s*\[[^\]]*\])?\s*(\+|-|\*|/)?=",
            RegexOptions.CultureInvariant);
        private static readonly Regex IdentifierRegex = new Regex(
            @"@?[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?",
            RegexOptions.CultureInvariant);
        private static readonly Regex LabelRegex = new Regex(
            @"^\s*[A-Za-z_][A-Za-z0-9_]*\s*:\s*$",
            RegexOptions.CultureInvariant);
        private static readonly Regex CallableRegex = new Regex(
            @"^\s*([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\s*\(",
            RegexOptions.CultureInvariant);
        private static readonly Regex CallExpressionRegex = new Regex(
            @"([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\s*\(",
            RegexOptions.CultureInvariant);
        private static readonly Regex ThreadRunRegex = new Regex(
            @"^\s*thread\.run\s*=\s*([A-Za-z_][A-Za-z0-9_]*)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "include", "import", "folder", "sub", "endsub", "function", "endfunction",
            "for", "to", "step", "endfor", "if", "then", "elseif", "else", "endif",
            "while", "endwhile", "goto", "private", "break", "continue", "return",
            "region", "endregion", "in", "out", "number", "number[]", "string", "string[]",
            "and", "or", "true", "false"
        };

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(Keywords, StringComparer.OrdinalIgnoreCase)
        {
            "assert", "buttons", "byte", "ev3", "ev3file", "lcd", "mailbox", "math", "motor",
            "motora", "motorb", "motorc", "motord", "motorab", "motorac", "motorad", "motorbc",
            "motorbd", "motorcd", "program", "row", "sensor", "sensor1", "sensor2", "sensor3",
            "sensor4", "speaker", "text", "thread", "time", "vector"
        };

        // The list is intentionally private and immutable.  It mirrors the public BP
        // standard-library surface without touching DefaultObjectList's mutable static
        // state, so background diagnostics cannot race a compilation.
        private static readonly Dictionary<string, int> BuiltInArity = CreateBuiltInArity();

        /// <summary>Analyses one immutable workspace snapshot without I/O or mutation.</summary>
        public static BpAnalysisResult Analyze(BpWorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var session = new AnalysisSession(snapshot);
            return session.Run();
        }

        private sealed class AnalysisSession
        {
            private readonly BpWorkspaceSnapshot _snapshot;
            private readonly Dictionary<string, SourceDocument> _documents;
            private readonly List<BpDiagnostic> _diagnostics;
            private readonly HashSet<string> _diagnosticKeys;
            private readonly Dictionary<string, CallableSymbol> _callables;
            private readonly Dictionary<string, string> _callableOrigins;
            private readonly List<SourceDocument> _reachable;

            internal AnalysisSession(BpWorkspaceSnapshot snapshot)
            {
                _snapshot = snapshot;
                _documents = new Dictionary<string, SourceDocument>(StringComparer.OrdinalIgnoreCase);
                _diagnostics = new List<BpDiagnostic>();
                _diagnosticKeys = new HashSet<string>(StringComparer.Ordinal);
                _callables = new Dictionary<string, CallableSymbol>(StringComparer.OrdinalIgnoreCase);
                _callableOrigins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _reachable = new List<SourceDocument>();

                if (snapshot.Files == null)
                    return;

                var sequence = 0;
                foreach (var source in snapshot.Files)
                {
                    if (source == null || string.IsNullOrWhiteSpace(source.Path))
                        continue;

                    var key = NormalizePath(source.Path);
                    if (_documents.ContainsKey(key))
                        continue;

                    _documents.Add(key, new SourceDocument(source, key, sequence++));
                }
            }

            internal BpAnalysisResult Run()
            {
                var root = ResolveRoot();
                if (root == null)
                    return new BpAnalysisResult(_diagnostics, new List<string>());

                CollectReachable(root, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                CollectCallableDeclarations();

                foreach (var document in _reachable)
                    AnalyzeDocument(document);

                var analyzed = new List<string>();
                foreach (var document in _reachable)
                    analyzed.Add(document.Source.Path);

                return new BpAnalysisResult(_diagnostics, analyzed);
            }

            private SourceDocument ResolveRoot()
            {
                if (_documents.Count == 0)
                    return null;

                if (!string.IsNullOrWhiteSpace(_snapshot.RootFilePath))
                {
                    SourceDocument root;
                    if (_documents.TryGetValue(NormalizePath(_snapshot.RootFilePath), out root))
                        return root;
                }

                foreach (var document in _documents.Values)
                    return document;

                return null;
            }

            private void CollectReachable(SourceDocument document, HashSet<string> visiting, HashSet<string> visited)
            {
                if (visited.Contains(document.Key))
                    return;

                if (visiting.Contains(document.Key))
                    return;

                visiting.Add(document.Key);
                visited.Add(document.Key);
                _reachable.Add(document);

                foreach (var line in document.Lines)
                {
                    var directive = DirectiveRegex.Match(line.Code);
                    if (!directive.Success)
                        continue;

                    var kind = directive.Groups[1].Value;
                    var target = ParseSingleQuotedArgument(directive.Groups[2].Value);
                    if (target == null)
                    {
                        Add(line, FindFirstNonWhitespace(line.Code), Math.Max(1, line.Code.Trim().Length), "BP1002",
                            "Директива " + kind + " должна содержать ровно один путь в кавычках.",
                            BpDiagnosticSeverity.Error, BpDiagnosticStage.Preprocessor, true);
                        continue;
                    }

                    var dependency = ResolveDependency(document, target, kind.Equals("include", StringComparison.OrdinalIgnoreCase) ? ".bpi" : ".bpm");
                    if (dependency == null)
                    {
                        Add(line, line.Code.IndexOf(target, StringComparison.Ordinal), Math.Max(1, target.Length), "BP1001",
                            "Не найден подключаемый файл: " + target,
                            BpDiagnosticSeverity.Error, BpDiagnosticStage.Preprocessor, false);
                        continue;
                    }

                    CollectReachable(dependency, visiting, visited);
                }

                visiting.Remove(document.Key);
            }

            private SourceDocument ResolveDependency(SourceDocument caller, string target, string extension)
            {
                var candidates = new List<string>();
                var normalizedTarget = target.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                if (normalizedTarget.StartsWith("Clev3r:", StringComparison.OrdinalIgnoreCase) || normalizedTarget.StartsWith("Clever:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(_snapshot.LibraryRoot))
                    {
                        var suffix = normalizedTarget.Substring(normalizedTarget.IndexOf(':') + 1).TrimStart(Path.DirectorySeparatorChar);
                        var folder = extension.Equals(".bpi", StringComparison.OrdinalIgnoreCase) ? "Includes" : "Modules";
                        candidates.Add(Path.Combine(_snapshot.LibraryRoot, folder, suffix));
                        candidates.Add(Path.Combine(_snapshot.LibraryRoot, "Lib", folder, suffix));
                    }
                }
                else
                {
                    var directory = Path.GetDirectoryName(caller.Source.Path) ?? string.Empty;
                    candidates.Add(Path.Combine(directory, normalizedTarget));
                }

                foreach (var candidate in candidates)
                {
                    SourceDocument found;
                    var fullCandidate = NormalizePath(candidate);
                    if (_documents.TryGetValue(fullCandidate, out found))
                        return found;
                    if (!fullCandidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
                        _documents.TryGetValue(fullCandidate + extension, out found))
                        return found;
                }

                return null;
            }

            private void CollectCallableDeclarations()
            {
                foreach (var document in _reachable)
                {
                    foreach (var line in document.Lines)
                    {
                        var function = FunctionRegex.Match(line.Code);
                        var sub = SubRegex.Match(line.Code);
                        if (!function.Success && !sub.Success)
                            continue;

                        var match = function.Success ? function : sub;
                        var name = match.Groups[1].Value;
                        var parameterText = function.Success ? match.Groups[2].Value : string.Empty;
                        var parameters = function.Success ? ParseParameters(parameterText) : new List<ParameterSymbol>();
                        var symbol = new CallableSymbol(name, function.Success, parameters);
                        var publicName = IsModule(document) ? Path.GetFileNameWithoutExtension(document.Source.Path) + "." + name : name;

                        CallableSymbol previous;
                        if (_callables.TryGetValue(publicName, out previous))
                        {
                            Add(line, match.Groups[1].Index, match.Groups[1].Length, "BP2001",
                                "Повторное объявление " + (function.Success ? "функции" : "подпрограммы") + ": " + name,
                                BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                            continue;
                        }

                        _callables.Add(publicName, symbol);
                        _callableOrigins.Add(publicName, document.Key);
                    }
                }
            }

            private void AnalyzeDocument(SourceDocument document)
            {
                var knownVariables = CollectAssignedVariables(document);
                var initializedVariables = new Dictionary<string, VariableKind>(StringComparer.OrdinalIgnoreCase);
                var blockStack = new Stack<BlockState>();
                var declaredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var currentParameters = new Dictionary<string, VariableKind>(StringComparer.OrdinalIgnoreCase);

                foreach (var line in document.Lines)
                {
                    AnalyzeLineCharacters(line);
                    if (string.IsNullOrWhiteSpace(line.Code))
                        continue;

                    var firstWord = FirstWord(line.Code);
                    var lower = firstWord.ToLowerInvariant();
                    var function = FunctionRegex.Match(line.Code);
                    var sub = SubRegex.Match(line.Code);

                    if (function.Success || sub.Success)
                    {
                        AnalyzeCallableHeader(line, function.Success ? function : sub, function.Success, declaredNames, currentParameters, blockStack);
                        continue;
                    }

                    if (DirectiveRegex.IsMatch(line.Code))
                    {
                        if (IsModule(document) && lower == "include")
                            Add(line, FindFirstNonWhitespace(line.Code), firstWord.Length, "BP1003", "Модуль .bpm не может подключать .bpi.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Preprocessor, false);
                        continue;
                    }

                    if (lower == "folder")
                    {
                        if (IsModule(document))
                            Add(line, FindFirstNonWhitespace(line.Code), firstWord.Length, "BP1004", "Директива folder недопустима в модуле .bpm.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Preprocessor, false);
                        else if (!HasExactlyQuotedArguments(line.Code.Substring(firstWord.Length), 2))
                            Add(line, FindFirstNonWhitespace(line.Code), Math.Max(1, line.Code.Trim().Length), "BP1005", "Директива folder требует два аргумента в кавычках.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Preprocessor, true);
                        continue;
                    }

                    if (TryAnalyzeBlockLine(line, lower, blockStack, knownVariables, initializedVariables, currentParameters))
                        continue;

                    var declaration = DeclarationRegex.Match(line.Code);
                    if (declaration.Success)
                    {
                        AnalyzeVariableDeclaration(line, declaration, declaredNames, initializedVariables, knownVariables);
                        continue;
                    }

                    if (LabelRegex.IsMatch(line.Code))
                        continue;

                    if (TryAnalyzeThreadRun(line))
                        continue;

                    var assignment = AssignmentRegex.Match(line.Code);
                    if (assignment.Success)
                    {
                        AnalyzeAssignment(line, assignment, knownVariables, initializedVariables, currentParameters);
                        continue;
                    }

                    var callable = CallableRegex.Match(line.Code);
                    if (callable.Success)
                    {
                        AnalyzeCall(line, callable, knownVariables, initializedVariables, currentParameters);
                        continue;
                    }

                    if (lower == "#" || line.Code.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        continue;

                    Add(line, FindFirstNonWhitespace(line.Code), Math.Max(1, line.Code.Trim().Length), "BP3004",
                        "Текст не распознан как инструкция BP. Комментарий должен начинаться с '.",
                        BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);
                }

                while (blockStack.Count > 0)
                {
                    var block = blockStack.Pop();
                    Add(block.Line, block.Start, Math.Max(1, block.Length), "BP2102",
                        "Отсутствует " + block.ExpectedEnd + " для конструкции " + block.Name + ".",
                        BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                }
            }

            private void AnalyzeLineCharacters(LineRecord line)
            {
                if (line.HasUnterminatedString)
                    Add(line, Math.Max(0, line.Code.LastIndexOf('"')), 1, "BP2103", "Не закрыта строка в кавычках.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);

                var stack = new Stack<BracketState>();
                var code = line.Code;
                var inString = false;
                for (var index = 0; index < code.Length; index++)
                {
                    var ch = code[index];
                    if (ch == '\'')
                        break;
                    if (ch == '"')
                    {
                        if (inString && index + 1 < code.Length && code[index + 1] == '"')
                        {
                            index++;
                            continue;
                        }
                        inString = !inString;
                        continue;
                    }
                    if (inString)
                        continue;
                    if (ch == '(' || ch == '[')
                    {
                        stack.Push(new BracketState(ch, index));
                    }
                    else if (ch == ')' || ch == ']')
                    {
                        var wanted = ch == ')' ? '(' : '[';
                        if (stack.Count == 0 || stack.Peek().Character != wanted)
                        {
                            Add(line, index, 1, "BP2101", "Несогласованная закрывающая скобка " + ch + ".", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                        }
                        else
                        {
                            stack.Pop();
                        }
                    }
                }

                while (stack.Count > 0)
                {
                    var opening = stack.Pop();
                    Add(line, opening.Index, 1, "BP2100", "Не закрыта скобка " + opening.Character + ".", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                }
            }

            private void AnalyzeCallableHeader(LineRecord line, Match match, bool isFunction, HashSet<string> declaredNames, Dictionary<string, VariableKind> currentParameters, Stack<BlockState> blockStack)
            {
                var name = match.Groups[1].Value;
                if (ReservedNames.Contains(name))
                    Add(line, match.Groups[1].Index, match.Groups[1].Length, "BP3005", "Имя " + name + " зарезервировано языком BP.", BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);

                if (!declaredNames.Add(name))
                    Add(line, match.Groups[1].Index, match.Groups[1].Length, "BP2001", "Повторное объявление: " + name, BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);

                currentParameters.Clear();
                if (!isFunction)
                {
                    var suffix = match.Groups[2].Value.Trim();
                    if (suffix.Length > 0)
                        Add(line, match.Groups[2].Index, match.Groups[2].Length, "BP2003", "Подпрограмма Sub не принимает список параметров.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    blockStack.Push(new BlockState("Sub", "EndSub", line, match.Index, match.Length));
                    return;
                }

                var parametersText = match.Groups[2].Value;
                var parameters = ParseParameters(parametersText);
                if (!IsParameterListWellFormed(parametersText))
                    Add(line, match.Groups[2].Index, Math.Max(1, match.Groups[2].Length), "BP2002", "Некорректный список параметров функции.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);

                foreach (var parameter in parameters)
                {
                    if (ReservedNames.Contains(parameter.Name))
                        Add(line, match.Groups[2].Index + parameter.Offset, parameter.Name.Length, "BP3005", "Имя " + parameter.Name + " зарезервировано языком BP.", BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);
                    else if (currentParameters.ContainsKey(parameter.Name))
                        Add(line, match.Groups[2].Index + parameter.Offset, parameter.Name.Length, "BP3006", "Параметр повторяется: " + parameter.Name, BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);
                    else
                        currentParameters.Add(parameter.Name, parameter.Kind);
                }

                blockStack.Push(new BlockState("Function", "EndFunction", line, match.Index, match.Length));
            }

            private bool TryAnalyzeBlockLine(LineRecord line, string lower, Stack<BlockState> stack, HashSet<string> knownVariables, Dictionary<string, VariableKind> initializedVariables, Dictionary<string, VariableKind> currentParameters)
            {
                if (lower == "if" || lower == "elseif")
                {
                    if (!EndsWithWord(line.Code, "then"))
                        Add(line, FindFirstNonWhitespace(line.Code), Math.Max(1, line.Code.Trim().Length), "BP2201", "Условие " + firstUpper(lower) + " должно оканчиваться Then.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);

                    if (lower == "if")
                        stack.Push(new BlockState("If", "EndIf", line, FindFirstNonWhitespace(line.Code), lower.Length));
                    else if (!ContainsExpected(stack, "EndIf"))
                        Add(line, FindFirstNonWhitespace(line.Code), lower.Length, "BP2202", "ElseIf допустим только внутри If ... EndIf.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    AnalyzeVariableReads(line, line.Code.Substring(Math.Min(line.Code.Length, lower.Length)), knownVariables, initializedVariables, currentParameters);
                    return true;
                }

                if (lower == "else")
                {
                    if (!HasOnlyWord(line.Code, "else"))
                        Add(line, FindFirstNonWhitespace(line.Code), Math.Max(1, line.Code.Trim().Length), "BP2203", "После Else не должно быть текста.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    if (!ContainsExpected(stack, "EndIf"))
                        Add(line, FindFirstNonWhitespace(line.Code), 4, "BP2202", "Else допустим только внутри If ... EndIf.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    return true;
                }

                if (lower == "for")
                {
                    var keywordStart = FindFirstNonWhitespace(line.Code);
                    var loopText = line.Code.Substring(Math.Min(line.Code.Length, keywordStart + 3));
                    var loopAssignment = AssignmentRegex.Match(loopText);
                    if (IndexOfWord(loopText, "to") < 0 || !loopAssignment.Success)
                        Add(line, FindFirstNonWhitespace(line.Code), Math.Max(1, line.Code.Trim().Length), "BP2204", "Конструкция For должна содержать переменную, = и To.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    else
                    {
                        var loopVariable = loopAssignment.Groups[1].Value;
                        var rightStart = loopAssignment.Index + loopAssignment.Length;
                        AnalyzeVariableReads(line, loopText.Substring(Math.Min(rightStart, loopText.Length)), knownVariables, initializedVariables, currentParameters, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { loopVariable });
                        initializedVariables[loopVariable] = VariableKind.Number;
                        knownVariables.Add(loopVariable);
                    }
                    stack.Push(new BlockState("For", "EndFor", line, FindFirstNonWhitespace(line.Code), 3));
                    return true;
                }

                if (lower == "while")
                {
                    if (line.Code.Trim().Length <= 5)
                        Add(line, FindFirstNonWhitespace(line.Code), 5, "BP2205", "После While должно быть условие.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    stack.Push(new BlockState("While", "EndWhile", line, FindFirstNonWhitespace(line.Code), 5));
                    return true;
                }

                if (lower == "endif" || lower == "endfor" || lower == "endwhile" || lower == "endsub" || lower == "endfunction")
                {
                    var expected = "End" + firstUpper(lower.Substring(3));
                    if (!HasOnlyWord(line.Code, lower))
                        Add(line, FindFirstNonWhitespace(line.Code), Math.Max(1, line.Code.Trim().Length), "BP2206", "После " + firstUpper(lower) + " не должно быть текста.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    if (stack.Count == 0)
                        Add(line, FindFirstNonWhitespace(line.Code), lower.Length, "BP2207", "Нет открывающей конструкции для " + firstUpper(lower) + ".", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    else if (!stack.Peek().ExpectedEnd.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        Add(line, FindFirstNonWhitespace(line.Code), lower.Length, "BP2208", "Ожидалось " + stack.Peek().ExpectedEnd + ", найдено " + firstUpper(lower) + ".", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    else
                    {
                        var ended = stack.Pop();
                        if (ended.ExpectedEnd.Equals("EndFunction", StringComparison.OrdinalIgnoreCase) || ended.ExpectedEnd.Equals("EndSub", StringComparison.OrdinalIgnoreCase))
                            currentParameters.Clear();
                    }
                    return true;
                }

                if (lower == "break" || lower == "continue")
                {
                    if (!HasOnlyWord(line.Code, lower))
                        Add(line, FindFirstNonWhitespace(line.Code), Math.Max(1, line.Code.Trim().Length), "BP2209", "После " + firstUpper(lower) + " не должно быть текста.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    if (!ContainsExpected(stack, "EndFor") && !ContainsExpected(stack, "EndWhile"))
                        Add(line, FindFirstNonWhitespace(line.Code), lower.Length, "BP2210", firstUpper(lower) + " допустим только внутри For или While.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    return true;
                }

                if (lower == "return")
                {
                    if (!HasOnlyWord(line.Code, lower))
                        Add(line, FindFirstNonWhitespace(line.Code), Math.Max(1, line.Code.Trim().Length), "BP2211", "После Return не должно быть текста.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    if (!ContainsExpected(stack, "EndSub") && !ContainsExpected(stack, "EndFunction"))
                        Add(line, FindFirstNonWhitespace(line.Code), lower.Length, "BP2212", "Return допустим только внутри Sub или Function.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    return true;
                }

                if (lower == "goto")
                {
                    var rest = line.Code.Substring(Math.Min(line.Code.Length, 4)).Trim();
                    if (!Regex.IsMatch(rest, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                        Add(line, FindFirstNonWhitespace(line.Code), Math.Max(1, line.Code.Trim().Length), "BP2213", "Goto требует одно имя метки.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
                    return true;
                }

                return false;
            }

            private void AnalyzeVariableDeclaration(LineRecord line, Match declaration, HashSet<string> declaredNames, Dictionary<string, VariableKind> initialized, HashSet<string> known)
            {
                var name = declaration.Groups[2].Value;
                if (ReservedNames.Contains(name))
                    Add(line, declaration.Groups[2].Index, declaration.Groups[2].Length, "BP3005", "Имя " + name + " зарезервировано языком BP.", BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);
                if (!declaredNames.Add(name))
                    Add(line, declaration.Groups[2].Index, declaration.Groups[2].Length, "BP3007", "Переменная уже объявлена: " + name, BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);

                known.Add(name);
                if (!string.IsNullOrWhiteSpace(declaration.Groups[3].Value))
                    Add(line, declaration.Groups[3].Index, declaration.Groups[3].Length, "BP2301", "После объявления переменной не ожидается текст.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
            }

            private void AnalyzeAssignment(LineRecord line, Match assignment, HashSet<string> known, Dictionary<string, VariableKind> initialized, Dictionary<string, VariableKind> parameters)
            {
                var variable = assignment.Groups[1].Value;
                var leftStart = assignment.Groups[1].Index;
                var rightStart = assignment.Index + assignment.Length;
                var readBeforeWrite = assignment.Groups[2].Success;
                var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { variable };
                AnalyzeVariableReads(line, line.Code.Substring(Math.Min(rightStart, line.Code.Length)), known, initialized, parameters, ignored);

                if (readBeforeWrite)
                    EnsureVariableReadable(line, variable, leftStart, known, initialized, parameters);

                var expression = line.Code.Substring(Math.Min(rightStart, line.Code.Length));
                AnalyzeEmbeddedCalls(line, expression, known, initialized, parameters);
                var inferred = InferKind(expression, initialized, parameters);
                initialized[variable] = inferred;
                known.Add(variable);
            }

            private bool TryAnalyzeThreadRun(LineRecord line)
            {
                var match = ThreadRunRegex.Match(line.Code);
                if (!match.Success)
                    return false;

                var subName = match.Groups[1].Value;
                CallableSymbol symbol;
                if (!_callables.TryGetValue(subName, out symbol) || symbol.IsFunction)
                    Add(line, match.Groups[1].Index, match.Groups[1].Length, "BP3010", "Неизвестная подпрограмма для Thread.Run: " + subName + ".", BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);
                return true;
            }

            private void AnalyzeEmbeddedCalls(LineRecord line, string expression, HashSet<string> known, Dictionary<string, VariableKind> initialized, Dictionary<string, VariableKind> parameters)
            {
                var expressionStart = line.Code.IndexOf(expression, StringComparison.Ordinal);
                foreach (Match nested in CallExpressionRegex.Matches(line.Code))
                {
                    if (nested.Index >= expressionStart && !IsInsideString(line.Code, nested.Index))
                        AnalyzeCall(line, nested, known, initialized, parameters, false);
                }
            }

            private void AnalyzeCall(LineRecord line, Match call, HashSet<string> known, Dictionary<string, VariableKind> initialized, Dictionary<string, VariableKind> parameters, bool isStatement = true)
            {
                var name = call.Groups[1].Value;
                var openBracket = line.Code.IndexOf('(', call.Index + call.Length - 1);
                var closeBracket = FindMatchingParenthesis(line.Code, openBracket);
                if (closeBracket < 0)
                    return;

                var argumentText = line.Code.Substring(openBracket + 1, closeBracket - openBracket - 1);
                var argumentCount = CountArguments(argumentText);
                var spanLength = Math.Max(1, name.Length);
                CallableSymbol callable;
                int expected;
                if (_callables.TryGetValue(name, out callable))
                {
                    if (callable.IsFunction && callable.Parameters.Count != argumentCount)
                        Add(line, call.Groups[1].Index, spanLength, "BP3011", "Неверное число параметров у " + name + ": ожидается " + callable.Parameters.Count + ", получено " + argumentCount + ".", BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);
                    if (!callable.IsFunction && argumentCount != 0)
                        Add(line, call.Groups[1].Index, spanLength, "BP3011", "Подпрограмма " + name + " не принимает параметры.", BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);
                    CheckFunctionArgumentKinds(line, call, argumentText, callable, initialized, parameters);
                }
                else if (BuiltInArity.TryGetValue(name, out expected))
                {
                    if (expected != argumentCount)
                        Add(line, call.Groups[1].Index, spanLength, "BP3011", "Неверное число параметров у " + name + ": ожидается " + expected + ", получено " + argumentCount + ".", BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);
                }
                else
                {
                    Add(line, call.Groups[1].Index, spanLength, "BP3010", "Неизвестная функция, подпрограмма или метод: " + name + ".", BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);
                }

                AnalyzeVariableReads(line, argumentText, known, initialized, parameters);
                if (isStatement && closeBracket + 1 < line.Code.Length && !string.IsNullOrWhiteSpace(line.Code.Substring(closeBracket + 1)))
                    Add(line, closeBracket + 1, line.Code.Length - closeBracket - 1, "BP2302", "После вызова не ожидается текст.", BpDiagnosticSeverity.Error, BpDiagnosticStage.Syntax, true);
            }

            private void CheckFunctionArgumentKinds(LineRecord line, Match call, string arguments, CallableSymbol callable, Dictionary<string, VariableKind> initialized, Dictionary<string, VariableKind> parameters)
            {
                var values = SplitArguments(arguments);
                for (var i = 0; i < values.Count && i < callable.Parameters.Count; i++)
                {
                    var expected = callable.Parameters[i].Kind;
                    var actual = InferKind(values[i], initialized, parameters);
                    if (expected != VariableKind.Unknown && actual != VariableKind.Unknown && expected != actual)
                        Add(line, call.Groups[1].Index, call.Groups[1].Length, "BP3012", "Неверный тип параметра " + (i + 1) + " у " + callable.Name + ".", BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);
                }
            }

            private void AnalyzeVariableReads(LineRecord line, string fragment, HashSet<string> known, Dictionary<string, VariableKind> initialized, Dictionary<string, VariableKind> parameters, HashSet<string> ignored = null)
            {
                if (known == null)
                    known = CollectAssignedVariables(line.Document);

                foreach (Match identifier in IdentifierRegex.Matches(fragment))
                {
                    var name = identifier.Value;
                    if (IsInsideString(fragment, identifier.Index) || Keywords.Contains(name) || name.IndexOf('.') >= 0 || (ignored != null && ignored.Contains(name)))
                        continue;
                    var following = identifier.Index + identifier.Length < fragment.Length ? fragment[identifier.Index + identifier.Length] : '\0';
                    if (following == '(')
                        continue;
                    EnsureVariableReadable(line, name, FindOffset(line, fragment, identifier.Index), known, initialized, parameters);
                }
            }

            private void EnsureVariableReadable(LineRecord line, string name, int localOffset, HashSet<string> known, Dictionary<string, VariableKind> initialized, Dictionary<string, VariableKind> parameters)
            {
                if (parameters.ContainsKey(name) || initialized.ContainsKey(name))
                    return;

                var code = known.Contains(name) ? "BP3002" : "BP3001";
                var message = known.Contains(name)
                    ? "Переменная считывается до присваивания: " + name + "."
                    : "Неизвестная переменная: " + name + ".";
                Add(line, Math.Max(0, localOffset), Math.Max(1, name.Length), code, message, BpDiagnosticSeverity.Warning, BpDiagnosticStage.Semantic, true);
            }

            private HashSet<string> CollectAssignedVariables(SourceDocument document)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in document.Lines)
                {
                    var assignment = AssignmentRegex.Match(line.Code);
                    if (assignment.Success)
                        names.Add(assignment.Groups[1].Value);
                    var declaration = DeclarationRegex.Match(line.Code);
                    if (declaration.Success)
                        names.Add(declaration.Groups[2].Value);
                    var function = FunctionRegex.Match(line.Code);
                    if (function.Success)
                    {
                        foreach (var parameter in ParseParameters(function.Groups[2].Value))
                            names.Add(parameter.Name);
                    }
                }
                return names;
            }

            private void Add(LineRecord line, int localStart, int length, string code, string message, BpDiagnosticSeverity severity, BpDiagnosticStage stage, bool fixable)
            {
                var safeStart = Math.Max(0, Math.Min(localStart, line.Code.Length));
                var safeLength = Math.Max(1, Math.Min(length, Math.Max(1, line.Code.Length - safeStart)));
                var span = new TextSpan(line.Start + safeStart, safeLength);
                var key = line.Document.Key + "|" + span.Start + "|" + span.Length + "|" + code;
                if (!_diagnosticKeys.Add(key))
                    return;

                _diagnostics.Add(new BpDiagnostic(CreateStableId(key), line.Document.Source.Path, span, code, message, severity, stage, fixable));
            }
        }

        private sealed class SourceDocument
        {
            internal SourceDocument(BpSourceFile source, string key, int order)
            {
                Source = source;
                Key = key;
                Order = order;
                Lines = SplitLines(this, source.Text ?? string.Empty);
            }

            internal BpSourceFile Source;
            internal string Key;
            internal int Order;
            internal List<LineRecord> Lines;
        }

        private sealed class LineRecord
        {
            internal LineRecord(SourceDocument document, int start, string raw)
            {
                Document = document;
                Start = start;
                Raw = raw;
                Code = RemoveComment(raw, out HasUnterminatedString);
            }

            internal SourceDocument Document;
            internal int Start;
            internal string Raw;
            internal string Code;
            internal bool HasUnterminatedString;
        }

        private sealed class CallableSymbol
        {
            internal CallableSymbol(string name, bool isFunction, List<ParameterSymbol> parameters)
            {
                Name = name;
                IsFunction = isFunction;
                Parameters = parameters;
            }

            internal string Name;
            internal bool IsFunction;
            internal List<ParameterSymbol> Parameters;
        }

        private sealed class ParameterSymbol
        {
            internal ParameterSymbol(string name, VariableKind kind, int offset)
            {
                Name = name;
                Kind = kind;
                Offset = offset;
            }

            internal string Name;
            internal VariableKind Kind;
            internal int Offset;
        }

        private struct BlockState
        {
            internal BlockState(string name, string expectedEnd, LineRecord line, int start, int length)
            {
                Name = name;
                ExpectedEnd = expectedEnd;
                Line = line;
                Start = start;
                Length = length;
            }

            internal string Name;
            internal string ExpectedEnd;
            internal LineRecord Line;
            internal int Start;
            internal int Length;
        }

        private struct BracketState
        {
            internal BracketState(char character, int index)
            {
                Character = character;
                Index = index;
            }

            internal char Character;
            internal int Index;
        }

        private enum VariableKind
        {
            Unknown,
            Number,
            String,
            NumberArray,
            StringArray
        }

        private static List<LineRecord> SplitLines(SourceDocument document, string text)
        {
            var result = new List<LineRecord>();
            var start = 0;
            for (var index = 0; index <= text.Length; index++)
            {
                if (index != text.Length && text[index] != '\n')
                    continue;
                var length = index - start;
                if (length > 0 && text[start + length - 1] == '\r')
                    length--;
                result.Add(new LineRecord(document, start, text.Substring(start, length)));
                start = index + 1;
            }
            return result;
        }

        private static string RemoveComment(string raw, out bool unterminatedString)
        {
            unterminatedString = false;
            var inString = false;
            for (var index = 0; index < raw.Length; index++)
            {
                if (raw[index] == '"')
                    inString = !inString;
                else if (raw[index] == '\'' && !inString)
                    return raw.Substring(0, index);
            }
            unterminatedString = inString;
            return raw;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            try
            {
                return Path.GetFullPath(path).Replace('\\', '/');
            }
            catch (Exception)
            {
                return path.Trim().Replace('\\', '/');
            }
        }

        private static bool IsModule(SourceDocument document)
        {
            return document.Source.Path.EndsWith(".bpm", StringComparison.OrdinalIgnoreCase);
        }

        private static string ParseSingleQuotedArgument(string value)
        {
            var match = Regex.Match(value ?? string.Empty, "^\\s*\\\"([^\\\"]+)\\\"\\s*$", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static bool HasExactlyQuotedArguments(string value, int count)
        {
            var matches = Regex.Matches(value ?? string.Empty, "\\\"[^\\\"]*\\\"");
            if (matches.Count != count)
                return false;
            var compact = Regex.Replace(value, "\\\"[^\\\"]*\\\"", string.Empty).Trim();
            return compact.Length == 0;
        }

        private static string FirstWord(string value)
        {
            var match = Regex.Match(value ?? string.Empty, @"^\s*([^\s(\[=]+)", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static int FindFirstNonWhitespace(string value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                if (!char.IsWhiteSpace(value[index]))
                    return index;
            }
            return 0;
        }

        private static bool EndsWithWord(string value, string word)
        {
            return Regex.IsMatch(value ?? string.Empty, @"\b" + Regex.Escape(word) + @"\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool HasOnlyWord(string value, string word)
        {
            return Regex.IsMatch(value ?? string.Empty, @"^\s*" + Regex.Escape(word) + @"\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static int IndexOfWord(string value, string word)
        {
            var match = Regex.Match(value ?? string.Empty, @"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Index : -1;
        }

        private static bool ContainsExpected(Stack<BlockState> stack, string expectedEnd)
        {
            foreach (var state in stack)
            {
                if (state.ExpectedEnd.Equals(expectedEnd, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string firstUpper(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        private static List<ParameterSymbol> ParseParameters(string parameterText)
        {
            var result = new List<ParameterSymbol>();
            if (string.IsNullOrWhiteSpace(parameterText))
                return result;
            var match = Regex.Match(parameterText, @"^\s*\((.*)\)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
                return result;

            var parts = SplitArguments(match.Groups[1].Value);
            var position = parameterText.IndexOf('(') + 1;
            foreach (var part in parts)
            {
                var parameter = Regex.Match(part, @"^\s*(in|out)\s+(number(?:\[\])?|string(?:\[\])?)\s+(@?[A-Za-z_][A-Za-z0-9_]*)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (parameter.Success)
                    result.Add(new ParameterSymbol(parameter.Groups[3].Value, ToVariableKind(parameter.Groups[2].Value), position + parameter.Groups[3].Index));
                position += part.Length + 1;
            }
            return result;
        }

        private static bool IsParameterListWellFormed(string parameterText)
        {
            if (string.IsNullOrWhiteSpace(parameterText))
                return true;
            var match = Regex.Match(parameterText, @"^\s*\((.*)\)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success)
                return false;
            var content = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(content))
                return true;
            foreach (var part in SplitArguments(content))
            {
                if (!Regex.IsMatch(part, @"^\s*(in|out)\s+(number(?:\[\])?|string(?:\[\])?)\s+@?[A-Za-z_][A-Za-z0-9_]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return false;
            }
            return true;
        }

        private static VariableKind ToVariableKind(string value)
        {
            if (value.Equals("number", StringComparison.OrdinalIgnoreCase)) return VariableKind.Number;
            if (value.Equals("string", StringComparison.OrdinalIgnoreCase)) return VariableKind.String;
            if (value.Equals("number[]", StringComparison.OrdinalIgnoreCase)) return VariableKind.NumberArray;
            if (value.Equals("string[]", StringComparison.OrdinalIgnoreCase)) return VariableKind.StringArray;
            return VariableKind.Unknown;
        }

        private static VariableKind InferKind(string expression, IDictionary<string, VariableKind> initialized, IDictionary<string, VariableKind> parameters)
        {
            var text = (expression ?? string.Empty).Trim();
            if (text.Length == 0)
                return VariableKind.Unknown;
            if (text[0] == '"')
                return VariableKind.String;
            if (Regex.IsMatch(text, @"^[+-]?[0-9]+(?:\.[0-9]+)?$", RegexOptions.CultureInvariant))
                return VariableKind.Number;
            if (text.EndsWith("]", StringComparison.Ordinal) && text.IndexOf('[') >= 0)
                return VariableKind.Unknown;
            var name = Regex.Match(text, @"^@?[A-Za-z_][A-Za-z0-9_]*$").Value;
            VariableKind kind;
            if (name.Length > 0 && parameters.TryGetValue(name, out kind)) return kind;
            if (name.Length > 0 && initialized.TryGetValue(name, out kind)) return kind;
            return VariableKind.Unknown;
        }

        private static int FindMatchingParenthesis(string text, int open)
        {
            if (open < 0 || open >= text.Length)
                return -1;
            var depth = 0;
            var inString = false;
            for (var index = open; index < text.Length; index++)
            {
                if (text[index] == '"')
                    inString = !inString;
                if (inString)
                    continue;
                if (text[index] == '(') depth++;
                else if (text[index] == ')' && --depth == 0) return index;
            }
            return -1;
        }

        private static int CountArguments(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
            return SplitArguments(text).Count;
        }

        private static List<string> SplitArguments(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return result;
            var start = 0;
            var depth = 0;
            var inString = false;
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '"') inString = !inString;
                if (inString) continue;
                if (text[index] == '(' || text[index] == '[') depth++;
                else if (text[index] == ')' || text[index] == ']') depth--;
                else if (text[index] == ',' && depth == 0)
                {
                    result.Add(text.Substring(start, index - start));
                    start = index + 1;
                }
            }
            result.Add(text.Substring(start));
            return result;
        }

        private static int FindOffset(LineRecord line, string fragment, int offset)
        {
            var first = line.Code.IndexOf(fragment, StringComparison.Ordinal);
            return first < 0 ? offset : first + offset;
        }

        private static bool IsInsideString(string value, int index)
        {
            var inString = false;
            for (var current = 0; current < index && current < value.Length; current++)
            {
                if (value[current] == '"')
                    inString = !inString;
            }
            return inString;
        }

        private static string CreateStableId(string value)
        {
            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;
                var hash = offset;
                foreach (var character in value)
                {
                    hash ^= character;
                    hash *= prime;
                }
                return "bp-" + hash.ToString("x8");
            }
        }

        private static Dictionary<string, int> CreateBuiltInArity()
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            AddKnown(result, "assert.failed:1,assert.equal:3,assert.notequal:3,assert.less:3,assert.greater:3,assert.lessequal:3,assert.greaterequal:3,assert.near:3");
            AddKnown(result, "buttons.getclicks:0,buttons.wait:0,buttons.flush:0,buttons.current:0");
            AddKnown(result, "byte.not:1,byte.and_:2,byte.or_:2,byte.xor:2,byte.bit:2,byte.shl:2,byte.shr:2,byte.tohex:1,byte.tobinary:1,byte.tologic:1,byte.h:1,byte.b:1,byte.l:1");
            AddKnown(result, "ev3.setledcolor:2,ev3.systemcall:1,ev3.queuenextcommand:0,ev3.batterylevel:0,ev3.batteryvoltage:0,ev3.batterycurrent:0,ev3.time:0,ev3.brickname:0");
            AddKnown(result, "ev3file.openwrite:1,ev3file.openappend:1,ev3file.openread:1,ev3file.close:1,ev3file.writeline:2,ev3file.writebyte:2,ev3file.writenumberarray:3,ev3file.readline:1,ev3file.readbyte:1,ev3file.readnumberarray:2,ev3file.converttonumber:1,ev3file.tablelookup:4");
            AddKnown(result, "lcd.stopupdate:0,lcd.update:0,lcd.clear:0,lcd.rect:5,lcd.line:5,lcd.text:5,lcd.write:3,lcd.circle:4,lcd.fillcircle:4,lcd.fillrect:5,lcd.inverserect:4,lcd.pixel:3,lcd.bmpfile:4");
            AddKnown(result, "mailbox.create:1,mailbox.createfornumber:1,mailbox.isavailable:1,mailbox.receive:1,mailbox.send:3,mailbox.receivenumber:1,mailbox.sendnumber:3,mailbox.connect:1");
            AddKnown(result, "math.pi:0,math.abs:1,math.ceiling:1,math.floor:1,math.naturallog:1,math.log:1,math.cos:1,math.sin:1,math.tan:1,math.arcsin:1,math.arccos:1,math.arctan:1,math.getdegrees:1,math.getradians:1,math.squareroot:1,math.power:2,math.round:1,math.getrandomnumber:1,math.min:2,math.max:2,math.remainder:2");
            AddKnown(result, "motor.stop:2,motor.start:2,motor.startpower:2,motor.startsync:3,motor.getspeed:1,motor.isbusy:1,motor.schedule:6,motor.schedulepower:6,motor.schedulesync:5,motor.resetcount:1,motor.getcount:1,motor.move:4,motor.movepower:4,motor.movesync:5,motor.startsteer:3,motor.schedulesteer:5,motor.movesteer:5,motor.invert:1,motor.wait:1");
            AddKnown(result, "program.delay:1,program.end:0,program.argumentcount:0,program.directory:0,program.getargument:1,row.init:2,row.delete:1,row.read:2,row.write:3,row.size:1,row.resize:2");
            AddKnown(result, "sensor.getname:1,sensor.gettype:1,sensor.getmode:1,sensor.setmode:2,sensor.isbusy:1,sensor.wait:1,sensor.readpercent:1,sensor.readraw:2,sensor.readrawvalue:2,sensor.communicatei2c:5,sensor.readi2cregister:3,sensor.readi2cregisters:4,sensor.writei2cregister:4,sensor.writei2cregisters:5,sensor.senduartdata:3");
            AddKnown(result, "speaker.stop:0,speaker.tone:3,speaker.note:3,speaker.play:2,speaker.isbusy:0,speaker.wait:0,text.append:2,text.getlength:1,text.issubtext:2,text.endswith:2,text.startswith:2,text.getsubtext:3,text.getsubtexttoend:2,text.converttolowercase:1,text.converttouppercase:1,text.getcharacter:1,text.getcharactercode:1,text.getindexof:2");
            AddKnown(result, "thread.yield:0,thread.createmutex:0,thread.lock:1,thread.unlock:1,thread.run:0,vector.init:2,vector.data:2,vector.add:3,vector.sort:2,vector.multiply:5");
            AddKnown(result, "motora.gettacho:0,motora.getspeed:0,motora.resetcount:0,motora.setdirectpolarity:0,motora.setreverspolarity:0,motora.off:0,motora.offandbrake:0,motora.islarge:0,motora.ismedium:0,motora.start:0,motora.setpower:1,motora.setspeed:1,motora.startpower:1,motora.startspeed:1");
            AddKnown(result, "motorb.gettacho:0,motorb.getspeed:0,motorb.resetcount:0,motorb.setdirectpolarity:0,motorb.setreverspolarity:0,motorb.off:0,motorb.offandbrake:0,motorb.islarge:0,motorb.ismedium:0,motorb.start:0,motorb.setpower:1,motorb.setspeed:1,motorb.startpower:1,motorb.startspeed:1");
            AddKnown(result, "motorc.gettacho:0,motorc.getspeed:0,motorc.resetcount:0,motorc.setdirectpolarity:0,motorc.setreverspolarity:0,motorc.off:0,motorc.offandbrake:0,motorc.islarge:0,motorc.ismedium:0,motorc.start:0,motorc.setpower:1,motorc.setspeed:1,motorc.startpower:1,motorc.startspeed:1");
            AddKnown(result, "motord.gettacho:0,motord.getspeed:0,motord.resetcount:0,motord.setdirectpolarity:0,motord.setreverspolarity:0,motord.off:0,motord.offandbrake:0,motord.islarge:0,motord.ismedium:0,motord.start:0,motord.setpower:1,motord.setspeed:1,motord.startpower:1,motord.startspeed:1");
            AddKnown(result, "motorab.off:0,motorab.offandbrake:0,motorab.start:0,motorab.setpower:1,motorab.setspeed:1,motorab.startpower:1,motorab.startspeed:1,motorac.off:0,motorac.offandbrake:0,motorac.start:0,motorac.setpower:1,motorac.setspeed:1,motorac.startpower:1,motorac.startspeed:1,motorad.off:0,motorad.offandbrake:0,motorad.start:0,motorad.setpower:1,motorad.setspeed:1,motorad.startpower:1,motorad.startspeed:1,motorbc.off:0,motorbc.offandbrake:0,motorbc.start:0,motorbc.setpower:1,motorbc.setspeed:1,motorbc.startpower:1,motorbc.startspeed:1,motorbd.off:0,motorbd.offandbrake:0,motorbd.start:0,motorbd.setpower:1,motorbd.setspeed:1,motorbd.startpower:1,motorbd.startspeed:1,motorcd.off:0,motorcd.offandbrake:0,motorcd.start:0,motorcd.setpower:1,motorcd.setspeed:1,motorcd.startpower:1,motorcd.startspeed:1");
            AddKnown(result, "sensor1.raw1:0,sensor1.raw3:3,sensor2.raw1:0,sensor2.raw3:3,sensor3.raw1:0,sensor3.raw3:3,sensor4.raw1:0,sensor4.raw3:3,time.get1:0,time.reset1:0,time.get2:0,time.reset2:0,time.get3:0,time.reset3:0,time.get4:0,time.reset4:0,time.get5:0,time.reset5:0,time.get6:0,time.reset6:0,time.get7:0,time.reset7:0,time.get8:0,time.reset8:0,time.get9:0,time.reset9:0");
            return result;
        }

        private static void AddKnown(IDictionary<string, int> destination, string serialized)
        {
            foreach (var item in serialized.Split(','))
            {
                var separator = item.LastIndexOf(':');
                if (separator <= 0)
                    continue;
                int arity;
                if (int.TryParse(item.Substring(separator + 1), out arity))
                    destination[item.Substring(0, separator)] = arity;
            }
        }
    }
}
