using Clever.ViewModel.PanelsVM;
using System.Windows.Controls;

namespace Clever.View.Panels
{
    /// <summary>
    /// Логика взаимодействия для GitPanel.xaml
    /// </summary>
    public partial class GitPanel : UserControl
    {
        public GitPanel()
        {
            DataContext = new GitPanelVM();
            InitializeComponent();
        }
    }
}
