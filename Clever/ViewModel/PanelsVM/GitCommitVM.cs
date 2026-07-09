using Clever.ViewModel.BaseVM;
using System.Collections.ObjectModel;

namespace Clever.ViewModel.PanelsVM
{
    internal class GitCommitVM : BaseViewModel
    {
        private bool _isExpanded;
        private bool _isSelected;

        public GitCommitVM()
        {
            ParentHashes = new ObservableCollection<string>();
            LocalRefs = new ObservableCollection<string>();
            RemoteRefs = new ObservableCollection<string>();
            Markers = new ObservableCollection<GitCommitMarkerVM>();
            Files = new ObservableCollection<GitChangeItemVM>();
        }

        public string Hash { get; set; }
        public string ShortHash { get; set; }
        public string Message { get; set; }
        public string Author { get; set; }
        public string TimeText { get; set; }
        public string Summary { get; set; }
        public string StatsText { get; set; }
        public GitDiffDocumentVM DiffDocument { get; set; }
        public bool IsHead { get; set; }

        public ObservableCollection<string> ParentHashes { get; private set; }
        public ObservableCollection<string> LocalRefs { get; private set; }
        public ObservableCollection<string> RemoteRefs { get; private set; }
        public ObservableCollection<GitCommitMarkerVM> Markers { get; private set; }
        public ObservableCollection<GitChangeItemVM> Files { get; private set; }

        public bool IsMerge
        {
            get { return ParentHashes.Count > 1; }
        }

        public bool HasRemoteRefs
        {
            get { return RemoteRefs.Count > 0; }
        }

        public bool HasLocalRefs
        {
            get { return LocalRefs.Count > 0; }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                _isExpanded = value;
                OnPropertyChanged("IsExpanded");
            }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                _isSelected = value;
                OnPropertyChanged("IsSelected");
            }
        }
    }
}
