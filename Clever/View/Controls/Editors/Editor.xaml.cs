using Clever.Model.Bplus;
using Clever.Model.Utils;
using Clever.View.Controls;
using Clever.ViewModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;

namespace Clever.View.Controls.Editors
{
    /// <summary>
    /// Логика взаимодействия для Editor.xaml
    /// </summary>
    public partial class Editor : System.Windows.Controls.UserControl
    {
        TextEditor te;
        public event RoutedEventHandler EditorTextChange;
        //CompletionWindow completionWindow;

        public Editor()
        {
            InitializeComponent();
            var color = BpColors.Back_Margin_Color;
            edit.Background = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
            te = new TextEditor();
            te.TextArea.BorderStyle = BorderStyle.None;
            wfh.Child = te.TextArea;
            te.TextArea.DragEnter += TextArea_EditorTabDragEnter;
            te.TextArea.DragOver += TextArea_EditorTabDragOver;
            te.TextArea.DragLeave += TextArea_EditorTabDragLeave;
            te.TextArea.DragDrop += TextArea_EditorTabDragDrop;
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

        private void TextArea_EditorTabDragLeave(object sender, System.EventArgs e)
        {
            EditorPanel.HideEditorTabDragPreview();
        }

        private void TextArea_EditorTabDragDrop(object sender, System.Windows.Forms.DragEventArgs e)
        {
            if (EditorPanel.DropEditorTabFromWinForms(e.Data, System.Windows.Forms.Cursor.Position))
                e.Effect = System.Windows.Forms.DragDropEffects.Move;
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
