using Clever.CommonData;
using Clever.Model.Utils;
using Clever.View.Dialogs;
using Clever.ViewModel;
using System.Windows;
using System.Windows.Controls;

namespace Clever.View.Controls.Menu
{
    /// <summary>
    /// Логика взаимодействия для HomeMenu.xaml
    /// </summary>
    public partial class HomeMenu : UserControl
    {
        private static AiSettingsWindow aiSettingsWindow;

        public HomeMenu()
        {
            InitializeComponent();
        }

        private void Language_Click(object sender, RoutedEventArgs e)
        {
            //var win = new LanguageChange();
            var win = new LanguageWindow();
            win.ShowDialog();
            ButtonsPanel.GetHomePopup.IsOpen = false;
        }

        private void Registry_Click(object sender, RoutedEventArgs e)
        {
            if (!Configurations.Get.Association)
            {
                var reg = new AssociationWindow();
                if (reg.ShowDialog() == true)
                {
                    var result = new KeyRegistry("r");
                    Configurations.Get.Association = result.IsRegistry;
                }
            }
            else
            {
                var reg = new UnAssociationWindow();
                if (reg.ShowDialog() == true)
                {
                    var result = new KeyRegistry("u");
                    Configurations.Get.Association = result.IsRegistry;
                }
            }
            ButtonsPanel.GetHomePopup.IsOpen = false;
        }

        private void Config_Click(object sender, RoutedEventArgs e)
        {
            var cdc = new ConfigWindowVM();
            var conf = new ConfigWindow();
            conf.DataContext = cdc;
            ButtonsPanel.GetHomePopup.IsOpen = false;                   

            if (conf.ShowDialog() == true)
            {
                cdc.RemoveEvents();
                cdc.ColorItems.Clear();
            }
        }

        private void Ai_Click(object sender, RoutedEventArgs e)
        {
            ButtonsPanel.GetHomePopup.IsOpen = false;

            if (aiSettingsWindow != null)
            {
                aiSettingsWindow.Activate();
                aiSettingsWindow.Focus();
                return;
            }

            aiSettingsWindow = new AiSettingsWindow
            {
                Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            aiSettingsWindow.Closed += (closedSender, closedArgs) => aiSettingsWindow = null;
            aiSettingsWindow.Show();
            aiSettingsWindow.Topmost = true;
            aiSettingsWindow.Topmost = false;
            aiSettingsWindow.Activate();
        }
    }
}
