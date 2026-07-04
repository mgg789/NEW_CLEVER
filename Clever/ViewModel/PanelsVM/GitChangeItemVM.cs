using Clever.ViewModel.BaseVM;
using System.Windows.Media;

namespace Clever.ViewModel.PanelsVM
{
    internal class GitChangeItemVM : BaseViewModel
    {
        public string FileName { get; set; }
        public string Folder { get; set; }
        public string Status { get; set; }
        public string StatusText { get; set; }
        public string FileType { get; set; }
        public Brush StatusBrush { get; set; }
        public Brush IconBrush { get; set; }
    }
}
