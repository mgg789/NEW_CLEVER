using Clever.Model.Bplus;
using Clever.Model.Utils;
using Clever.View.Controls;
using Clever.View.Dialogs;
using Clever.ViewModel;
using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clever.View.Controls.Editors
{
    /// <summary>
    /// Логика взаимодействия для Editor.xaml
    /// </summary>
    public partial class Editor : System.Windows.Controls.UserControl
    {
        TextEditor te;
        private CsvTableDocument _csvDocument;
        private CsvTableChangeTracker _csvChangeTracker;
        private DataTable _trackedCsvTable;
        private bool _isUpdatingCsvText;
        private bool _isRenderedView;
        private string _renderedExtension;
        private string _cachedMarkdownText;
        private string _cachedMarkdownTitle;
        private string _cachedMarkdownHtml;
        private string _cachedCsvText;
        private int _renderRequestId;
        private readonly DispatcherTimer _csvTextUpdateTimer;
        private bool _pendingCsvTextUpdateMarkDirty;
        private DataGridColumn _csvColumnSelectionAnchor;
        private object _csvRowSelectionAnchor;
        private CsvChartLineOrientation _csvLineOrientation = CsvChartLineOrientation.Auto;
        private static readonly Brush CsvTextBrush = CreateBrush("#414659");

        public event RoutedEventHandler EditorTextChange;
        //CompletionWindow completionWindow;

        public Editor()
        {
            InitializeComponent();
            var color = BpColors.Back_Margin_Color;
            edit.Background = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
            te = new TextEditor();
            te.TextArea.BorderStyle = System.Windows.Forms.BorderStyle.None;
            wfh.Child = te.TextArea;
            te.TextArea.DragEnter += TextArea_EditorTabDragEnter;
            te.TextArea.DragOver += TextArea_EditorTabDragOver;
            te.TextArea.DragLeave += TextArea_EditorTabDragLeave;
            te.TextArea.DragDrop += TextArea_EditorTabDragDrop;
            _csvTextUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _csvTextUpdateTimer.Tick += CsvTextUpdateTimer_Tick;
        }

        internal bool IsRenderedView
        {
            get { return _isRenderedView; }
        }

        internal void SetDocumentViewMode(string extension, bool rendered)
        {
            var normalized = (extension ?? "").ToLowerInvariant();
            var targetRendered = rendered && (normalized == ".md" || normalized == ".csv");
            if (_renderedExtension == normalized && _isRenderedView == targetRendered && IsCurrentDocumentViewVisible(normalized, targetRendered))
                return;

            if (_isRenderedView && !targetRendered)
                CommitRenderedViewChanges();

            _renderedExtension = normalized;
            _isRenderedView = targetRendered;
            _renderRequestId++;

            if (!_isRenderedView)
            {
                ShowTextEditor();
                return;
            }

            if (normalized == ".md")
                ShowMarkdownPreview();
            else if (normalized == ".csv")
                ShowCsvTable();
            else
                ShowTextEditor();
        }

        internal void RefreshRenderedView()
        {
            if (!_isRenderedView)
                return;

            _renderRequestId++;
            if (_renderedExtension == ".md")
                ShowMarkdownPreview();
            else if (_renderedExtension == ".csv")
                ShowCsvTable();
        }

        internal void CommitRenderedViewChanges()
        {
            if (csvDataGrid.Visibility != Visibility.Visible)
                return;

            var markDirty = _pendingCsvTextUpdateMarkDirty;
            _pendingCsvTextUpdateMarkDirty = false;
            _csvTextUpdateTimer.Stop();
            csvDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            csvDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
            UpdateCsvTextFromTable(markDirty);
        }

        public TextEditor GetTextEditor
        {
            get { return te; }
            //set { aEditor = value; }
        }

        private void edit_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (csvDataGrid.Visibility == Visibility.Visible && FindVisualParent<DataGrid>(e.OriginalSource as DependencyObject) != null)
                return;
            editorPopup.IsOpen = true;
        }

        private void edit_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!string.IsNullOrEmpty(te.ProgramName))
                MainWindowVM.ActivateProgramByName(te.ProgramName);
        }

        private void TextArea_EditorTabDragEnter(object sender, System.Windows.Forms.DragEventArgs e)
        {
            if (MainWindowVM.IsEditorTabDragData(e.Data))
            {
                EditorPanel.PreviewEditorTabDragFromWinForms(System.Windows.Forms.Cursor.Position);
                e.Effect = System.Windows.Forms.DragDropEffects.Move;
            }
        }

        private void TextArea_EditorTabDragOver(object sender, System.Windows.Forms.DragEventArgs e)
        {
            if (MainWindowVM.IsEditorTabDragData(e.Data))
            {
                EditorPanel.PreviewEditorTabDragFromWinForms(System.Windows.Forms.Cursor.Position);
                e.Effect = System.Windows.Forms.DragDropEffects.Move;
            }
        }

        private void TextArea_EditorTabDragLeave(object sender, EventArgs e)
        {
            EditorPanel.HideEditorTabDragPreview();
        }

        private void TextArea_EditorTabDragDrop(object sender, System.Windows.Forms.DragEventArgs e)
        {
            if (EditorPanel.DropEditorTabFromWinForms(e.Data, System.Windows.Forms.Cursor.Position))
                e.Effect = System.Windows.Forms.DragDropEffects.Move;
        }

        private void ShowTextEditor()
        {
            markdownPreviewHost.Visibility = Visibility.Collapsed;
            csvDataGrid.Visibility = Visibility.Collapsed;
            wfh.Visibility = Visibility.Visible;
            te.TextArea.Focus();
        }

        private async void ShowMarkdownPreview()
        {
            wfh.Visibility = Visibility.Collapsed;
            csvDataGrid.Visibility = Visibility.Collapsed;
            markdownPreviewHost.Visibility = Visibility.Visible;

            var text = te.TextArea.Text;
            var title = te.ProgramName;
            var requestId = _renderRequestId;
            if (_cachedMarkdownHtml != null && _cachedMarkdownText == text && _cachedMarkdownTitle == title)
            {
                markdownBrowser.NavigateToString(_cachedMarkdownHtml);
                return;
            }

            string html;
            try
            {
                html = await Task.Run(() => MarkdownPreviewHtmlRenderer.RenderHtml(text, title));
            }
            catch
            {
                return;
            }

            if (requestId != _renderRequestId
                || !_isRenderedView
                || _renderedExtension != ".md"
                || te.TextArea.Text != text
                || te.ProgramName != title)
                return;

            _cachedMarkdownText = text;
            _cachedMarkdownTitle = title;
            _cachedMarkdownHtml = html;
            markdownBrowser.NavigateToString(html);
        }

        private async void ShowCsvTable()
        {
            wfh.Visibility = Visibility.Collapsed;
            markdownPreviewHost.Visibility = Visibility.Collapsed;
            csvDataGrid.Visibility = Visibility.Visible;

            var text = te.TextArea.Text;
            var requestId = _renderRequestId;
            if (_csvDocument != null && _cachedCsvText == text)
            {
                BindCsvDocument(_csvDocument);
                return;
            }

            CsvTableDocument document;
            try
            {
                document = await Task.Run(() => CsvTableDocument.Parse(text));
            }
            catch
            {
                return;
            }

            if (requestId != _renderRequestId
                || !_isRenderedView
                || _renderedExtension != ".csv"
                || te.TextArea.Text != text)
                return;

            _csvDocument = document;
            _cachedCsvText = text;
            BindCsvDocument(_csvDocument);
        }

        private void BindCsvDocument(CsvTableDocument document)
        {
            if (document == null)
                return;

            TrackCsvChanges(document.Table);

            var currentView = csvDataGrid.ItemsSource;
            if (currentView == document.Table.DefaultView && CsvColumnsMatch(document.Table))
                return;

            if (!CsvColumnsMatch(document.Table))
            {
                csvDataGrid.Columns.Clear();
                _csvColumnSelectionAnchor = null;
                _csvRowSelectionAnchor = null;
                for (var i = 0; i < document.Table.Columns.Count; i++)
                {
                    var column = document.Table.Columns[i];
                    csvDataGrid.Columns.Add(new DataGridTextColumn
                    {
                        Header = column.Caption,
                        IsReadOnly = false,
                        CanUserSort = false,
                        Binding = new Binding("[" + column.ColumnName + "]")
                        {
                            Mode = BindingMode.TwoWay,
                            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                        },
                        ElementStyle = CreateCsvTextElementStyle(),
                        EditingElementStyle = CreateCsvTextEditingStyle(),
                        MinWidth = 72,
                        Width = new DataGridLength(EstimateCsvColumnWidth(document.Table, i))
                    });
                }
            }

            csvDataGrid.ItemsSource = document.Table.DefaultView;
        }

        private void TrackCsvChanges(DataTable table)
        {
            if (ReferenceEquals(_trackedCsvTable, table))
                return;

            if (_csvChangeTracker != null)
            {
                _csvChangeTracker.Changed -= CsvTable_Changed;
                _csvChangeTracker.Dispose();
            }

            _trackedCsvTable = table;
            _csvChangeTracker = table == null ? null : new CsvTableChangeTracker(table);
            if (_csvChangeTracker != null)
                _csvChangeTracker.Changed += CsvTable_Changed;
        }

        private void CsvTable_Changed(object sender, EventArgs e)
        {
            QueueCsvTextUpdate(true);
        }

        private bool CsvColumnsMatch(DataTable table)
        {
            if (table == null || csvDataGrid.Columns.Count != table.Columns.Count)
                return false;

            for (var i = 0; i < table.Columns.Count; i++)
            {
                var boundColumn = csvDataGrid.Columns[i] as DataGridTextColumn;
                if (boundColumn == null)
                    return false;

                if (!Equals(boundColumn.Header, table.Columns[i].Caption))
                    return false;
            }

            return true;
        }

        private static double EstimateCsvColumnWidth(DataTable table, int columnIndex)
        {
            if (table == null || columnIndex < 0 || columnIndex >= table.Columns.Count)
                return 96;

            var column = table.Columns[columnIndex];
            var maxLength = (column.Caption ?? "").Length;
            foreach (var value in table.Rows.Cast<DataRow>().Take(40).Select(row => Convert.ToString(row[column]) ?? ""))
                maxLength = Math.Max(maxLength, value.Length);

            return Math.Max(72, Math.Min(420, 28 + maxLength * 8));
        }

        private bool IsCurrentDocumentViewVisible(string normalizedExtension, bool rendered)
        {
            if (!rendered)
                return wfh.Visibility == Visibility.Visible;

            if (normalizedExtension == ".md")
                return markdownPreviewHost.Visibility == Visibility.Visible;

            if (normalizedExtension == ".csv")
                return csvDataGrid.Visibility == Visibility.Visible;

            return false;
        }

        private void InvalidateRenderedCaches()
        {
            _cachedMarkdownText = null;
            _cachedMarkdownTitle = null;
            _cachedMarkdownHtml = null;
            _cachedCsvText = null;
        }

        private static Style CreateCsvTextElementStyle()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, CsvTextBrush));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            return style;
        }

        private static Style CreateCsvTextEditingStyle()
        {
            var style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(TextBox.ForegroundProperty, CsvTextBrush));
            style.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(7, 3, 7, 3)));
            style.Setters.Add(new Setter(TextBox.BackgroundProperty, Brushes.White));
            return style;
        }

        private void CsvDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        }

        private void CsvDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            var columnHeader = FindVisualParent<DataGridColumnHeader>(source);
            if (columnHeader != null && columnHeader.Column != null)
            {
                _csvLineOrientation = CsvChartLineOrientation.SeriesByColumn;
                var extend = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                SelectCsvColumn(columnHeader.Column, extend ? _csvColumnSelectionAnchor : null);
                if (!extend || _csvColumnSelectionAnchor == null)
                    _csvColumnSelectionAnchor = columnHeader.Column;
                e.Handled = true;
                return;
            }

            var rowHeader = FindVisualParent<DataGridRowHeader>(source);
            if (rowHeader == null)
            {
                _csvLineOrientation = CsvChartLineOrientation.Auto;
                return;
            }

            var row = FindVisualParent<DataGridRow>(rowHeader);
            if (row == null || ReferenceEquals(row.Item, CollectionView.NewItemPlaceholder))
                return;

            _csvLineOrientation = CsvChartLineOrientation.SeriesByRow;
            var extendRows = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            SelectCsvRow(row.Item, extendRows ? _csvRowSelectionAnchor : null);
            if (!extendRows || _csvRowSelectionAnchor == null)
                _csvRowSelectionAnchor = row.Item;
            e.Handled = true;
        }

        private void CsvDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            var cell = FindVisualParent<DataGridCell>(source);
            var row = FindVisualParent<DataGridRow>(source);
            if (cell == null || row == null || cell.Column == null || ReferenceEquals(row.Item, CollectionView.NewItemPlaceholder))
                return;

            var selectedCell = new DataGridCellInfo(row.Item, cell.Column);
            if (csvDataGrid.SelectedCells.Contains(selectedCell))
                return;

            _csvLineOrientation = CsvChartLineOrientation.Auto;
            csvDataGrid.SelectedCells.Clear();
            csvDataGrid.SelectedCells.Add(selectedCell);
            csvDataGrid.CurrentCell = selectedCell;
        }

        private void CsvLineChartMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenCsvChart(CsvChartKind.Line);
        }

        private void CsvDistributionChartMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenCsvChart(CsvChartKind.Distribution);
        }

        private void CsvPieChartMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenCsvChart(CsvChartKind.Pie);
        }

        private void OpenCsvChart(CsvChartKind kind)
        {
            CommitRenderedViewChanges();
            var selectedCells = GetSelectedCsvChartCells();
            if (selectedCells.Count == 0)
            {
                MessageBox.Show("Выделите ячейки CSV для построения графика.", "CSV-график", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var data = CsvChartDataBuilder.Build(selectedCells, kind, _csvLineOrientation);
            if (!data.HasData)
            {
                var message = kind == CsvChartKind.Pie
                    ? "В выделении нет положительных числовых значений для круговой диаграммы."
                    : "В выделении нет числовых значений для построения графика.";
                MessageBox.Show(message, "CSV-график", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var chartWindow = new CsvChartWindow(data);
            var owner = Window.GetWindow(this);
            if (owner != null)
                chartWindow.Owner = owner;
            chartWindow.Show();
        }

        private System.Collections.Generic.IList<CsvChartCell> GetSelectedCsvChartCells()
        {
            var cells = new System.Collections.Generic.List<CsvChartCell>();
            if (_csvDocument == null)
                return cells;

            foreach (var selectedCell in csvDataGrid.SelectedCells)
            {
                var view = selectedCell.Item as DataRowView;
                var columnIndex = csvDataGrid.Columns.IndexOf(selectedCell.Column);
                if (view == null || columnIndex < 0 || columnIndex >= _csvDocument.Table.Columns.Count)
                    continue;

                var rowIndex = _csvDocument.Table.Rows.IndexOf(view.Row);
                if (rowIndex < 0)
                    continue;

                var column = _csvDocument.Table.Columns[columnIndex];
                cells.Add(new CsvChartCell(rowIndex, columnIndex, column.Caption, Convert.ToString(view.Row[column]) ?? ""));
            }

            return cells;
        }

        private void SelectCsvColumn(DataGridColumn column, DataGridColumn rangeStart)
        {
            csvDataGrid.SelectedCells.Clear();
            var firstColumnIndex = rangeStart == null ? csvDataGrid.Columns.IndexOf(column) : Math.Min(csvDataGrid.Columns.IndexOf(rangeStart), csvDataGrid.Columns.IndexOf(column));
            var lastColumnIndex = rangeStart == null ? firstColumnIndex : Math.Max(csvDataGrid.Columns.IndexOf(rangeStart), csvDataGrid.Columns.IndexOf(column));
            if (firstColumnIndex < 0 || lastColumnIndex < 0)
                return;
            DataGridCellInfo? firstCell = null;
            foreach (var item in csvDataGrid.Items)
            {
                if (ReferenceEquals(item, CollectionView.NewItemPlaceholder))
                    continue;

                for (var index = firstColumnIndex; index <= lastColumnIndex; index++)
                {
                    var candidate = csvDataGrid.Columns[index];
                    if (candidate.Visibility != Visibility.Visible)
                        continue;
                    var cell = new DataGridCellInfo(item, candidate);
                    csvDataGrid.SelectedCells.Add(cell);
                    if (!firstCell.HasValue)
                        firstCell = cell;
                }
            }

            if (firstCell.HasValue)
                csvDataGrid.CurrentCell = firstCell.Value;
        }

        private void SelectCsvRow(object item, object rangeStart)
        {
            csvDataGrid.SelectedCells.Clear();
            var items = csvDataGrid.Items
                .Cast<object>()
                .Where(candidate => !ReferenceEquals(candidate, CollectionView.NewItemPlaceholder))
                .ToList();
            var itemIndex = items.IndexOf(item);
            var startIndex = rangeStart == null ? itemIndex : items.IndexOf(rangeStart);
            if (itemIndex < 0 || startIndex < 0)
                return;
            var firstRowIndex = Math.Min(startIndex, itemIndex);
            var lastRowIndex = Math.Max(startIndex, itemIndex);
            DataGridCellInfo? firstCell = null;
            for (var rowIndex = firstRowIndex; rowIndex <= lastRowIndex; rowIndex++)
            {
                foreach (var column in csvDataGrid.Columns.Where(candidate => candidate.Visibility == Visibility.Visible))
                {
                    var cell = new DataGridCellInfo(items[rowIndex], column);
                    csvDataGrid.SelectedCells.Add(cell);
                    if (!firstCell.HasValue)
                        firstCell = cell;
                }
            }

            if (firstCell.HasValue)
                csvDataGrid.CurrentCell = firstCell.Value;
        }

        private static T FindVisualParent<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null)
            {
                var match = source as T;
                if (match != null)
                    return match;

                var contentElement = source as ContentElement;
                if (contentElement != null)
                {
                    source = ContentOperations.GetParent(contentElement);
                    var frameworkContentElement = contentElement as FrameworkContentElement;
                    if (source == null && frameworkContentElement != null)
                        source = frameworkContentElement.Parent;
                }
                else
                {
                    source = VisualTreeHelper.GetParent(source);
                }
            }

            return null;
        }

        private void CsvDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            QueueCsvTextUpdate(true);
        }

        private void CsvDataGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            QueueCsvTextUpdate(true);
        }

        private void QueueCsvTextUpdate(bool markDirty)
        {
            if (_isUpdatingCsvText || csvDataGrid.Visibility != Visibility.Visible)
                return;

            if (markDirty && !string.IsNullOrEmpty(te.ProgramName))
                MainWindowVM.MarkProgramTextChanged(te.ProgramName);

            _pendingCsvTextUpdateMarkDirty = _pendingCsvTextUpdateMarkDirty || markDirty;
            _csvTextUpdateTimer.Stop();
            _csvTextUpdateTimer.Start();
        }

        private void CsvTextUpdateTimer_Tick(object sender, EventArgs e)
        {
            _csvTextUpdateTimer.Stop();
            var markDirty = _pendingCsvTextUpdateMarkDirty;
            _pendingCsvTextUpdateMarkDirty = false;
            UpdateCsvTextFromTable(markDirty);
        }

        private void UpdateCsvTextFromTable(bool markDirty)
        {
            if (_csvDocument == null || _isUpdatingCsvText)
                return;

            try
            {
                _isUpdatingCsvText = true;
                var text = _csvDocument.ToCsv();
                if (te.TextArea.Text != text)
                {
                    var readOnly = te.TextArea.ReadOnly;
                    te.TextArea.ReadOnly = false;
                    te.TextArea.Text = text;
                    te.TextArea.ReadOnly = readOnly;
                }

                _cachedCsvText = text;
                if (markDirty && !string.IsNullOrEmpty(te.ProgramName))
                    MainWindowVM.MarkProgramTextChanged(te.ProgramName);
            }
            finally
            {
                _isUpdatingCsvText = false;
            }
        }

        private static Brush CreateBrush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        public Popup GetTextEditorPopup
        {
            get { return editorPopup; }
        }

        public Popup GetTextEditorTools
        {
            get { return editorTools; }
        }

        public string SetTextEditorToolsText
        {
            get
            {
                return editorToolsText.Text;
            }
            set
            {
                editorToolsText.Text = value;
            }
        }

        public System.Windows.Forms.Integration.WindowsFormsHost GetHost
        {
            get { return wfh; }
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            this.EditorTextChange -= TextEditorEvents.Editor_Change;
            CommitRenderedViewChanges();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            this.EditorTextChange += TextEditorEvents.Editor_Change;
        }
    }
}
