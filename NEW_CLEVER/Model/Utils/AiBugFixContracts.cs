using System;
using System.Collections.Generic;

namespace NEW_CLEVER.Model.Utils
{
    /// <summary>
    /// Immutable-in-practice input supplied by the IDE for one constrained AI repair request.
    /// The service never reads files itself: all source text is supplied by the caller.
    /// </summary>
    internal sealed class AiBugFixRequest
    {
        internal AiBugFixRequest()
        {
            Files = new List<AiBugFixFile>();
            Diagnostics = new List<AiBugFixDiagnostic>();
        }

        internal IList<AiBugFixFile> Files { get; set; }
        internal IList<AiBugFixDiagnostic> Diagnostics { get; set; }
        internal string LanguageReference { get; set; }
        internal string LinkedContext { get; set; }
    }

    /// <summary>
    /// A source snapshot. FilePath is an identity supplied by the IDE and is never resolved,
    /// opened or otherwise accessed by the AI layer.
    /// </summary>
    internal sealed class AiBugFixFile
    {
        internal string FilePath { get; set; }
        internal string Text { get; set; }
        internal bool IsEditable { get; set; }
        internal long DocumentVersion { get; set; }
    }

    internal sealed class AiBugFixDiagnostic
    {
        internal string Id { get; set; }
        internal string FilePath { get; set; }
        internal string Message { get; set; }
        internal string Code { get; set; }
        internal int Start { get; set; }
        internal int Length { get; set; }
        internal string Severity { get; set; }
        internal bool IsFixable { get; set; }
    }

    internal sealed class AiBugFixResponse
    {
        internal AiBugFixResponse()
        {
            DiagnosticIds = new List<string>();
            Edits = new List<AiBugFixEdit>();
        }

        /// <summary>True only when the provider response is strict JSON matching the repair schema.</summary>
        internal bool IsValid { get; set; }
        internal string ValidationError { get; set; }
        internal string RawResponse { get; set; }
        internal string Action { get; set; }
        internal IList<string> DiagnosticIds { get; set; }
        internal string Reason { get; set; }
        internal IList<AiBugFixEdit> Edits { get; set; }

        internal bool ShouldApply
        {
            get { return IsValid && string.Equals(Action, "apply", StringComparison.Ordinal); }
        }
    }

    /// <summary>
    /// An offset-based replacement against the original text snapshot. expectedText is mandatory
    /// so a stale editor document cannot receive a blind edit.
    /// </summary>
    internal sealed class AiBugFixEdit
    {
        internal string FilePath { get; set; }
        internal int Start { get; set; }
        internal int Length { get; set; }
        internal string ExpectedText { get; set; }
        internal string ReplacementText { get; set; }
    }

    /// <summary>Validated, in-memory result. Applying it to editors is the caller's responsibility.</summary>
    internal sealed class AiBugFixApplyResult
    {
        internal AiBugFixApplyResult()
        {
            Files = new List<AiBugFixAppliedFile>();
            AppliedDiagnosticIds = new List<string>();
        }

        internal bool Success { get; set; }
        internal string FailureReason { get; set; }
        internal IList<AiBugFixAppliedFile> Files { get; set; }
        internal IList<string> AppliedDiagnosticIds { get; set; }
    }

    internal sealed class AiBugFixAppliedFile
    {
        internal string FilePath { get; set; }
        internal long DocumentVersion { get; set; }
        internal string OriginalText { get; set; }
        internal string UpdatedText { get; set; }
        internal IList<AiBugFixEdit> Edits { get; set; }
    }
}
