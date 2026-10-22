using NEW_CLEVER.ViewModel;
using System.Windows;

namespace NEW_CLEVER.View.Dialogs
{
    /// <summary>
    /// Логика взаимодействия для MessageWindow.xaml
    /// </summary>
    public partial class MessageWindow : Window
    {
        public MessageWindow(string defaultAnswer = "")
        {
            InitializeComponent();
            string exceptionTitle;
            if (MainWindowVM.GetLocalization == null ||
                !MainWindowVM.GetLocalization.TryGetValue("dException", out exceptionTitle))
                exceptionTitle = "Error";
            txtException.Text = exceptionTitle;
            txtAnswer.Text = defaultAnswer;
            string closeText;
            if (MainWindowVM.GetLocalization == null ||
                !MainWindowVM.GetLocalization.TryGetValue("dButClose", out closeText))
                closeText = "Close";
            butText.Text = closeText;
        }

        private void btnDialogClose_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
        }
    }
}
