using NEW_CLEVER.CommonData;
using NEW_CLEVER.Model.Utils;
using NEW_CLEVER.ViewModel;
using System.Windows;

namespace NEW_CLEVER.View.Dialogs
{
    /// <summary>
    /// Логика взаимодействия для FirstAssociationWindow.xaml
    /// </summary>
    public partial class FirstAssociationWindow : Window
    {
        public FirstAssociationWindow()
        {
            InitializeComponent();
            txtAnswer.Text = MainWindowVM.GetLocalization["prgAssociation"];
            butTextYes.Text = MainWindowVM.GetLocalization["prgYes"];
            butTextNo.Text = MainWindowVM.GetLocalization["prgNo"];
            butTextCancel.Text = MainWindowVM.GetLocalization["dButCancel"];
            txtNotShow.Text = MainWindowVM.GetLocalization["winNotShow"];
            notShow.IsChecked = Configurations.Get.Association_NotShow;
        }

        private void notShow_Click(object sender, RoutedEventArgs e)
        {
            if (notShow.IsChecked == true)
            {
                Configurations.Get.Association_NotShow = true;
            }
            else
            {
                Configurations.Get.Association_NotShow = false;
            }
        }

        private void butNo_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
        }

        private void butYes_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
        }

        private void butCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
        }
    }
}
