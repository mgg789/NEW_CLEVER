using Clever.ViewModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Clever.View.Dialogs
{
    /// <summary>
    /// Interaction logic for AiSettingsWindow.xaml
    /// </summary>
    public partial class AiSettingsWindow : Window
    {
        private bool _isSynchronizingToken;

        public AiSettingsWindow()
        {
            InitializeComponent();
            var viewModel = new AiSettingsWindowVM();
            DataContext = viewModel;
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            SynchronizePasswordBox(viewModel.EmbeddedToken);
            Closed += AiSettingsWindow_Closed;
        }

        private void EmbeddedTokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isSynchronizingToken)
                return;

            var viewModel = DataContext as AiSettingsWindowVM;
            var passwordBox = sender as PasswordBox;
            if (viewModel != null && passwordBox != null)
                viewModel.EmbeddedToken = passwordBox.Password;
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "EmbeddedToken")
            {
                var viewModel = sender as AiSettingsWindowVM;
                if (viewModel != null)
                    SynchronizePasswordBox(viewModel.EmbeddedToken);
            }
        }

        private void SynchronizePasswordBox(string value)
        {
            _isSynchronizingToken = true;
            try
            {
                EmbeddedTokenPasswordBox.Password = value ?? "";
            }
            finally
            {
                _isSynchronizingToken = false;
            }
        }

        private void AiSettingsWindow_Closed(object sender, System.EventArgs e)
        {
            var viewModel = DataContext as AiSettingsWindowVM;
            if (viewModel != null)
            {
                viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                viewModel.Dispose();
            }
        }
    }
}
