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

        private enum SplitPreviewTarget
        {
            None,
            Left,
            Right,
            Top,
            Bottom
        }

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
            HideSplitPreview();
            var tabName = GetDraggedTabName(e);
            if (tabName == null)
                return;

            var position = e.GetPosition(EditorSurface);
            var target = DetermineSplitPreviewTarget(position);
            if (!MainWindowVM.IsEditorSplitActive && ShouldCreateSplit(target))
            {
                MainWindowVM.SetEditorSplitMode(target == SplitPreviewTarget.Left || target == SplitPreviewTarget.Right);
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
            if (IsEditorTabDrag(e))
            {
                e.Effects = DragDropEffects.Move;
                UpdateSplitPreview(e.GetPosition(EditorSurface));
            }
            else
            {
                e.Effects = DragDropEffects.None;
                HideSplitPreview();
            }

            e.Handled = true;
        }

        private void EditorSurface_PreviewDragLeave(object sender, DragEventArgs e)
        {
            HideSplitPreview();
        }

        private void EditorSurface_Drop(object sender, DragEventArgs e)
        {
            HideSplitPreview();
            var tabName = GetDraggedTabName(e);
            if (tabName == null)
                return;

            var position = e.GetPosition(EditorSurface);
            if (MainWindowVM.IsEditorSplitActive)
            {
                MainWindowVM.MoveProgramToPane(tabName, IsSecondaryDropPosition(DetermineSplitPreviewTarget(position)));
                e.Handled = true;
                return;
            }

            var target = DetermineSplitPreviewTarget(position);
            if (ShouldCreateSplit(target))
            {
                MainWindowVM.SetEditorSplitMode(target == SplitPreviewTarget.Left || target == SplitPreviewTarget.Right);
                MainWindowVM.MoveProgramToPane(tabName, true);
            }
            else
            {
                MainWindowVM.MoveProgramToPane(tabName, false);
            }

            e.Handled = true;
        }

        private void EditorColumnSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (!MainWindowVM.IsEditorSplitActive || MainWindowVM.EditorPaneColumns != 2)
                return;

            var first = PrimaryColumn.ActualWidth;
            var second = SecondaryColumn.ActualWidth;
            if (first <= 0 || second <= 0)
                return;

            var firstLength = new GridLength(first, GridUnitType.Star);
            var secondLength = new GridLength(second, GridUnitType.Star);
            PrimaryColumn.SetCurrentValue(ColumnDefinition.WidthProperty, firstLength);
            SecondaryColumn.SetCurrentValue(ColumnDefinition.WidthProperty, secondLength);
            MainWindowVM.SetEditorSplitProportion(firstLength, secondLength, true);
        }

        private void EditorRowSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (!MainWindowVM.IsEditorSplitActive || MainWindowVM.EditorPaneRows != 2)
                return;

            var first = PrimaryRow.ActualHeight;
            var second = SecondaryRow.ActualHeight;
            if (first <= 0 || second <= 0)
                return;

            var firstLength = new GridLength(first, GridUnitType.Star);
            var secondLength = new GridLength(second, GridUnitType.Star);
            PrimaryRow.SetCurrentValue(RowDefinition.HeightProperty, firstLength);
            SecondaryRow.SetCurrentValue(RowDefinition.HeightProperty, secondLength);
            MainWindowVM.SetEditorSplitProportion(firstLength, secondLength, false);
        }

        private static bool IsEditorTabDrag(DragEventArgs e)
        {
            return e.Data.GetDataPresent(EditorTabDragFormat);
        }

        private static string GetDraggedTabName(DragEventArgs e)
        {
            return e.Data.GetData(EditorTabDragFormat) as string;
        }

        private bool ShouldCreateSplit(SplitPreviewTarget target)
        {
            return target == SplitPreviewTarget.Right || target == SplitPreviewTarget.Bottom;
        }

        private SplitPreviewTarget DetermineSplitPreviewTarget(Point position)
        {
            if (MainWindowVM.IsEditorSplitActive)
            {
                if (MainWindowVM.EditorPaneColumns == 2)
                    return position.X >= EditorSurface.ActualWidth / 2 ? SplitPreviewTarget.Right : SplitPreviewTarget.Left;

                if (MainWindowVM.EditorPaneRows == 2)
                    return position.Y >= EditorSurface.ActualHeight / 2 ? SplitPreviewTarget.Bottom : SplitPreviewTarget.Top;

                return SplitPreviewTarget.None;
            }

            if (EditorSurface.ActualWidth > 0 && position.X > EditorSurface.ActualWidth * 0.58)
                return SplitPreviewTarget.Right;

            if (EditorSurface.ActualHeight > 0 && position.Y > EditorSurface.ActualHeight * 0.62)
                return SplitPreviewTarget.Bottom;

            return SplitPreviewTarget.None;
        }

        private bool IsSecondaryDropPosition(SplitPreviewTarget target)
        {
            if (MainWindowVM.EditorPaneColumns == 2)
                return target == SplitPreviewTarget.Right;

            if (MainWindowVM.EditorPaneRows == 2)
                return target == SplitPreviewTarget.Bottom;

            return false;
        }

        private void UpdateSplitPreview(Point position)
        {
            var target = DetermineSplitPreviewTarget(position);
            if (!MainWindowVM.IsEditorSplitActive && !ShouldCreateSplit(target))
            {
                HideSplitPreview();
                return;
            }

            ShowSplitPreview(target);
        }

        private void ShowSplitPreview(SplitPreviewTarget target)
        {
            if (target == SplitPreviewTarget.None || EditorSurface.ActualWidth <= 0 || EditorSurface.ActualHeight <= 0)
            {
                HideSplitPreview();
                return;
            }

            SplitPreviewOverlay.HorizontalAlignment = HorizontalAlignment.Stretch;
            SplitPreviewOverlay.VerticalAlignment = VerticalAlignment.Stretch;

            var halfWidth = EditorSurface.ActualWidth / 2;
            var halfHeight = EditorSurface.ActualHeight / 2;

            switch (target)
            {
                case SplitPreviewTarget.Left:
                    SplitPreviewOverlay.Margin = new Thickness(0, 0, halfWidth, 0);
                    break;
                case SplitPreviewTarget.Right:
                    SplitPreviewOverlay.Margin = new Thickness(halfWidth, 0, 0, 0);
                    break;
                case SplitPreviewTarget.Top:
                    SplitPreviewOverlay.Margin = new Thickness(0, 0, 0, halfHeight);
                    break;
                case SplitPreviewTarget.Bottom:
                    SplitPreviewOverlay.Margin = new Thickness(0, halfHeight, 0, 0);
                    break;
            }

            SplitPreviewOverlay.Visibility = Visibility.Visible;
        }

        private void HideSplitPreview()
        {
            SplitPreviewOverlay.Visibility = Visibility.Collapsed;
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
