using System.Windows.Controls;

namespace NEW_CLEVER.View.Controls.Intellisense
{
    /// <summary>
    /// Логика взаимодействия для HeaderProperty.xaml
    /// </summary>
    public partial class HeaderProperty : UserControl
    {
        public HeaderProperty()
        {
            InitializeComponent();
        }

        public string Text
        {
            get { return hoText.Text; }
            set { hoText.Text = value; }
        }
    }
}
