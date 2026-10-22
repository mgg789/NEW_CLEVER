using NEW_CLEVER.ViewModel.BaseVM;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace NEW_CLEVER.ViewModel.PanelsVM
{
    internal class GitGraphRowVM : BaseViewModel
    {
        public GitGraphRowVM()
        {
            Segments = new ObservableCollection<GitGraphSegmentVM>();
        }

        public GitCommitVM Commit { get; set; }
        public ObservableCollection<GitGraphSegmentVM> Segments { get; private set; }
        public double NodeX { get; set; }
        public double NodeY { get; set; }
        public double GraphWidth { get; set; }
        public double CommitHeight { get; set; }
        public double RowHeight { get; set; }
        public Brush NodeBrush { get; set; }
    }
}
