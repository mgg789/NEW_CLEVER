using Clever.Model.Bplus;
using Clever.Model.Utils;
using Clever.View.Controls;
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

        private static readonly Brush[] CsvColumnBrushes =
        {
            CreateBrush("#4FAF5B"),
            CreateBrush("#B98542"),
            CreateBrush("#6AA16F"),
            CreateBrush("#D5655F"),
            CreateBrush("#D15F91"),
            CreateBrush("#A0B847"),
            CreateBrush("#35A2C6")
        };

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

            var currentView = csvDataGrid.ItemsSource;
            if (currentView == document.Table.DefaultView && CsvColumnsMatch(document.Table))
                return;

            if (!CsvColumnsMatch(document.Table))
            {
                csvDataGrid.Columns.Clear();
                for (var i = 0; i < document.Table.Columns.Count; i++)
                {
                    var column = document.Table.Columns[i];
                    var brush = CsvColumnBrushes[i % CsvColumnBrushes.Length];
                    csvDataGrid.Columns.Add(new DataGridTextColumn
                    {
                        Header = column.Caption,
                        Binding = new Binding("[" + column.ColumnName + "]")
                        {
                            Mode = BindingMode.TwoWay,
                            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                        },
                        ElementStyle = CreateCsvTextElementStyle(brush),
                        EditingElementStyle = CreateCsvTextEditingStyle(brush),
                        MinWidth = 72,
                        Width = new DataGridLength(EstimateCsvColumnWidth(document.Table, i))
                    });
                }
            }

            csvDataGrid.ItemsSource = document.Table.DefaultView;
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

        private static Style CreateCsvTextElementStyle(Brush brush)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, brush));
            style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            return style;
        }

        private static Style CreateCsvTextEditingStyle(Brush brush)
        {
            var style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(TextBox.ForegroundProperty, brush));
            style.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(7, 3, 7, 3)));
            style.Setters.Add(new Setter(TextBox.BackgroundProperty, Brushes.White));
            return style;
        }

        private void CsvDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        }

        private void CsvDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            QueueCsvTextUpdate(true);
        }

        private void CsvDataGrid_CurrentCellChanged(object sender, EventArgs e)
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
