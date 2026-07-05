namespace Clever.ViewModel.PanelsVM
{
    internal class GitDiffLineVM
    {
        public GitDiffLineVM(string text, GitDiffLineKind kind)
        {
            Text = text;
            Kind = kind;
        }

        public string Text { get; private set; }
        public GitDiffLineKind Kind { get; private set; }
    }
}
