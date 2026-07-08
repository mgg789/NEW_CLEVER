namespace Clever.ViewModel.PanelsVM
{
    internal class GitDiffLineVM
    {
        public GitDiffLineVM(string text, GitDiffLineKind kind)
            : this(text, kind, text)
        {
        }

        public GitDiffLineVM(string text, GitDiffLineKind kind, string displayText)
        {
            Text = text;
            Kind = kind;
            DisplayText = displayText ?? text;
        }

        public string Text { get; private set; }
        public string DisplayText { get; private set; }
        public GitDiffLineKind Kind { get; private set; }
    }
}
