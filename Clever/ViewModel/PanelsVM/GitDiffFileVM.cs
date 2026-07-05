using System.Collections.ObjectModel;

namespace Clever.ViewModel.PanelsVM
{
    internal class GitDiffFileVM
    {
        public GitDiffFileVM(string path)
        {
            Path = path;
            Lines = new ObservableCollection<GitDiffLineVM>();
        }

        public string Path { get; private set; }
        public ObservableCollection<GitDiffLineVM> Lines { get; private set; }
    }
}
