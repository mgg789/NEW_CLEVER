using System;
using System.Collections.Generic;

namespace Interpreter.LiveDiagnostics
{
    /// <summary>
    /// A half-open character range in a BP source document.  Offsets are UTF-16
    /// offsets, matching Scintilla and <see cref="string"/> indexing.
    /// </summary>
    public struct TextSpan : IEquatable<TextSpan>
    {
        public TextSpan(int start, int length)
        {
            if (start < 0)
                throw new ArgumentOutOfRangeException(nameof(start));
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            Start = start;
            Length = length;
        }

        public int Start { get; private set; }
        public int Length { get; private set; }
        public int End { get { return Start + Length; } }

        public bool Equals(TextSpan other)
        {
            return Start == other.Start && Length == other.Length;
        }

        public override bool Equals(object obj)
        {
            return obj is TextSpan && Equals((TextSpan)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Start * 397) ^ Length;
            }
        }

        public static bool operator ==(TextSpan left, TextSpan right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(TextSpan left, TextSpan right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>Visual category used by the editor's diagnostic indicators.</summary>
    public enum BpDiagnosticSeverity
    {
        Error = 0,
        Warning = 1
    }

    /// <summary>BP pipeline area that produced a diagnostic.</summary>
    public enum BpDiagnosticStage
    {
        Preprocessor = 0,
        Syntax = 1,
        Semantic = 2,
        Compiler = 3,
        Assembler = 4
    }

    /// <summary>
    /// One immutable live-analysis finding.  Id is deterministic for the same
    /// source location and diagnostic code and can therefore be used by UI and
    /// Fix bugs requests without relying on a localized message.
    /// </summary>
    public sealed class BpDiagnostic
    {
        public BpDiagnostic(
            string id,
            string filePath,
            TextSpan span,
            string code,
            string message,
            BpDiagnosticSeverity severity,
            BpDiagnosticStage stage,
            bool isFixable)
        {
            Id = id ?? string.Empty;
            FilePath = filePath ?? string.Empty;
            Span = span;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Severity = severity;
            Stage = stage;
            IsFixable = isFixable;
        }

        public string Id { get; private set; }
        public string FilePath { get; private set; }
        public TextSpan Span { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public BpDiagnosticSeverity Severity { get; private set; }
        public BpDiagnosticStage Stage { get; private set; }
        public bool IsFixable { get; private set; }
    }

    /// <summary>One in-memory source supplied to the live analyzer.</summary>
    public sealed class BpSourceFile
    {
        public BpSourceFile()
        {
            Path = string.Empty;
            Text = string.Empty;
        }

        public BpSourceFile(string path, string text, bool isReadOnly)
        {
            Path = path ?? string.Empty;
            Text = text ?? string.Empty;
            IsReadOnly = isReadOnly;
        }

        public string Path { get; set; }
        public string Text { get; set; }
        public bool IsReadOnly { get; set; }
    }

    /// <summary>
    /// Complete, in-memory view of the currently open BP workspace.  The
    /// analyzer never reads or writes files; dependencies must be represented
    /// by <see cref="Files"/> (read-only library files may be included too).
    /// </summary>
    public sealed class BpWorkspaceSnapshot
    {
        public BpWorkspaceSnapshot()
        {
            RootFilePath = string.Empty;
            LibraryRoot = string.Empty;
            Files = new List<BpSourceFile>();
        }

        public string RootFilePath { get; set; }
        public string LibraryRoot { get; set; }
        public IList<BpSourceFile> Files { get; private set; }
    }

    /// <summary>The complete result of a single snapshot analysis.</summary>
    public sealed class BpAnalysisResult
    {
        internal BpAnalysisResult(IList<BpDiagnostic> diagnostics, IList<string> analyzedFiles)
        {
            Diagnostics = diagnostics;
            AnalyzedFiles = analyzedFiles;
        }

        public IList<BpDiagnostic> Diagnostics { get; private set; }
        public IList<string> AnalyzedFiles { get; private set; }
        public bool HasErrors
        {
            get
            {
                foreach (var diagnostic in Diagnostics)
                {
                    if (diagnostic.Severity == BpDiagnosticSeverity.Error)
                        return true;
                }

                return false;
            }
        }

        public bool HasDiagnostics { get { return Diagnostics.Count > 0; } }
    }
}
