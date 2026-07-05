using Clever.ViewModel.PanelsVM;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Clever.View.Panels
{
    /// <summary>
    /// Interaction logic for GitPanel.xaml
    /// </summary>
    public partial class GitPanel : UserControl
    {
        private readonly DispatcherTimer _popupCloseTimer;
        private bool _isGraphRowMouseOver;
        private bool _isPopupMouseOver;

        public GitPanel()
        {
            DataContext = new GitPanelVM();
            InitializeComponent();

            _popupCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _popupCloseTimer.Tick += PopupCloseTimer_Tick;
        }

        private void GraphCommitButton_MouseEnter(object sender, MouseEventArgs e)
        {
            var element = sender as FrameworkElement;
            var row = element == null ? null : element.DataContext as GitGraphRowVM;
            if (row == null)
                return;

            _isGraphRowMouseOver = true;
            _popupCloseTimer.Stop();

            CommitInfoPopup.DataContext = row;
            CommitInfoPopup.PlacementTarget = element;
            CommitInfoPopup.IsOpen = true;
        }

        private void GraphCommitButton_MouseLeave(object sender, MouseEventArgs e)
        {
            _isGraphRowMouseOver = false;
            StartPopupCloseTimer();
        }

        private void CommitInfoPopup_MouseEnter(object sender, MouseEventArgs e)
        {
            _isPopupMouseOver = true;
            _popupCloseTimer.Stop();
        }

        private void CommitInfoPopup_MouseLeave(object sender, MouseEventArgs e)
        {
            _isPopupMouseOver = false;
            StartPopupCloseTimer();
        }

        private void RevertCommitButton_Click(object sender, RoutedEventArgs e)
        {
            ExecuteCommitAction(vm => vm.RevertCommitCommand);
        }

        private void DiffCommitButton_Click(object sender, RoutedEventArgs e)
        {
            ExecuteCommitAction(vm => vm.DiffCommitCommand);
        }

        private void CommitRowActionButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void ExecuteCommitAction(Func<GitPanelVM, ICommand> commandSelector)
        {
            var viewModel = DataContext as GitPanelVM;
            var row = CommitInfoPopup.DataContext as GitGraphRowVM;
            var commit = row == null ? null : row.Commit;
            if (viewModel == null)
                return;

            var command = commandSelector(viewModel);
            if (command != null && command.CanExecute(commit))
                command.Execute(commit);
        }

        private void StartPopupCloseTimer()
        {
            _popupCloseTimer.Stop();
            _popupCloseTimer.Start();
        }

        private void PopupCloseTimer_Tick(object sender, EventArgs e)
        {
            _popupCloseTimer.Stop();
            if (!_isGraphRowMouseOver && !_isPopupMouseOver)
                CommitInfoPopup.IsOpen = false;
        }
    }
}
