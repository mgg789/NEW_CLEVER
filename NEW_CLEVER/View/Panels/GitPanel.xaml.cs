using NEW_CLEVER.ViewModel.PanelsVM;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace NEW_CLEVER.View.Panels
{
    /// <summary>
    /// Interaction logic for GitPanel.xaml
    /// </summary>
    public partial class GitPanel : UserControl
    {
        private readonly DispatcherTimer _popupCloseTimer;
        private bool _isGraphRowMouseOver;
        private bool _isPopupMouseOver;
        private bool _restoreRemotePopupAfterMinimize;
        private bool _isRestoringRemotePopup;
        private Window _ownerWindow;

        public GitPanel()
        {
            DataContext = new GitPanelVM();
            InitializeComponent();

            _popupCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _popupCloseTimer.Tick += PopupCloseTimer_Tick;
            Loaded += GitPanel_Loaded;
            Unloaded += GitPanel_Unloaded;
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

        private void RemotePopup_Opened(object sender, EventArgs e)
        {
            if (_isRestoringRemotePopup)
            {
                var viewModel = DataContext as GitPanelVM;
                RemotePasswordBox.Password = viewModel == null ? "" : viewModel.RemotePassword;
                RemoteTokenBox.Password = viewModel == null ? "" : viewModel.RemoteToken;
                _isRestoringRemotePopup = false;
                return;
            }

            RemotePasswordBox.Password = "";
            RemoteTokenBox.Password = "";
        }

        private void RemotePopup_Closed(object sender, EventArgs e)
        {
            var owner = Window.GetWindow(this);
            if (owner != null && owner.WindowState == WindowState.Minimized)
                _restoreRemotePopupAfterMinimize = true;
        }

        private void RemotePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as GitPanelVM;
            var box = sender as PasswordBox;
            if (viewModel != null && box != null)
                viewModel.RemotePassword = box.Password;
        }

        private void RemoteTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as GitPanelVM;
            var box = sender as PasswordBox;
            if (viewModel != null && box != null)
                viewModel.RemoteToken = box.Password;
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

        private void GitPanel_Loaded(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(this);
            if (ReferenceEquals(owner, _ownerWindow))
                return;

            if (_ownerWindow != null)
                _ownerWindow.StateChanged -= OwnerWindow_StateChanged;

            _ownerWindow = owner;
            if (_ownerWindow != null)
                _ownerWindow.StateChanged += OwnerWindow_StateChanged;
        }

        private void GitPanel_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_ownerWindow != null)
                _ownerWindow.StateChanged -= OwnerWindow_StateChanged;

            _ownerWindow = null;
        }

        private void OwnerWindow_StateChanged(object sender, EventArgs e)
        {
            var owner = sender as Window;
            if (owner == null)
                return;

            if (owner.WindowState == WindowState.Minimized)
            {
                var viewModel = DataContext as GitPanelVM;
                _restoreRemotePopupAfterMinimize = RemotePopup.IsOpen || (viewModel != null && viewModel.IsRemotePopupOpen);
                return;
            }

            if (!_restoreRemotePopupAfterMinimize)
                return;

            _restoreRemotePopupAfterMinimize = false;
            Dispatcher.BeginInvoke(new Action(RestoreRemotePopup), DispatcherPriority.Loaded);
        }

        private void RestoreRemotePopup()
        {
            if (RemotePopup.IsOpen)
            {
                _isRestoringRemotePopup = false;
                return;
            }

            var viewModel = DataContext as GitPanelVM;
            _isRestoringRemotePopup = true;

            if (viewModel != null)
                viewModel.IsRemotePopupOpen = true;

            RemotePopup.IsOpen = true;
        }
    }
}
