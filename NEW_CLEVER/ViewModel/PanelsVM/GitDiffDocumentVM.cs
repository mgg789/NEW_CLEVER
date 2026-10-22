using System.Collections.ObjectModel;
using System.Text;

namespace NEW_CLEVER.ViewModel.PanelsVM
{
    internal class GitDiffDocumentVM
    {
        public GitDiffDocumentVM(string hash, string title)
        {
            Hash = hash;
            Title = title;
            Files = new ObservableCollection<GitDiffFileVM>();
        }

        public string Hash { get; private set; }
        public string Title { get; private set; }
        public ObservableCollection<GitDiffFileVM> Files { get; private set; }

        public string ToText()
        {
            var text = new StringBuilder();
            foreach (var file in Files)
            {
                foreach (var line in file.Lines)
                    text.AppendLine(line.DisplayText);
            }

            return text.ToString();
        }
    }
}
