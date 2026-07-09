using Clever.CommonData;
using Clever.Model.Utils;
using Clever.ViewModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Clever.View.Dialogs
{
    public partial class SetNames : Window
    {
        private readonly Dictionary<string, int> _extensionIndexes;
        private bool _updatingFileType;

        public SetNames(string prgN, string modN, string incN)
        {
            InitializeComponent();

            _extensionIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { ".bp", 0 },
                { ".bpm", 1 },
                { ".bpi", 2 },
                { ".md", 3 },
                { ".txt", 4 },
                { ".csv", 5 }
            };

            projectName.Text = MainWindowVM.CurrentProjectName == "" ? "Project" : MainWindowVM.CurrentProjectName;
            fileName.Text = Path.GetFileNameWithoutExtension(prgN);
            fileType.SelectedIndex = 0;

            butTextProject.Text = Text("winCreate", "Create");
            butTextFile.Text = Text("winCreate", "Create");
            butTextClose.Text = Text("winCancel", "Cancel");
            txtProjectTitle.Text = Text("ProjectNew", "New project");
            txtProjectName.Text = Text("ProjectName", "Project name") + ":";
            txtFileTitle.Text = Text("FileNew", "New file");
            txtFileName.Text = Text("FileName", "Filename") + ":";
            ApplyFileTypeLocalization();

            fileName.Focus();
        }

        public bool IsProject { get; private set; }
        public string ProjectName { get; private set; }
        public string GetName { get; set; }

        private void butCreateProject_Click(object sender, RoutedEventArgs e)
        {
            SubmitProject();
        }

        private void butCreateFile_Click(object sender, RoutedEventArgs e)
        {
            SubmitFile();
        }

        private void butClose_Click(object sender, RoutedEventArgs e)
        {
            GetName = "";
            DialogResult = false;
        }

        private void projectName_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                SubmitProject();
        }

        private void fileName_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                SubmitFile();
        }

        private void fileName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_updatingFileType)
                return;

            var extension = Path.GetExtension(fileName.Text.Trim());
            int index;
            if (!string.IsNullOrWhiteSpace(extension) && _extensionIndexes.TryGetValue(extension, out index))
            {
                _updatingFileType = true;
                fileType.SelectedIndex = index;
                _updatingFileType = false;
            }
        }

        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                GetName = "";
                DialogResult = false;
            }
        }

        private void SubmitProject()
        {
            var name = projectName.Text.Trim();
            if (!IsValidSimpleName(name))
                return;

            IsProject = true;
            ProjectName = name;
            GetName = "";
            DialogResult = true;
        }

        private void SubmitFile()
        {
            var normalizedName = NormalizeFileName(fileName.Text.Trim());
            if (!IsValidSimpleName(Path.GetFileNameWithoutExtension(normalizedName)))
                return;

            IsProject = false;
            ProjectName = "";
            GetName = normalizedName;
            DialogResult = true;
        }

        private string NormalizeFileName(string rawName)
        {
            var extension = Path.GetExtension(rawName);
            if (!string.IsNullOrWhiteSpace(extension) && MainWindowVM.IsSupportedEditorExtension(extension))
                return rawName;

            return rawName + SelectedExtension();
        }

        private string SelectedExtension()
        {
            var selected = fileType.SelectedItem as ComboBoxItem;
            if (selected == null || selected.Tag == null)
                return MainWindowVM.ext;

            return selected.Tag.ToString();
        }

        private void ApplyFileTypeLocalization()
        {
            foreach (var item in fileType.Items)
            {
                var comboBoxItem = item as ComboBoxItem;
                if (comboBoxItem == null || comboBoxItem.Tag == null)
                    continue;

                var extension = comboBoxItem.Tag.ToString();
                switch (extension)
                {
                    case ".bp":
                        comboBoxItem.Content = Text("FileTypeProgram", "Program (.bp)");
                        break;
                    case ".bpm":
                        comboBoxItem.Content = Text("FileTypeModule", "Module (.bpm)");
                        break;
                    case ".bpi":
                        comboBoxItem.Content = Text("FileTypeInclude", "Include (.bpi)");
                        break;
                    case ".md":
                        comboBoxItem.Content = Text("FileTypeMarkdown", "Markdown (.md)");
                        break;
                    case ".txt":
                        comboBoxItem.Content = Text("FileTypeText", "Text (.txt)");
                        break;
                    case ".csv":
                        comboBoxItem.Content = Text("FileTypeTable", "Table (.csv)");
                        break;
                }
            }
        }

        private bool IsValidSimpleName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;

            return new ValidName().Valid(name);
        }

        private static string Text(string key, string fallback)
        {
            var value = GUILanguage.GetItem(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
