using Clever.ViewModel;
using Clever.ViewModel.PanelsVM;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
            Unloaded += AiChatPanel_Unloaded;
        }

        private void ComposerTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var viewModel = DataContext as AiChatPanelVM;
            if (viewModel == null)
                return;

            if (e.Key == Key.Up && viewModel.MoveSlashSelection(-1))
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down && viewModel.MoveSlashSelection(1))
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && viewModel.ExecuteSelectedSlash())
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                viewModel.CloseSlashPalette();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (viewModel.IsBotWorking && viewModel.CanSend && viewModel.SteerCommand.CanExecute(null))
                    viewModel.SteerCommand.Execute(null);
                else if (viewModel.SendOrStopCommand.CanExecute(null))
                    viewModel.SendOrStopCommand.Execute(null);
                e.Handled = true;
            }
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

        private void AiChatPanel_Unloaded(object sender, RoutedEventArgs e)
        {
            var disposable = DataContext as System.IDisposable;
            if (disposable != null)
                disposable.Dispose();
        }
    }
}
