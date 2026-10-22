using NEW_CLEVER.ViewModel.BaseVM;
using System.Windows;
using System.Windows.Media;

namespace NEW_CLEVER.ViewModel.PanelsVM
{
    internal class GitCommitMarkerVM : BaseViewModel
    {
        public GitCommitMarkerKind Kind { get; set; }
        public string Text { get; set; }
        public Brush Brush { get; set; }

        public Visibility BranchVisibility
        {
            get { return Kind == GitCommitMarkerKind.Branch ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility RemoteVisibility
        {
            get { return Kind == GitCommitMarkerKind.Remote ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility HeadVisibility
        {
            get { return Kind == GitCommitMarkerKind.Head ? Visibility.Visible : Visibility.Collapsed; }
        }
    }
}
