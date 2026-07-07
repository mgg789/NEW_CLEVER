using Clever.ViewModel;
using Clever.ViewModel.PanelsVM;
using System.Windows;
using System.Windows.Controls;

namespace Clever.View.Panels
{
    /// <summary>
    /// Interaction logic for AiChatPanel.xaml
    /// </summary>
    public partial class AiChatPanel : UserControl
    {
        public AiChatPanel()
        {
            DataContext = new AiChatPanelVM();
            InitializeComponent();
        }

        private void AiChatPanel_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || MainWindowVM.IsEditorTabDragData(e.Data))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;

            e.Handled = true;
        }

        private void AiChatPanel_Drop(object sender, DragEventArgs e)
        {
            var viewModel = DataContext as AiChatPanelVM;
            if (viewModel == null)
                return;

            var tabName = MainWindowVM.GetDraggedEditorTabName(e.Data);
            if (!string.IsNullOrEmpty(tabName))
            {
                viewModel.AttachEditorTab(tabName);
                e.Handled = true;
                return;
            }

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null)
                return;

            foreach (var file in files)
                viewModel.AttachDroppedFile(file);

            e.Handled = true;
        }
    }
}
