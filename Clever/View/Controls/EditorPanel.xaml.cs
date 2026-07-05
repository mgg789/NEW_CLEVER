using Clever.View.Controls.Editors;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Clever.ViewModel;
using Clever.ViewModel.PanelsVM;
using TabControl = System.Windows.Controls.TabControl;

namespace Clever.View.Controls
{
    /// <summary>
    /// Логика взаимодействия для EditorPanel.xaml
    /// </summary>
    public partial class EditorPanel : System.Windows.Controls.UserControl
    {
        private static TabControl _tabControl;
        private static TabControl _secondaryTabControl;
        static Popup _popup;
        private Point _dragStartPoint;

        public EditorPanel()
        {
            InitializeComponent();
            _tabControl = EditorTabControl;
            _secondaryTabControl = SecondaryEditorTabControl;
            _popup = PopUp;
        }

        public static TabItem GetSelectedItem
        {
            get
            {
                if (_tabControl.Items.Count > 0)
                {
                    return MainWindowVM.CurrentProgram ?? (TabItem)_tabControl.Items[_tabControl.SelectedIndex];
                }
                return null;
            }
        }

        public static TabControl GetTabControl
        {
            get { return _tabControl; }
        }

        public static TabControl GetSecondaryTabControl
        {
            get { return _secondaryTabControl; }
        }

        public static string SelectedItemHeaderName
        {
            get
            {
                if (_tabControl.Items.Count > 0)
                {
                    var ti = (TabItem)_tabControl.Items[_tabControl.SelectedIndex];
                    var h = (EditorHeader)ti.Header;
                    return h.HeaderName;
                }
                return null;
            }
            set
            {
                if (_tabControl.Items.Count > 0)
                {
                    var ti = (TabItem)_tabControl.Items[_tabControl.SelectedIndex];
                    var h = (EditorHeader)ti.Header;
                    h.HeaderName = value;
                }
            }
        }

        public static Brush SelectedItemHeaderChangeColor
        {
            get
            {
                if (_tabControl.Items.Count > 0)
                {
                    var ti = (TabItem)_tabControl.Items[_tabControl.SelectedIndex];
                    var h = (EditorHeader)ti.Header;
                    return h.HeaderColor;
                }
                return null;
            }
            set
            {
                if (_tabControl.Items.Count > 0)
                {
                    var ti = (TabItem)_tabControl.Items[_tabControl.SelectedIndex];
                    var h = (EditorHeader)ti.Header;
                    h.HeaderColor = value;
                }
            }
        }

        public static string SelectedItemHeaderChange
        {
            get
            {
                if (_tabControl.Items.Count > 0)
                {
                    var ti = (TabItem)_tabControl.Items[_tabControl.SelectedIndex];
                    var h = (EditorHeader)ti.Header;
                    return h.HeaderChange;
                }
                return null;
            }
            set
            {
                if (_tabControl.Items.Count > 0)
                {
                    var ti = (TabItem)_tabControl.Items[_tabControl.SelectedIndex];
                    var h = (EditorHeader)ti.Header;
                    h.HeaderChange = value;
                }
            }
        }

        public static void AddItem(TabItem item)
        {
            _tabControl.Items.Add(item);
            _tabControl.SelectedIndex = _tabControl.Items.Count - 1;
        }

        public static Popup GetPopup
        {
            get { return _popup; }
        }

        private void EditorTabControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void EditorTabControl_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            var currentPosition = e.GetPosition(null);
            if (System.Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                System.Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var header = FindParent<EditorHeader>(e.OriginalSource as DependencyObject);
            if (header == null)
                return;

            var tabItem = FindParent<TabItem>(header);
            if (tabItem != null)
                DragDrop.DoDragDrop(tabItem, tabItem, DragDropEffects.Move);
        }

        private void EditorTabControl_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(TabItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void EditorTabControl_Drop(object sender, DragEventArgs e)
        {
            var tabItem = e.Data.GetData(typeof(TabItem)) as TabItem;
            if (tabItem == null)
                return;

            MainWindowVM.MoveProgramToPane(tabItem, sender == SecondaryEditorTabControl);
            e.Handled = true;
        }

        private void EditorSurface_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(TabItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void EditorSurface_Drop(object sender, DragEventArgs e)
        {
            if (MainWindowVM.IsEditorSplitActive)
                return;

            var tabItem = e.Data.GetData(typeof(TabItem)) as TabItem;
            if (tabItem == null)
                return;

            var position = e.GetPosition(EditorSurface);
            var sideBySide = true;
            if (position.Y > EditorSurface.ActualHeight * 0.62)
                sideBySide = false;

            MainWindowVM.SetEditorSplitMode(sideBySide);
            MainWindowVM.MoveProgramToPane(tabItem, true);
            e.Handled = true;
        }

        private static T FindParent<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null)
            {
                var typed = source as T;
                if (typed != null)
                    return typed;

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }
    }
}
