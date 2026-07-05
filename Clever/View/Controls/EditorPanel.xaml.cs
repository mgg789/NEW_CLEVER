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
        private const string EditorTabDragFormat = "Clever.EditorTabName";
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
                if (MainWindowVM.CurrentProgram != null)
                    return MainWindowVM.CurrentProgram;

                if (_tabControl.Items.Count > 0 && _tabControl.SelectedIndex >= 0)
                    return (TabItem)_tabControl.Items[_tabControl.SelectedIndex];

                if (_secondaryTabControl.Items.Count > 0 && _secondaryTabControl.SelectedIndex >= 0)
                    return (TabItem)_secondaryTabControl.Items[_secondaryTabControl.SelectedIndex];

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
                var ti = GetSelectedItem;
                if (ti == null)
                    return null;

                var h = (EditorHeader)ti.Header;
                return h.HeaderName;
            }
            set
            {
                var ti = GetSelectedItem;
                if (ti == null)
                    return;

                var h = (EditorHeader)ti.Header;
                h.HeaderName = value;
            }
        }

        public static Brush SelectedItemHeaderChangeColor
        {
            get
            {
                var ti = GetSelectedItem;
                if (ti == null)
                    return null;

                var h = (EditorHeader)ti.Header;
                return h.HeaderColor;
            }
            set
            {
                var ti = GetSelectedItem;
                if (ti == null)
                    return;

                var h = (EditorHeader)ti.Header;
                h.HeaderColor = value;
            }
        }

        public static string SelectedItemHeaderChange
        {
            get
            {
                var ti = GetSelectedItem;
                if (ti == null)
                    return null;

                var h = (EditorHeader)ti.Header;
                return h.HeaderChange;
            }
            set
            {
                var ti = GetSelectedItem;
                if (ti == null)
                    return;

                var h = (EditorHeader)ti.Header;
                h.HeaderChange = value;
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

            var closeLabel = FindParent<Label>(e.OriginalSource as DependencyObject);
            if (closeLabel != null && closeLabel.Name == "lClose")
                return;

            var tabItem = MainWindowVM.FindOpenProgramItem(header.HeaderName);
            if (tabItem == null)
                return;

            var data = new DataObject();
            data.SetData(EditorTabDragFormat, header.HeaderName);
            DragDrop.DoDragDrop(tabItem, data, DragDropEffects.Move);
            e.Handled = true;
        }

        private void EditorTabControl_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = IsEditorTabDrag(e) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void EditorTabControl_Drop(object sender, DragEventArgs e)
        {
            var tabName = GetDraggedTabName(e);
            if (tabName == null)
                return;

            var position = e.GetPosition(EditorSurface);
            bool sideBySide;
            if (!MainWindowVM.IsEditorSplitActive && ShouldCreateSplit(position, out sideBySide))
            {
                MainWindowVM.SetEditorSplitMode(sideBySide);
                MainWindowVM.MoveProgramToPane(tabName, true);
            }
            else
            {
                MainWindowVM.MoveProgramToPane(tabName, sender == SecondaryEditorTabControl);
            }

            e.Handled = true;
        }

        private void EditorSurface_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = IsEditorTabDrag(e) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void EditorSurface_Drop(object sender, DragEventArgs e)
        {
            var tabName = GetDraggedTabName(e);
            if (tabName == null)
                return;

            var position = e.GetPosition(EditorSurface);
            if (MainWindowVM.IsEditorSplitActive)
            {
                MainWindowVM.MoveProgramToPane(tabName, IsSecondaryDropPosition(position));
                e.Handled = true;
                return;
            }

            bool sideBySide;
            if (ShouldCreateSplit(position, out sideBySide))
            {
                MainWindowVM.SetEditorSplitMode(sideBySide);
                MainWindowVM.MoveProgramToPane(tabName, true);
            }
            else
            {
                MainWindowVM.MoveProgramToPane(tabName, false);
            }

            e.Handled = true;
        }

        private static bool IsEditorTabDrag(DragEventArgs e)
        {
            return e.Data.GetDataPresent(EditorTabDragFormat);
        }

        private static string GetDraggedTabName(DragEventArgs e)
        {
            return e.Data.GetData(EditorTabDragFormat) as string;
        }

        private bool ShouldCreateSplit(Point position, out bool sideBySide)
        {
            sideBySide = true;
            if (EditorSurface.ActualWidth > 0 && position.X > EditorSurface.ActualWidth * 0.58)
                return true;

            if (EditorSurface.ActualHeight > 0 && position.Y > EditorSurface.ActualHeight * 0.62)
            {
                sideBySide = false;
                return true;
            }

            return false;
        }

        private bool IsSecondaryDropPosition(Point position)
        {
            if (MainWindowVM.EditorPaneColumns == 2)
                return position.X >= EditorSurface.ActualWidth / 2;

            if (MainWindowVM.EditorPaneRows == 2)
                return position.Y >= EditorSurface.ActualHeight / 2;

            return false;
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
