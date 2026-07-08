using Clever.ViewModel.BaseVM;
using System.IO;
using System.Windows;
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
        public bool HasFixedFileType { get; private set; }
        public string RepositoryRoot { get; set; }
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public string OldPath { get; set; }
        public string PreviewText { get; set; }
        public bool IsDeleted { get; set; }

        public Visibility FileTypeVisibility
        {
            get { return HasFixedFileType ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility DefaultFileIconVisibility
        {
            get { return HasFixedFileType ? Visibility.Collapsed : Visibility.Visible; }
        }

        internal void ApplyFixedFileType()
        {
            var extension = Path.GetExtension(FileName).ToLowerInvariant();
            HasFixedFileType = true;

            switch (extension)
            {
                case ".bp":
                    FileType = "BP";
                    IconBrush = CreateBrush("#D45B55");
                    break;
                case ".bpi":
                    FileType = "BPI";
                    IconBrush = CreateBrush("#3F9D64");
                    break;
                case ".bpm":
                    FileType = "BPM";
                    IconBrush = CreateBrush("#0A86FF");
                    break;
                case ".txt":
                    FileType = "TXT";
                    IconBrush = CreateBrush("#8F98AB");
                    break;
                case ".csv":
                    FileType = "CSV";
                    IconBrush = CreateBrush("#F39C12");
                    break;
                default:
                    HasFixedFileType = false;
                    FileType = "";
                    IconBrush = CreateBrush("#8F98AB");
                    break;
            }

            OnPropertyChanged("FileType");
            OnPropertyChanged("IconBrush");
            OnPropertyChanged("HasFixedFileType");
            OnPropertyChanged("FileTypeVisibility");
            OnPropertyChanged("DefaultFileIconVisibility");
        }

        private static Brush CreateBrush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
    }
}
