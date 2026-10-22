using NEW_CLEVER.Model.Intellisense;
using System.Windows.Controls;

namespace NEW_CLEVER.View.Controls.Helps
{
    /// <summary>
    /// Логика взаимодействия для Help.xaml
    /// </summary>
    public partial class Help : UserControl
    {
        static TreeView _helpTree;
        public Help()
        {
            InitializeComponent();
            _helpTree = HelpTreeView;
            new Model.Intellisense.IntellisenseInfo().GetInfo();
        }

        public static TreeView GetHelpTree
        {
            get { return _helpTree; }
        }
    }
}
