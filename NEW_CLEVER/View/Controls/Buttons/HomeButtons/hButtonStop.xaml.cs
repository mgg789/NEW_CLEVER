using NEW_CLEVER.View.Controls.Helps;
using System.Windows.Controls;
using System.Windows.Input;

namespace NEW_CLEVER.View.Controls.Buttons.HomeButtons
{
    /// <summary>
    /// Логика взаимодействия для hButtonStop.xaml
    /// </summary>
    public partial class hButtonStop : UserControl
    {
        private static Button _button;
        public hButtonStop()
        {
            InitializeComponent();
            _button = button;
        }
        public static Button GetButton
        {
            get { return _button; }
        }

        private void button_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            EV3Brick.GetEV3Brick.BrickStop_PreviewMouseLeftButtonDown(sender, e);
        }
    }
}
