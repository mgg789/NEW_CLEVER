using Clever.ViewModel;
using System.Windows;

namespace Clever.View.Dialogs
{
    /// <summary>
    /// Interaction logic for AiSettingsWindow.xaml
    /// </summary>
    public partial class AiSettingsWindow : Window
    {
        public AiSettingsWindow()
        {
            DataContext = new AiSettingsWindowVM();
            InitializeComponent();
        }
    }
}
