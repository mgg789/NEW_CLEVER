using NEW_CLEVER.ViewModel;
using NEW_CLEVER.ViewModel.PanelsVM;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Globalization;

namespace NEW_CLEVER.View.Panels
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

            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (e.Key == Key.Enter && shift)
                return;

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

            if (e.Key == Key.Enter)
            {
                if (viewModel.IsBotWorking && viewModel.CanSend)
                    viewModel.QueueComposerMessage();
                else if (!viewModel.IsBotWorking && viewModel.SendOrStopCommand.CanExecute(null))
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

        private void ConversationScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var viewer = sender as ScrollViewer;
            if (viewer == null)
                return;
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta);
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

        private void HistoryTitleTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var textBox = sender as TextBox;
            var item = textBox == null ? null : textBox.DataContext as AiChatHistoryItemVM;
            var viewModel = DataContext as AiChatPanelVM;
            if (item == null || viewModel == null)
                return;

            if (e.Key == Key.Enter)
            {
                viewModel.CommitHistoryRename(item);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                viewModel.CancelHistoryRename(item);
                e.Handled = true;
            }
        }

        private void HistoryTitleTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(e.NewValue is bool) || !(bool)e.NewValue)
                return;
            var textBox = sender as TextBox;
            if (textBox == null)
                return;
            textBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            }), DispatcherPriority.Input);
        }

    }

    public sealed class AiReasoningDisplayConverter : IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
        {
            var text = (value as string ?? "").Trim();
            var normalized = text.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
            if (normalized == "medium") return "mid";
            if (normalized == "extrahigh") return "xhigh";
            switch (normalized)
            {
                case "low": case "high": case "xhigh": case "max": case "ultra": return normalized;
                default: return text;
            }
        }

        public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
