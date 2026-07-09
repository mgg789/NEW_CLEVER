using Clever.Model.Bplus;
using Clever.Model.Utils;
using Clever.View.Controls;
using Clever.ViewModel;
using System;
using System.Data;
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
        }

        internal bool IsRenderedView
        {
            get { return _isRenderedView; }
        }

        internal void SetDocumentViewMode(string extension, bool rendered)
        {
            CommitRenderedViewChanges();

            var normalized = (extension ?? "").ToLowerInvariant();
            _renderedExtension = normalized;
            _isRenderedView = rendered && (normalized == ".md" || normalized == ".csv");

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

            if (_renderedExtension == ".md")
                ShowMarkdownPreview();
            else if (_renderedExtension == ".csv")
                ShowCsvTable();
        }

        internal void CommitRenderedViewChanges()
        {
            if (csvDataGrid.Visibility != Visibility.Visible)
                return;

            csvDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            csvDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
            UpdateCsvTextFromTable(false);
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

        private void ShowMarkdownPreview()
        {
            wfh.Visibility = Visibility.Collapsed;
            csvDataGrid.Visibility = Visibility.Collapsed;
            markdownPreviewHost.Visibility = Visibility.Visible;

            var html = MarkdownPreviewHtmlRenderer.RenderHtml(te.TextArea.Text, te.ProgramName);
            markdownBrowser.NavigateToString(html);
        }

        private void ShowCsvTable()
        {
            wfh.Visibility = Visibility.Collapsed;
            markdownPreviewHost.Visibility = Visibility.Collapsed;
            csvDataGrid.Visibility = Visibility.Visible;

            _csvDocument = CsvTableDocument.Parse(te.TextArea.Text);
            csvDataGrid.Columns.Clear();
            for (var i = 0; i < _csvDocument.Table.Columns.Count; i++)
            {
                var column = _csvDocument.Table.Columns[i];
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
                    Width = new DataGridLength(1, DataGridLengthUnitType.Auto)
                });
            }

            csvDataGrid.ItemsSource = _csvDocument.Table.DefaultView;
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

            Dispatcher.BeginInvoke(new Action(() => UpdateCsvTextFromTable(markDirty)), DispatcherPriority.Background);
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
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
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
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            this.EditorTextChange += TextEditorEvents.Editor_Change;
        }
    }
}
