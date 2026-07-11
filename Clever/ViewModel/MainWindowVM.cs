using Clever.Model.Bplus;
using Clever.Model.Program;
using Clever.Model.Diagnostics;
using Clever.Model.Utils;
using Clever.View.Controls;
using Clever.View.Controls.Editors;
using Clever.CommonData;
using ScintillaNET;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clever.ViewModel.BaseVM;
using Clever.ViewModel.PanelsVM;
using Clever.Model.Intellisense;

namespace Clever.ViewModel
{
    class MainWindowVM : INotifyPropertyChanged
    {
        public ICommand FileNewCommand { get; set; }
        public ICommand FileOpenCommand { get; set; }
        public ICommand FileSaveCommand { get; set; }
        public ICommand FileSaveAllCommand { get; set; }
        public ICommand FileSaveAsCommand { get; set; }
        public ICommand FileCloseAllCommand { get; set; }
        public ICommand OpenProjectCommand { get; set; }
        public ICommand EditCopyCommand { get; set; }
        public ICommand EditPasteCommand { get; set; }
        public ICommand EditCutCommand { get; set; }
        public ICommand EditUndoCommand { get; set; }
        public ICommand EditRedoCommand { get; set; }
        public ICommand EditSelectAllCommand { get; set; }
        public ICommand EditFormatCommand { get; set; }
        public ICommand EditShowLineCommand { get; set; }
        public ICommand EditShowNumberCommand { get; set; }
        public ICommand EditShowSpaceCommand { get; set; }
        public ICommand EditWrapCommand { get; set; }
        public ICommand EditShowLineMenuCommand { get; set; }
        public ICommand EditShowNumberMenuCommand { get; set; }
        public ICommand EditShowSpaceMenuCommand { get; set; }
        public ICommand EditWrapMenuCommand { get; set; }
        public ICommand EditCommentCommand { get; set; }
        public ICommand EditUnCommentCommand { get; set; }
        public ICommand EditFoldAllCommand { get; set; }
        public ICommand EditUnFoldAllCommand { get; set; }
        public ICommand EditFoldFuncCommand { get; set; }
        public ICommand EditUnFoldFuncCommand { get; set; }
        public ICommand CloseProgramCommand { get; set; }
        
        public ICommand FindCommand { get; set; }
        public ICommand GetProgramNamesCommand { get; set; }
        public ICommand SplitSideBySideCommand { get; set; }
        public ICommand SplitTopBottomCommand { get; set; }
        public ICommand ToggleRightSidebarCommand { get; set; }
        public ICommand OpenAiChatCommand { get; set; }
        public ICommand ToggleAiCodeCompletionsCommand { get; set; }
        public ICommand FixBugsCommand { get; set; }
        public ICommand EditorRawViewCommand { get; set; }
        public ICommand EditorRenderedViewCommand { get; set; }
        public ICommand AboutCommand { get; set; }

        public static ProjectData Project;
        public static string ext = ".bp";
        internal static readonly string[] SupportedEditorExtensions = { ".bp", ".bpm", ".bpi", ".md", ".txt", ".csv", ".ms" };
        static string currentPath = "";
        private static MainWindowVM _instance;
        private const string BaseWindowTitle = "CLEVƎR v1.8.0-B";
        private static string _currentProjectRoot = "";

        Elements elements;
        BPInterop interop;

        public static SearchFlags searchFlags = SearchFlags.None;
        public static bool highlightAll = false;

        public MainWindowVM()
        {
            _instance = this;
            CurrentName = "";
            Project = new ProjectData();

            //=====================
            //Brick
            BrickConnect = false;
            //=====================

            elements = new Elements();
            _programNameList = new ObservableCollection<TabItem>();
            _secondaryProgramNameList = new ObservableCollection<TabItem>();
            EditorPaneRows = 1;
            EditorPaneColumns = 1;
            SecondaryEditorVisibility = Visibility.Collapsed;
            ApplySingleEditorLayout();

            FileNewCommand = new RelayCommand(param => FileNew(), true);
            FileOpenCommand = new RelayCommand(param => FileOpen(), true);
            FileSaveCommand = new RelayCommand(param => FileSave(), true);
            FileSaveAllCommand = new RelayCommand(param => FileSaveAll(), true);
            FileSaveAsCommand = new RelayCommand(param => FileSaveAs(), true);
            FileCloseAllCommand = new RelayCommand(param => FileCloseAll(), true);
            OpenProjectCommand = new RelayCommand(param => OpenProject(), true);
            EditCopyCommand = new RelayCommand(param => EditCopy(), true);
            EditPasteCommand = new RelayCommand(param => EditPaste(), true);
            EditCutCommand = new RelayCommand(param => EditCut(), true);
            EditUndoCommand = new RelayCommand(param => EditUndo(), true);
            EditRedoCommand = new RelayCommand(param => EditRedo(), true);
            EditSelectAllCommand = new RelayCommand(param => EditSelectAll(), true);
            EditFormatCommand = new RelayCommand(param => EditFormat(), true);
            EditShowLineCommand = new RelayCommand(param => EditShowLine(), true);
            EditShowNumberCommand = new RelayCommand(param => EditShowNumber(), true);
            EditShowSpaceCommand = new RelayCommand(param => EditShowSpace(), true);
            EditWrapCommand = new RelayCommand(param => EditWrap(), true);
            EditShowLineMenuCommand = new RelayCommand(param => EditShowLineMenu(), true);
            EditShowNumberMenuCommand = new RelayCommand(param => EditShowNumberMenu(), true);
            EditShowSpaceMenuCommand = new RelayCommand(param => EditShowSpaceMenu(), true);
            EditWrapMenuCommand = new RelayCommand(param => EditWrapMenu(), true);

            EditCommentCommand = new RelayCommand(param => EditComment(), true);
            EditUnCommentCommand = new RelayCommand(param => EditUnComment(), true);
            EditFoldAllCommand = new RelayCommand(param => EditFoldAll(), true);
            EditUnFoldAllCommand = new RelayCommand(param => EditUnFoldAll(), true);
            EditFoldFuncCommand = new RelayCommand(param => EditFoldFunc(), true);
            EditUnFoldFuncCommand = new RelayCommand(param => EditUnFoldFunc(), true);


            CloseProgramCommand = new RelayCommand(param => CloseProgram(), true);
            FindCommand = new RelayCommand(param => FindText(), true);
            GetProgramNamesCommand = new RelayCommand(param => SetProgramName(), true);
            SplitSideBySideCommand = new RelayCommand(param => ToggleEditorSplit(true), true);
            SplitTopBottomCommand = new RelayCommand(param => ToggleEditorSplit(false), true);
            ToggleRightSidebarCommand = new RelayCommand(param => ToggleRightSidebar(), true);
            OpenAiChatCommand = new RelayCommand(param => OpenAiChatSidebar(), true);
            ToggleAiCodeCompletionsCommand = new RelayCommand(param => ToggleAiCodeCompletions(), true);
            FixBugsCommand = new RelayCommand(param => OpenFixBugs(), true);
            EditorRawViewCommand = new RelayCommand(param => SwitchCurrentDocumentView(false), true);
            EditorRenderedViewCommand = new RelayCommand(param => SwitchCurrentDocumentView(true), true);
            AboutCommand = new RelayCommand(param => About(), true);
            AiAppSettingsStore.SettingsChanged += AiAppSettingsStore_SettingsChanged;
            IdeProblemStore.Changed += IdeProblemStore_Changed;

            ShowNumber = Configurations.Get.ShowNumber;
            EditShowNumber();
            ShowLine = Configurations.Get.ShowLine;
            EditShowLine();
            ShowSpace = Configurations.Get.ShowSpace;
            EditShowSpace();
            ShowWrap = Configurations.Get.Wrap;
            EditWrap();

            interop = new BPInterop();
            SpWidth = 300;

            if (Configurations.Get.File_New == true) FileNewV = Visibility.Visible;
            else FileNewV = Visibility.Collapsed;
            if (Configurations.Get.File_Open == true) FileOpenV = Visibility.Visible;
            else FileOpenV = Visibility.Collapsed;
            if (Configurations.Get.File_CloseAll == true) FileCloseAllV = Visibility.Visible;
            else FileCloseAllV = Visibility.Collapsed;
            if (Configurations.Get.File_Save == true) FileSaveV = Visibility.Visible;
            else FileSaveV = Visibility.Collapsed;
            if (Configurations.Get.File_SaveAll == true) FileSaveAllV = Visibility.Visible;
            else FileSaveAllV = Visibility.Collapsed;
            if (Configurations.Get.File_SaveAs == true) FileSaveAsV = Visibility.Visible;
            else FileSaveAsV = Visibility.Collapsed;
            if (Configurations.Get.Edit_Copy == true) EditCopyV = Visibility.Visible;
            else EditCopyV = Visibility.Collapsed;
            if (Configurations.Get.Edit_Paste == true) EditPasteV = Visibility.Visible;
            else EditPasteV = Visibility.Collapsed;
            if (Configurations.Get.Edit_Cut == true) EditCutV = Visibility.Visible;
            else EditCutV = Visibility.Collapsed;
            if (Configurations.Get.Edit_Undo == true) EditUndoV = Visibility.Visible;
            else EditUndoV = Visibility.Collapsed;
            if (Configurations.Get .Edit_Redo == true) EditRedoV = Visibility.Visible;
            else EditRedoV = Visibility.Collapsed;
            if (Configurations.Get.Edit_SelectAll == true) EditSelectAllV = Visibility.Visible;
            else EditSelectAllV = Visibility.Collapsed;
            if (Configurations.Get.Edit_Format == true) EditFormatV = Visibility.Visible;
            else EditFormatV = Visibility.Collapsed;
            if (Configurations.Get.Edit_ShowLine == true) EditShowLineV = Visibility.Visible;
            else EditShowLineV = Visibility.Collapsed;
            if (Configurations.Get.Edit_ShowNumber == true) EditShowNumberV = Visibility.Visible;
            else EditShowNumberV = Visibility.Collapsed;
            if (Configurations.Get.Edit_ShowSpace == true) EditShowSpaceV = Visibility.Visible;
            else EditShowSpaceV = Visibility.Collapsed;
            if (Configurations.Get.Edit_Wrap == true) EditWrapV = Visibility.Visible;
            else EditWrapV = Visibility.Collapsed;
            if (Configurations.Get.Edit_Find == true) EditFindV = Visibility.Visible;
            else EditFindV = Visibility.Collapsed;

            if (Configurations.Get.Brick_Compile == true) CompileV = Visibility.Visible;
            else CompileV = Visibility.Collapsed;
            if (Configurations.Get.Brick_CompileAndRun == true) CompileAndRunV = Visibility.Visible;
            else CompileAndRunV = Visibility.Collapsed;
            if (Configurations.Get.Brick_Connect == true) ConnectV = Visibility.Visible;
            else ConnectV = Visibility.Collapsed;
            if (Configurations.Get.Brick_Download == true) DownloadV = Visibility.Visible;
            else DownloadV = Visibility.Collapsed;
            if (Configurations.Get.Brick_Stop == true) StopV = Visibility.Visible;
            else StopV = Visibility.Collapsed;

            HiddenProgramButton = Visibility.Hidden;

            IntellisenseParser.Install();

            Topmost();
        }

        private static void Topmost()
        {
            Application.Current.MainWindow.Topmost = true;
            Application.Current.MainWindow.Topmost = false;
        }

        private static void StartNewServer()
        {
            var server = new CleverServer();
            server.Start();
        }

        public string CurrentWindowTitle
        {
            get { return string.IsNullOrWhiteSpace(CurrentProjectName) ? BaseWindowTitle : BaseWindowTitle + " | " + CurrentProjectName; }
        }

        public Visibility EmptyProjectStartVisibility
        {
            get
            {
                return string.IsNullOrWhiteSpace(CurrentProjectRoot) && GetOpenProgramCount() == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        internal static string CurrentProjectRoot
        {
            get { return _currentProjectRoot; }
        }

        internal static string CurrentProjectName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_currentProjectRoot))
                    return "";

                try
                {
                    return new DirectoryInfo(_currentProjectRoot).Name;
                }
                catch
                {
                    return "";
                }
            }
        }

        internal static bool IsSupportedEditorExtension(string extension)
        {
            return SupportedEditorExtensions.Any(item => string.Equals(item, extension, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsBasicPlusExtension(string extension)
        {
            return string.Equals(extension, ".bp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bpm", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bpi", StringComparison.OrdinalIgnoreCase);
        }

        internal static void RequestNewProject()
        {
            if (_instance != null)
                _instance.FileNew();
        }

        internal static void RequestOpenProject()
        {
            if (_instance != null)
                _instance.OpenProject();
        }

        private static void SetCurrentProjectRoot(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
                return;

            string normalizedRoot;
            try
            {
                normalizedRoot = Path.GetFullPath(projectRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return;
            }

            if (string.Equals(_currentProjectRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                RefreshOpenFileProjectMembership();
                NotifyProjectContextChanged();
                return;
            }

            _currentProjectRoot = normalizedRoot;
            if (Project != null)
                Project.ProjectName = CurrentProjectName;

            RefreshOpenFileProjectMembership();
            NotifyProjectContextChanged();
        }

        private static void EnsureProjectRootFromFile(string fullPath)
        {
            if (!string.IsNullOrWhiteSpace(CurrentProjectRoot) || string.IsNullOrWhiteSpace(fullPath))
                return;

            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(fullPath));
                if (!string.IsNullOrWhiteSpace(directory))
                    SetCurrentProjectRoot(directory);
            }
            catch
            {
            }
        }

        internal static bool IsPathInsideCurrentProject(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(CurrentProjectRoot) || string.IsNullOrWhiteSpace(fullPath))
                return true;

            try
            {
                var root = Path.GetFullPath(CurrentProjectRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var path = Path.GetFullPath(fullPath);
                return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        private static void ApplyProjectMembership(ProgramData data)
        {
            if (data == null || data.IsPreview)
                return;

            data.IsExternalProjectFile = !string.IsNullOrWhiteSpace(CurrentProjectRoot)
                && !string.IsNullOrWhiteSpace(data.FullPath)
                && !IsPathInsideCurrentProject(data.FullPath);
        }

        private static void RefreshOpenFileProjectMembership()
        {
            if (Project == null)
                return;

            foreach (var data in Project.GetDictionary().Values)
                ApplyProjectMembership(data);

            Project.UpdateColor();
        }

        private static void NotifyProjectContextChanged()
        {
            OnStaticPropertyChanged("CurrentWindowTitle");
            OnStaticPropertyChanged("EmptyProjectStartVisibility");
            GitPanelVM.NotifyProjectContextChanged();
        }

        private static void NotifyEditorContentChanged()
        {
            OnStaticPropertyChanged("EmptyProjectStartVisibility");
        }

        private static ObservableCollection<TabItem> _programNameList;
        public static ObservableCollection<TabItem> ProgramNameList
        {
            get { return _programNameList; }
            set
            {
                _programNameList = value;


                if (_programNameList.Count <= 1)
                {
                    OnStaticPropertyChanged("ProgramNameList");
                    return;
                }

                double wh = 150;

                for (int i = 0; i < _programNameList.Count; i++)
                {
                    TabItem item = _programNameList[i];
                    var h = (EditorHeader)item.Header;

                    if (i == 0 && h.GetWidth == 0)
                    {
                        wh += h.GetWidth + 1;
                    }
                    else if (i > 0)
                    {
                        wh += h.GetWidth + 1;
                    }

                    if (wh <= MainWindow.GetWidthGrid - 50)
                    {
                        item.Visibility = Visibility.Visible;
                        HiddenProgramButton = Visibility.Hidden;
                    }
                    else
                    {
                        item.Visibility = Visibility.Collapsed;
                        HiddenProgramButton = Visibility.Visible;
                    }
                }
                OnStaticPropertyChanged("ProgramNameList");
            }
        }

        private static ObservableCollection<TabItem> _secondaryProgramNameList;
        public static ObservableCollection<TabItem> SecondaryProgramNameList
        {
            get { return _secondaryProgramNameList; }
            set
            {
                _secondaryProgramNameList = value;
                OnStaticPropertyChanged("SecondaryProgramNameList");
            }
        }

        private static TabItem _primaryCurrentProgram;
        public static TabItem PrimaryCurrentProgram
        {
            get { return _primaryCurrentProgram; }
            set
            {
                _primaryCurrentProgram = value;
                OnStaticPropertyChanged("PrimaryCurrentProgram");

                if (!_syncingEditorSelection && value != null)
                    CurrentProgram = value;
            }
        }

        private static TabItem _secondaryCurrentProgram;
        public static TabItem SecondaryCurrentProgram
        {
            get { return _secondaryCurrentProgram; }
            set
            {
                _secondaryCurrentProgram = value;
                OnStaticPropertyChanged("SecondaryCurrentProgram");

                if (!_syncingEditorSelection && value != null)
                    CurrentProgram = value;
            }
        }

        private static int _editorPaneRows;
        public static int EditorPaneRows
        {
            get { return _editorPaneRows; }
            private set
            {
                _editorPaneRows = value;
                OnStaticPropertyChanged("EditorPaneRows");
            }
        }

        private static int _editorPaneColumns;
        public static int EditorPaneColumns
        {
            get { return _editorPaneColumns; }
            private set
            {
                _editorPaneColumns = value;
                OnStaticPropertyChanged("EditorPaneColumns");
            }
        }

        private static Visibility _secondaryEditorVisibility;
        public static Visibility SecondaryEditorVisibility
        {
            get { return _secondaryEditorVisibility; }
            private set
            {
                _secondaryEditorVisibility = value;
                OnStaticPropertyChanged("SecondaryEditorVisibility");
            }
        }

        private static Visibility _editorColumnSplitterVisibility;
        public static Visibility EditorColumnSplitterVisibility
        {
            get { return _editorColumnSplitterVisibility; }
            private set
            {
                _editorColumnSplitterVisibility = value;
                OnStaticPropertyChanged("EditorColumnSplitterVisibility");
            }
        }

        private static Visibility _editorRowSplitterVisibility;
        public static Visibility EditorRowSplitterVisibility
        {
            get { return _editorRowSplitterVisibility; }
            private set
            {
                _editorRowSplitterVisibility = value;
                OnStaticPropertyChanged("EditorRowSplitterVisibility");
            }
        }

        private static Thickness _secondaryEditorBorderThickness;
        public static Thickness SecondaryEditorBorderThickness
        {
            get { return _secondaryEditorBorderThickness; }
            private set
            {
                _secondaryEditorBorderThickness = value;
                OnStaticPropertyChanged("SecondaryEditorBorderThickness");
            }
        }

        private static Thickness _secondaryEditorMargin;
        public static Thickness SecondaryEditorMargin
        {
            get { return _secondaryEditorMargin; }
            private set
            {
                _secondaryEditorMargin = value;
                OnStaticPropertyChanged("SecondaryEditorMargin");
            }
        }

        private static GridLength _editorFirstColumnWidth;
        public static GridLength EditorFirstColumnWidth
        {
            get { return _editorFirstColumnWidth; }
            private set
            {
                _editorFirstColumnWidth = value;
                OnStaticPropertyChanged("EditorFirstColumnWidth");
            }
        }

        private static GridLength _editorSecondColumnWidth;
        public static GridLength EditorSecondColumnWidth
        {
            get { return _editorSecondColumnWidth; }
            private set
            {
                _editorSecondColumnWidth = value;
                OnStaticPropertyChanged("EditorSecondColumnWidth");
            }
        }

        private static GridLength _editorFirstRowHeight;
        public static GridLength EditorFirstRowHeight
        {
            get { return _editorFirstRowHeight; }
            private set
            {
                _editorFirstRowHeight = value;
                OnStaticPropertyChanged("EditorFirstRowHeight");
            }
        }

        private static GridLength _editorSecondRowHeight;
        public static GridLength EditorSecondRowHeight
        {
            get { return _editorSecondRowHeight; }
            private set
            {
                _editorSecondRowHeight = value;
                OnStaticPropertyChanged("EditorSecondRowHeight");
            }
        }

        private static int _primaryEditorRow;
        public static int PrimaryEditorRow
        {
            get { return _primaryEditorRow; }
            private set
            {
                _primaryEditorRow = value;
                OnStaticPropertyChanged("PrimaryEditorRow");
            }
        }

        private static int _primaryEditorColumn;
        public static int PrimaryEditorColumn
        {
            get { return _primaryEditorColumn; }
            private set
            {
                _primaryEditorColumn = value;
                OnStaticPropertyChanged("PrimaryEditorColumn");
            }
        }

        private static int _primaryEditorRowSpan;
        public static int PrimaryEditorRowSpan
        {
            get { return _primaryEditorRowSpan; }
            private set
            {
                _primaryEditorRowSpan = value;
                OnStaticPropertyChanged("PrimaryEditorRowSpan");
            }
        }

        private static int _primaryEditorColumnSpan;
        public static int PrimaryEditorColumnSpan
        {
            get { return _primaryEditorColumnSpan; }
            private set
            {
                _primaryEditorColumnSpan = value;
                OnStaticPropertyChanged("PrimaryEditorColumnSpan");
            }
        }

        private static int _secondaryEditorRow;
        public static int SecondaryEditorRow
        {
            get { return _secondaryEditorRow; }
            private set
            {
                _secondaryEditorRow = value;
                OnStaticPropertyChanged("SecondaryEditorRow");
            }
        }

        private static int _secondaryEditorColumn;
        public static int SecondaryEditorColumn
        {
            get { return _secondaryEditorColumn; }
            private set
            {
                _secondaryEditorColumn = value;
                OnStaticPropertyChanged("SecondaryEditorColumn");
            }
        }

        private static int _secondaryEditorRowSpan;
        public static int SecondaryEditorRowSpan
        {
            get { return _secondaryEditorRowSpan; }
            private set
            {
                _secondaryEditorRowSpan = value;
                OnStaticPropertyChanged("SecondaryEditorRowSpan");
            }
        }

        private static int _secondaryEditorColumnSpan;
        public static int SecondaryEditorColumnSpan
        {
            get { return _secondaryEditorColumnSpan; }
            private set
            {
                _secondaryEditorColumnSpan = value;
                OnStaticPropertyChanged("SecondaryEditorColumnSpan");
            }
        }

        private static bool _isEditorSplitActive;
        internal static bool IsEditorSplitActive
        {
            get { return _isEditorSplitActive; }
        }

        private static bool _syncingEditorSelection;
        private static bool _isEditorSplitSideBySide = true;
        private static bool _rightSidebarHidden;
        private static GridLength _lastEditorColumnWidth;
        private static GridLength _lastRightSidebarColumnWidth;
        internal const string EditorTabDragFormat = "Clever.EditorTabName";
        private const double RightSidebarMinWidth = 330;
        private const double RightSidebarSplitterWidth = 4;

        private static TabItem _currentProgram;
        private static readonly Brush DocumentViewActiveBackground = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xD9));
        private static readonly Brush DocumentViewInactiveBackground = new SolidColorBrush(Color.FromRgb(0xEA, 0xED, 0xF4));
        private static readonly Brush DocumentViewActiveForeground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly Brush DocumentViewInactiveForeground = new SolidColorBrush(Color.FromRgb(0x60, 0x69, 0x85));

        public static TabItem CurrentProgram
        {
            get { return _currentProgram; }
            set
            {
                _currentProgram = value;
                _syncingEditorSelection = true;
                if (_currentProgram != null)
                {
                    if (ProgramNameList != null && ProgramNameList.Contains(_currentProgram))
                        PrimaryCurrentProgram = _currentProgram;
                    if (SecondaryProgramNameList != null && SecondaryProgramNameList.Contains(_currentProgram))
                        SecondaryCurrentProgram = _currentProgram;
                    _currentProgram.IsSelected = true;
                }
                _syncingEditorSelection = false;
                OnStaticPropertyChanged("CurrentProgram");

                if (_currentProgram != null)
                {
                    BpObjects.UpdateWork = false;
                    Project.UpdateColor();
                    var header = _currentProgram.Header as EditorHeader;
                    var name = header.HeaderName;
                    if (Project.ContainsKey(name))
                    {
                        var data = Project.GetDictionary()[name];
                        if (data.IsPreview)
                        {
                            CurrentName = "";
                        }
                        else if (!data.AddToObjects || !IsBasicPlusExtension(Path.GetExtension(data.Name)))
                        {
                            CurrentName = "";
                        }
                        else
                        {
                            CurrentName = name;
                            //var data = Project.GetDictionary()[name];
                            IntellisenseParser.UpdateMap(CurrentName);
                        }
                    }
                    else
                    {
                        CurrentName = "";
                    }
                }
                else
                {
                    CurrentName = "";
                }

                var currentData = GetCurrentProgramData();
                if (currentData != null)
                    currentData.ApplyDocumentViewMode();
                NotifyDocumentViewSwitchChanged();
            }
        }

        public static Visibility EditorDocumentViewSwitchVisibility
        {
            get
            {
                var data = GetCurrentProgramData();
                return data != null && data.SupportsRenderedDocumentView ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public static Visibility EditorMarkdownRenderedIconVisibility
        {
            get { return IsCurrentDocumentExtension(".md") ? Visibility.Visible : Visibility.Collapsed; }
        }

        public static Visibility EditorCsvRenderedIconVisibility
        {
            get { return IsCurrentDocumentExtension(".csv") ? Visibility.Visible : Visibility.Collapsed; }
        }

        public static Brush EditorRawViewBackground
        {
            get
            {
                var data = GetCurrentProgramData();
                return data == null || !data.IsRenderedDocumentView ? DocumentViewActiveBackground : DocumentViewInactiveBackground;
            }
        }

        public static Brush EditorRenderedViewBackground
        {
            get
            {
                var data = GetCurrentProgramData();
                return data != null && data.IsRenderedDocumentView ? DocumentViewActiveBackground : DocumentViewInactiveBackground;
            }
        }

        public static Brush EditorRawViewForeground
        {
            get
            {
                var data = GetCurrentProgramData();
                return data == null || !data.IsRenderedDocumentView ? DocumentViewActiveForeground : DocumentViewInactiveForeground;
            }
        }

        public static Brush EditorRenderedViewForeground
        {
            get
            {
                var data = GetCurrentProgramData();
                return data != null && data.IsRenderedDocumentView ? DocumentViewActiveForeground : DocumentViewInactiveForeground;
            }
        }

        public static string EditorRawViewToolTip
        {
            get { return GetLocalizedOrDefault("tRawTextView", "Text view"); }
        }

        public static string EditorRenderedViewToolTip
        {
            get
            {
                return IsCurrentDocumentExtension(".csv")
                    ? GetLocalizedOrDefault("tCsvTableView", "Table view")
                    : GetLocalizedOrDefault("tMarkdownPreview", "Markdown preview");
            }
        }

        internal static void MarkProgramTextChanged(string name)
        {
            if (Project == null || string.IsNullOrEmpty(name) || !Project.ContainsKey(name))
                return;

            var data = Project.GetDictionary()[name];
            if (data != null && !data.IsReadOnlyPreview)
                data.TextChange = true;
        }

        private void SwitchCurrentDocumentView(bool rendered)
        {
            var data = GetCurrentProgramData();
            if (data == null || !data.SupportsRenderedDocumentView)
                return;

            data.IsRenderedDocumentView = rendered;
            data.ApplyDocumentViewMode();
            NotifyDocumentViewSwitchChanged();
        }

        private static ProgramData GetCurrentProgramData()
        {
            if (Project == null || CurrentProgram == null)
                return null;

            return Project.GetProgramData(CurrentProgram);
        }

        private static bool IsCurrentDocumentExtension(string extension)
        {
            var data = GetCurrentProgramData();
            if (data == null)
                return false;

            var source = !string.IsNullOrWhiteSpace(data.FullPath) ? data.FullPath : data.Name;
            return string.Equals(Path.GetExtension(source), extension, StringComparison.OrdinalIgnoreCase);
        }

        private static void NotifyDocumentViewSwitchChanged()
        {
            OnStaticPropertyChanged("EditorDocumentViewSwitchVisibility");
            OnStaticPropertyChanged("EditorMarkdownRenderedIconVisibility");
            OnStaticPropertyChanged("EditorCsvRenderedIconVisibility");
            OnStaticPropertyChanged("EditorRawViewBackground");
            OnStaticPropertyChanged("EditorRenderedViewBackground");
            OnStaticPropertyChanged("EditorRawViewForeground");
            OnStaticPropertyChanged("EditorRenderedViewForeground");
            OnStaticPropertyChanged("EditorRawViewToolTip");
            OnStaticPropertyChanged("EditorRenderedViewToolTip");
        }

        internal static IEnumerable<TabItem> GetOpenProgramItems()
        {
            if (ProgramNameList != null)
            {
                foreach (var item in ProgramNameList)
                    yield return item;
            }

            if (SecondaryProgramNameList != null)
            {
                foreach (var item in SecondaryProgramNameList)
                    yield return item;
            }
        }

        internal static int GetOpenProgramCount()
        {
            return GetOpenProgramItems().Count();
        }

        internal static void ActivateProgram(TabItem item)
        {
            if (item == null)
                return;

            CurrentProgram = item;
        }

        internal static void ActivateProgramByName(string name)
        {
            if (string.IsNullOrEmpty(name) || !Project.ContainsKey(name))
                return;

            ActivateProgram(Project.GetDictionary()[name].Item);
        }

        internal static void MoveProgramToPane(TabItem item, bool secondaryPane)
        {
            if (item == null)
                return;

            if (secondaryPane)
            {
                if (!_isEditorSplitActive)
                    SetEditorSplitMode(true);

                if (ProgramNameList.Contains(item))
                    ProgramNameList.Remove(item);
                if (!SecondaryProgramNameList.Contains(item))
                    SecondaryProgramNameList.Add(item);

                SecondaryCurrentProgram = item;
            }
            else
            {
                if (SecondaryProgramNameList.Contains(item))
                    SecondaryProgramNameList.Remove(item);
                if (!ProgramNameList.Contains(item))
                    ProgramNameList.Add(item);

                PrimaryCurrentProgram = item;
                if (SecondaryProgramNameList.Count == 0)
                    DisableEditorSplit();
            }

            NormalizeEditorTabStyles();
            CurrentProgram = item;
        }

        internal static void MoveProgramToPane(string name, bool secondaryPane)
        {
            MoveProgramToPane(FindOpenProgramItem(name), secondaryPane);
        }

        internal static bool IsEditorTabDragData(System.Windows.IDataObject data)
        {
            return !string.IsNullOrEmpty(GetDraggedEditorTabName(data));
        }

        internal static bool IsEditorTabDragData(System.Windows.Forms.IDataObject data)
        {
            return !string.IsNullOrEmpty(GetDraggedEditorTabName(data));
        }

        internal static string GetDraggedEditorTabName(System.Windows.IDataObject data)
        {
            if (data == null)
                return null;

            var name = data.GetData(EditorTabDragFormat) as string;
            if (string.IsNullOrEmpty(name))
                name = data.GetData(System.Windows.DataFormats.StringFormat) as string;
            if (string.IsNullOrEmpty(name))
                name = data.GetData(System.Windows.DataFormats.UnicodeText) as string;

            return FindOpenProgramItem(name) == null ? null : name;
        }

        internal static string GetDraggedEditorTabName(System.Windows.Forms.IDataObject data)
        {
            if (data == null)
                return null;

            var name = data.GetData(EditorTabDragFormat) as string;
            if (string.IsNullOrEmpty(name))
                name = data.GetData(System.Windows.Forms.DataFormats.StringFormat) as string;
            if (string.IsNullOrEmpty(name))
                name = data.GetData(System.Windows.Forms.DataFormats.UnicodeText) as string;

            return FindOpenProgramItem(name) == null ? null : name;
        }

        internal static bool MoveDraggedEditorTabToEditor(System.Windows.Forms.IDataObject data, TextEditor targetEditor)
        {
            var tabName = GetDraggedEditorTabName(data);
            if (string.IsNullOrEmpty(tabName))
                return false;

            MoveProgramToPane(tabName, IsEditorInSecondaryPane(targetEditor));
            return true;
        }

        private static bool IsEditorInSecondaryPane(TextEditor targetEditor)
        {
            if (targetEditor == null || Project == null || SecondaryProgramNameList == null)
                return false;

            foreach (var data in Project.GetDictionary().Values)
            {
                if (ReferenceEquals(data.Editor, targetEditor))
                    return SecondaryProgramNameList.Contains(data.Item);
            }

            return false;
        }

        internal static TabItem FindOpenProgramItem(string name)
        {
            if (string.IsNullOrEmpty(name) || !Project.ContainsKey(name))
                return null;

            return Project.GetDictionary()[name].Item;
        }

        internal static void SetEditorSplitMode(bool sideBySide)
        {
            _isEditorSplitActive = true;
            _isEditorSplitSideBySide = sideBySide;
            SecondaryEditorVisibility = Visibility.Visible;
            EditorPaneRows = sideBySide ? 1 : 2;
            EditorPaneColumns = sideBySide ? 2 : 1;
            if (sideBySide)
                ApplySideBySideEditorLayout();
            else
                ApplyTopBottomEditorLayout();
        }

        private static void InsertProgramItem(TabItem item)
        {
            var target = GetActiveProgramList();
            item.Style = (System.Windows.Style)Application.Current.Resources["FirstTabItem"];
            foreach (var existing in target)
                existing.Style = (System.Windows.Style)Application.Current.Resources["NextTabItem"];

            target.Insert(0, item);
            NotifyProgramListChanged(target);
            CurrentProgram = item;
            NotifyEditorContentChanged();
        }

        private static ObservableCollection<TabItem> GetActiveProgramList()
        {
            if (_isEditorSplitActive && CurrentProgram != null && SecondaryProgramNameList.Contains(CurrentProgram))
                return SecondaryProgramNameList;

            return ProgramNameList;
        }

        private static void NotifyProgramListChanged(ObservableCollection<TabItem> list)
        {
            if (list == ProgramNameList)
                OnStaticPropertyChanged("ProgramNameList");
            else
                OnStaticPropertyChanged("SecondaryProgramNameList");
        }

        private static void NormalizeEditorTabStyles()
        {
            NormalizeEditorTabStyles(ProgramNameList);
            NormalizeEditorTabStyles(SecondaryProgramNameList);
            OnStaticPropertyChanged("ProgramNameList");
            OnStaticPropertyChanged("SecondaryProgramNameList");
        }

        private static void NormalizeEditorTabStyles(ObservableCollection<TabItem> items)
        {
            if (items == null)
                return;

            for (var i = 0; i < items.Count; i++)
                items[i].Style = (System.Windows.Style)Application.Current.Resources[i == 0 ? "FirstTabItem" : "NextTabItem"];
        }

        private void ToggleEditorSplit(bool sideBySide)
        {
            if (_isEditorSplitActive)
            {
                if (_isEditorSplitSideBySide == sideBySide)
                    DisableEditorSplit();
                else
                    SetEditorSplitMode(sideBySide);
                return;
            }

            SetEditorSplitMode(sideBySide);
            var current = CurrentProgram;
            var next = GetNextProgramAfter(current);
            if (next != null)
                MoveProgramToPane(next, true);
            else
                NormalizeEditorTabStyles();

            CurrentProgram = current ?? ProgramNameList.FirstOrDefault() ?? SecondaryProgramNameList.FirstOrDefault();
        }

        private static TabItem GetNextProgramAfter(TabItem current)
        {
            if (ProgramNameList == null || ProgramNameList.Count <= 1)
                return null;

            var index = current == null ? -1 : ProgramNameList.IndexOf(current);
            if (index < 0)
                index = 0;

            var nextIndex = index + 1;
            if (nextIndex < ProgramNameList.Count)
                return ProgramNameList[nextIndex];

            var previousIndex = index - 1;
            return previousIndex >= 0 ? ProgramNameList[previousIndex] : null;
        }

        private static void DisableEditorSplit()
        {
            var active = CurrentProgram;
            foreach (var item in SecondaryProgramNameList.ToList())
            {
                SecondaryProgramNameList.Remove(item);
                ProgramNameList.Add(item);
            }

            _isEditorSplitActive = false;
            _isEditorSplitSideBySide = true;
            SecondaryEditorVisibility = Visibility.Collapsed;
            EditorPaneRows = 1;
            EditorPaneColumns = 1;
            ApplySingleEditorLayout();
            SecondaryCurrentProgram = null;
            NormalizeEditorTabStyles();
            CurrentProgram = active ?? ProgramNameList.FirstOrDefault();
        }

        private static void ApplySingleEditorLayout()
        {
            EditorFirstColumnWidth = new GridLength(1, GridUnitType.Star);
            EditorSecondColumnWidth = new GridLength(0, GridUnitType.Pixel);
            EditorFirstRowHeight = new GridLength(1, GridUnitType.Star);
            EditorSecondRowHeight = new GridLength(0, GridUnitType.Pixel);
            EditorColumnSplitterVisibility = Visibility.Collapsed;
            EditorRowSplitterVisibility = Visibility.Collapsed;
            SecondaryEditorBorderThickness = new Thickness(0);
            SecondaryEditorMargin = new Thickness(0);

            PrimaryEditorRow = 0;
            PrimaryEditorColumn = 0;
            PrimaryEditorRowSpan = 2;
            PrimaryEditorColumnSpan = 2;

            SecondaryEditorRow = 0;
            SecondaryEditorColumn = 1;
            SecondaryEditorRowSpan = 2;
            SecondaryEditorColumnSpan = 1;
        }

        private static void ApplySideBySideEditorLayout()
        {
            EditorFirstColumnWidth = new GridLength(1, GridUnitType.Star);
            EditorSecondColumnWidth = new GridLength(1, GridUnitType.Star);
            EditorFirstRowHeight = new GridLength(1, GridUnitType.Star);
            EditorSecondRowHeight = new GridLength(0, GridUnitType.Pixel);
            EditorColumnSplitterVisibility = Visibility.Visible;
            EditorRowSplitterVisibility = Visibility.Collapsed;
            SecondaryEditorBorderThickness = new Thickness(1, 0, 0, 0);
            SecondaryEditorMargin = new Thickness(2, 0, 0, 0);

            PrimaryEditorRow = 0;
            PrimaryEditorColumn = 0;
            PrimaryEditorRowSpan = 2;
            PrimaryEditorColumnSpan = 1;

            SecondaryEditorRow = 0;
            SecondaryEditorColumn = 1;
            SecondaryEditorRowSpan = 2;
            SecondaryEditorColumnSpan = 1;
        }

        private static void ApplyTopBottomEditorLayout()
        {
            EditorFirstColumnWidth = new GridLength(1, GridUnitType.Star);
            EditorSecondColumnWidth = new GridLength(0, GridUnitType.Pixel);
            EditorFirstRowHeight = new GridLength(1, GridUnitType.Star);
            EditorSecondRowHeight = new GridLength(1, GridUnitType.Star);
            EditorColumnSplitterVisibility = Visibility.Collapsed;
            EditorRowSplitterVisibility = Visibility.Visible;
            SecondaryEditorBorderThickness = new Thickness(0, 1, 0, 0);
            SecondaryEditorMargin = new Thickness(0, 2, 0, 0);

            PrimaryEditorRow = 0;
            PrimaryEditorColumn = 0;
            PrimaryEditorRowSpan = 1;
            PrimaryEditorColumnSpan = 2;

            SecondaryEditorRow = 1;
            SecondaryEditorColumn = 0;
            SecondaryEditorRowSpan = 1;
            SecondaryEditorColumnSpan = 2;
        }

        internal static void SetEditorSplitProportion(GridLength first, GridLength second, bool sideBySide)
        {
            if (!_isEditorSplitActive)
                return;

            if (sideBySide)
            {
                EditorFirstColumnWidth = first;
                EditorSecondColumnWidth = second;
            }
            else
            {
                EditorFirstRowHeight = first;
                EditorSecondRowHeight = second;
            }
        }

        private void ToggleRightSidebar()
        {
            try
            {
                var grid = MainWindow.GetEditorGrid;
                if (grid == null || grid.ColumnDefinitions.Count < 3)
                    return;

                var editorColumn = grid.ColumnDefinitions[0];
                var splitterColumn = grid.ColumnDefinitions[1];
                var sidebarColumn = grid.ColumnDefinitions[2];
                var editor = grid.Children.Cast<UIElement>().FirstOrDefault(child => Grid.GetColumn(child) == 0);
                var splitter = grid.Children.Cast<UIElement>().FirstOrDefault(child => Grid.GetColumn(child) == 1);
                var sidebar = grid.Children.Cast<UIElement>().FirstOrDefault(child => Grid.GetColumn(child) == 2);
                var sidebarIsHidden = _rightSidebarHidden || sidebarColumn.ActualWidth <= 1 || sidebar == null || sidebar.Visibility != Visibility.Visible;

                if (!sidebarIsHidden)
                {
                    _lastEditorColumnWidth = editorColumn.Width.Value > 0 ? editorColumn.Width : new GridLength(700, GridUnitType.Star);
                    _lastRightSidebarColumnWidth = sidebarColumn.Width.Value > 0 ? sidebarColumn.Width : new GridLength(300, GridUnitType.Star);

                    if (editor != null)
                        Grid.SetColumnSpan(editor, 1);
                    if (sidebar != null)
                        sidebar.Visibility = Visibility.Collapsed;
                    if (splitter != null)
                        splitter.Visibility = Visibility.Collapsed;

                    editorColumn.MinWidth = 0;
                    editorColumn.MaxWidth = double.PositiveInfinity;
                    editorColumn.Width = new GridLength(1, GridUnitType.Star);

                    splitterColumn.MinWidth = 0;
                    splitterColumn.MaxWidth = 0;
                    splitterColumn.Width = new GridLength(0, GridUnitType.Pixel);

                    sidebarColumn.MinWidth = 0;
                    sidebarColumn.MaxWidth = 0;
                    sidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
                    _rightSidebarHidden = true;
                }
                else
                {
                    if (editor != null)
                        Grid.SetColumnSpan(editor, 1);

                    editorColumn.MaxWidth = double.PositiveInfinity;
                    editorColumn.MinWidth = 400;
                    editorColumn.Width = _lastEditorColumnWidth.Value > 0 ? _lastEditorColumnWidth : new GridLength(700, GridUnitType.Star);

                    splitterColumn.MaxWidth = double.PositiveInfinity;
                    splitterColumn.MinWidth = 0;
                    splitterColumn.Width = new GridLength(RightSidebarSplitterWidth, GridUnitType.Pixel);

                    sidebarColumn.MaxWidth = double.PositiveInfinity;
                    sidebarColumn.MinWidth = RightSidebarMinWidth;
                    sidebarColumn.Width = _lastRightSidebarColumnWidth.Value > 0 ? _lastRightSidebarColumnWidth : new GridLength(300, GridUnitType.Star);

                    if (sidebar != null)
                        sidebar.Visibility = Visibility.Visible;
                    if (splitter != null)
                        splitter.Visibility = Visibility.Visible;
                    _rightSidebarHidden = false;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    CommonData.Status.Clear();
                    CommonData.Status.Add(ex.Message);
                }
                catch
                {
                }
            }
        }

        private void OpenAiChatSidebar()
        {
            if (!AiAppSettingsStore.Settings.AiEnabled)
                return;

            try
            {
                var grid = MainWindow.GetEditorGrid;
                if (grid != null && grid.ColumnDefinitions.Count >= 3)
                {
                    var sidebarColumn = grid.ColumnDefinitions[2];
                    var sidebar = grid.Children.Cast<UIElement>().FirstOrDefault(child => Grid.GetColumn(child) == 2);
                    var sidebarIsHidden = _rightSidebarHidden || sidebarColumn.ActualWidth <= 1 || sidebar == null || sidebar.Visibility != Visibility.Visible;
                    if (sidebarIsHidden)
                        ToggleRightSidebar();
                }

                HelpPanel.SelectAiChatTab();
            }
            catch
            {
            }
        }

        private void AiAppSettingsStore_SettingsChanged(object sender, EventArgs e)
        {
            OnPropertyChanged("AiFunctionsVisibility");
            OnPropertyChanged("AiCodeCompletionsEnabled");
            OnPropertyChanged("CanFixBugs");
            OnPropertyChanged("FixBugsToolTip");
            if (!AiAppSettingsStore.Settings.AiEnabled)
                HelpPanel.EnsureAiChatNotSelected();
        }

        private void IdeProblemStore_Changed(object sender, EventArgs e)
        {
            var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => IdeProblemStore_Changed(sender, e)));
                return;
            }

            OnPropertyChanged("CanFixBugs");
            OnPropertyChanged("FixBugsToolTip");
        }

        private void OpenFixBugs()
        {
            if (!CanFixBugs)
                return;

            FixBugsCoordinator.Open();
        }

        private void ToggleAiCodeCompletions()
        {
            if (!AiAppSettingsStore.Settings.AiEnabled)
                return;

            var settings = AiAppSettingsStore.Settings;
            var previousValue = settings.AiCodeCompletionsEnabled;
            settings.AiCodeCompletionsEnabled = !previousValue;
            OnPropertyChanged("AiCodeCompletionsEnabled");

            try
            {
                AiAppSettingsStore.SaveSettings();
            }
            catch (Exception ex)
            {
                settings.AiCodeCompletionsEnabled = previousValue;
                OnPropertyChanged("AiCodeCompletionsEnabled");
                CommonData.Status.Clear();
                var message = GetLocalizedOrDefault("AiSettingsSaveFailed", "Could not save AI settings: {0}");
                CommonData.Status.Add(string.Format(message, ex.Message));
            }
        }

        internal static void OpenGitDiffPreview(GitCommitVM commit)
        {
            if (commit == null || commit.DiffDocument == null || commit.DiffDocument.Files.Count == 0)
            {
                CommonData.Status.Clear();
                var noDataMessage = GUILanguage.GetItem("GitDiffNoData");
                CommonData.Status.Add(string.IsNullOrEmpty(noDataMessage) ? "No diff data for this commit." : noDataMessage);
                return;
            }

            MainWindow.UpdateSize();
            var name = commit.DiffDocument.Title;
            if (Project.ContainsKey(name))
            {
                var existing = Project.GetDictionary()[name];
                CurrentProgram = existing.Item;
                existing.Item.IsSelected = true;
                SetGitDiffStatus(commit);
                Topmost();
                return;
            }

            var tmp = new NewTabItem().CreateReadOnlyPreview(Project.GetDictionary(), commit.DiffDocument);
            var pd = new ProgramData();
            pd.AddToObjects = false;
            pd.IsPreview = true;
            pd.IsReadOnlyPreview = true;
            pd.OldName = name;
            pd.ClosedName = name;
            pd.Name = name;
            pd.Path = "";
            var tooltip = GUILanguage.GetItem("GitDiffReadOnly");
            pd.FullPath = string.IsNullOrEmpty(tooltip) ? "Read-only Git diff preview" : tooltip;
            pd.Item = tmp;
            pd.TextChange = false;

            if (Project.Add(pd.Name, pd))
                InsertProgramItem(pd.Item);

            SetGitDiffStatus(commit);
            Topmost();
        }

        internal static void OpenGitChangeFile(GitChangeItemVM file)
        {
            if (file == null)
                return;

            if (!file.IsDeleted && !string.IsNullOrWhiteSpace(file.FullPath) && File.Exists(file.FullPath))
            {
                var extension = Path.GetExtension(file.FullPath).ToLowerInvariant();
                if (IsSupportedEditorExtension(extension))
                {
                    FileNew(file.FullPath, false);
                    return;
                }
            }

            var name = CreateGitPreviewName(file);
            if (Project.ContainsKey(name))
            {
                CurrentProgram = Project.GetDictionary()[name].Item;
                Topmost();
                return;
            }

            var text = ReadGitChangePreviewText(file);
            var tmp = new NewTabItem().CreateReadOnlyTextPreview(Project.GetDictionary(), name, text);
            var pd = new ProgramData();
            pd.AddToObjects = false;
            pd.IsPreview = true;
            pd.IsReadOnlyPreview = true;
            pd.OldName = name;
            pd.ClosedName = name;
            pd.Name = name;
            pd.Path = file.Folder;
            pd.FullPath = !string.IsNullOrWhiteSpace(file.FullPath)
                ? file.FullPath
                : "Git preview: " + file.RelativePath;
            pd.Item = tmp;
            pd.TextChange = false;

            if (Project.Add(pd.Name, pd))
                InsertProgramItem(pd.Item);

            CommonData.Status.Clear();
            var message = GUILanguage.GetItem("GitStatusFileOpened");
            CommonData.Status.Add((string.IsNullOrEmpty(message) ? "Opened file from Git changes:" : message) + " " + name);
            Topmost();
        }

        private static string CreateGitPreviewName(GitChangeItemVM file)
        {
            var relative = string.IsNullOrWhiteSpace(file.RelativePath)
                ? file.FileName
                : file.RelativePath.Replace('/', '_').Replace('\\', '_');

            return file.IsDeleted ? relative + " (HEAD)" : relative + " (Git)";
        }

        private static string ReadGitChangePreviewText(GitChangeItemVM file)
        {
            if (!file.IsDeleted && !string.IsNullOrWhiteSpace(file.FullPath) && File.Exists(file.FullPath))
                return File.ReadAllText(file.FullPath);

            if (!string.IsNullOrEmpty(file.PreviewText))
                return file.PreviewText;

            string text;
            if (GitRepositoryService.TryGetFileText(file.RepositoryRoot, file.RelativePath, out text))
                return text;

            var message = GUILanguage.GetItem("GitDiffNoData");
            return string.IsNullOrEmpty(message) ? "No Git file data available." : message;
        }

        private static void SetGitDiffStatus(GitCommitVM commit)
        {
            var message = GUILanguage.GetItem("GitStatusDiffOpened");
            if (string.IsNullOrEmpty(message))
                message = "Opened read-only diff:";

            CommonData.Status.Clear();
            CommonData.Status.Add(message + " " + commit.ShortHash);
        }

        internal static void ReloadOpenProjectFilesFromDisk(string repositoryRoot)
        {
            ReloadOpenProjectFilesFromDisk(repositoryRoot, null, false);
        }

        /// <summary>
        /// Refreshes open editor tabs changed by an external operation. Agent changes must
        /// never overwrite a keystroke made locally while a turn was running.
        /// </summary>
        internal static void ReloadOpenProjectFilesFromDisk(
            string repositoryRoot,
            IEnumerable<string> changedPaths,
            bool preserveUnsavedChanges)
        {
            if (Project == null || string.IsNullOrWhiteSpace(repositoryRoot))
                return;

            string root;
            try
            {
                root = Path.GetFullPath(repositoryRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            }
            catch
            {
                return;
            }

            HashSet<string> selectedPaths = null;
            if (changedPaths != null)
            {
                selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in changedPaths.Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    try
                    {
                        var fullPath = Path.GetFullPath(Path.IsPathRooted(path)
                            ? path
                            : Path.Combine(root, path));
                        if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            selectedPaths.Add(fullPath);
                    }
                    catch { }
                }
            }

            foreach (var data in Project.GetDictionary().Values.ToList())
            {
                if (data == null || data.IsPreview || data.Editor == null || string.IsNullOrWhiteSpace(data.FullPath))
                    continue;

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(data.FullPath);
                }
                catch
                {
                    continue;
                }

                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (selectedPaths != null && !selectedPaths.Contains(fullPath))
                    continue;

                var extension = Path.GetExtension(fullPath).ToLowerInvariant();
                if (!IsSupportedEditorExtension(extension))
                    continue;
                if (!File.Exists(fullPath))
                    continue;
                if (preserveUnsavedChanges &&
                    (data.TextChange || !string.IsNullOrWhiteSpace(data.GetHeaderChangeText)))
                    continue;

                string diskText;
                try { diskText = File.ReadAllText(fullPath); }
                catch { continue; }
                if (data.Editor.TextArea.Text == diskText)
                {
                    data.TextChange = false;
                    continue;
                }

                var oldPosition = data.Editor.TextArea.CurrentPosition;
                var wasReadOnly = data.Editor.TextArea.ReadOnly;
                try
                {
                    data.Editor.TextArea.ReadOnly = false;
                    data.Editor.TextArea.Text = diskText;
                    data.Editor.TextArea.EmptyUndoBuffer();
                    data.Editor.TextArea.GotoPosition(Math.Min(oldPosition, data.Editor.TextArea.TextLength));
                    data.RefreshRenderedDocumentView();
                    data.TextChange = false;
                }
                catch { }
                finally
                {
                    try { data.Editor.TextArea.ReadOnly = wasReadOnly; }
                    catch { }
                }
            }
        }

        internal static bool HasUnsavedProjectEditorChanges(string repositoryRoot)
        {
            if (Project == null || string.IsNullOrWhiteSpace(repositoryRoot))
                return false;

            string root;
            try
            {
                root = Path.GetFullPath(repositoryRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            }
            catch
            {
                return false;
            }

            foreach (var data in Project.GetDictionary().Values)
            {
                if (data == null || data.IsPreview || data.Editor == null || string.IsNullOrWhiteSpace(data.FullPath))
                    continue;

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(data.FullPath);
                }
                catch
                {
                    continue;
                }

                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (data.TextChange || !string.IsNullOrEmpty(data.GetHeaderChangeText))
                    return true;
            }

            return false;
        }

        internal static string CurrentName { get; private set; }

        private static ContentControl _currentInfo;
        public static ContentControl CurrentInfo
        {
            get { return _currentInfo; }

            set
            {
                _currentInfo = value;
                OnStaticPropertyChanged("CurrentInfo");
            }
        }

        private static string _textFind = "";
        public static string TextFind
        {
            get { return _textFind; }

            set
            {
                _textFind = value;
                OnStaticPropertyChanged("TextFind");
            }
        }

        private static ObservableCollection<string> _prgName = new ObservableCollection<string>();
        public static ObservableCollection<string> ProgramName
        {
            get { return _prgName; }
            set
            {
                _prgName = value;
                OnStaticPropertyChanged("ProgramName");
            }
        }

        #region Checked button

        private static bool _showNumber;
        public static bool ShowNumber
        {
            get { return _showNumber; }
            set
            {
                _showNumber = value;
                Configurations.Get.ShowNumber = _showNumber;
                OnStaticPropertyChanged("ShowNumber");
            }
        }

        private static bool _showLine;
        public static bool ShowLine
        {
            get { return _showLine; }
            set
            {
                _showLine = value;
                Configurations.Get.ShowLine = _showLine;
                OnStaticPropertyChanged("ShowLine");
            }
        }

        private static bool _showSpace;
        public static bool ShowSpace
        {
            get { return _showSpace; }
            set
            {
                _showSpace = value;
                Configurations.Get.ShowSpace = _showSpace;
                OnStaticPropertyChanged("ShowSpace");
            }
        }

        private static bool _showWrap;
        public static bool ShowWrap
        {
            get { return _showWrap; }
            set
            {
                _showWrap = value;
                Configurations.Get.Wrap = _showWrap;
                OnStaticPropertyChanged("ShowWrap");
            }
        }

        #endregion

        #region Brick checed button

        private static bool _brickConnect;
        public static bool BrickConnect
        {
            get { return _brickConnect; }
            set
            {
                _brickConnect = value;
                OnStaticPropertyChanged("BrickConnect");
            }
        }
        #endregion

        #region Panel size

        private static double _spWidth;
        public static double SpWidth
        {
            get { return _spWidth; }
            set
            {
                _spWidth = value;
                HelperPanelVM.Get.SpWidth = value;
            }
        }

        private static double _spWidthCl;
        public static double SpWidthCl
        {
            get { return _spWidthCl; }
            set
            {
                _spWidthCl = value;
                HelperPanelVM.Get.SpWidthCl = value;
            }
        }

        private static double _widthHelpPanel;
        public static double WidthHelpPanel
        {
            get { return _widthHelpPanel; }
            set
            {
                _widthHelpPanel = value;
                HelperPanelVM.Get.WidthHelpPanel = value;
                OnStaticPropertyChanged("WidthHelpPanel");
            }
        }

        private static double _heightHelpPanel;
        public static double HeightHelpPanel
        {
            get { return _heightHelpPanel; }
            set
            {
                _heightHelpPanel = value;
                HelperPanelVM.Get.HeightHelpPanel = value;
                OnStaticPropertyChanged("HeightHelpPanel");
            }
        }

        #endregion

        public void FileNew()
        {
            MainWindow.UpdateSize();
            string name = "";
            string path = "";
            bool createProject = false;
            string projectName = "";
            var tmp = new NewTabItem().Create(Project.GetDictionary(), out name, out path, out createProject, out projectName);
            if (createProject)
            {
                CreateNewProject(projectName);
                ButtonsPanel.GetFilePopup.IsOpen = false;
                return;
            }
            if (tmp == null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(path))
                EnsureProjectRootFromFile(Path.Combine(path, name));
            var pd = new ProgramData();
            pd.AddToObjects = IsBasicPlusExtension(Path.GetExtension(name));
            pd.OldName = name;
            pd.ClosedName = name;
            pd.Name = name;
            pd.Path = path;
            pd.FullPath = Path.Combine(path, name);
            pd.Item = tmp;
            ApplyProjectMembership(pd);
            pd.TextChange = false;
            if (Project.Add(name, pd))
                InsertProgramItem(pd.Item);
            ButtonsPanel.GetFilePopup.IsOpen = false;
        }

        private void OpenProject()
        {
            try
            {
                var dialog = new FolderPickerDialog
                {
                    Title = GetProjectStatusText("ProjectOpenDialogTitle", "Open project folder"),
                    InitialDirectory = !string.IsNullOrWhiteSpace(CurrentProjectRoot) && Directory.Exists(CurrentProjectRoot)
                        ? CurrentProjectRoot
                        : ""
                };

                string selectedPath;
                if (!dialog.Show(out selectedPath))
                    return;

                SetCurrentProjectRoot(selectedPath);
                CommonData.Status.Clear();
                CommonData.Status.Add(GetProjectStatusText("ProjectOpened", "Project opened:") + " " + CurrentProjectName);
            }
            catch (Exception ex)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(ex.Message);
            }

            ButtonsPanel.GetFilePopup.IsOpen = false;
        }

        private static void CreateNewProject(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return;

            try
            {
                var initialDirectory = "";
                if (!string.IsNullOrWhiteSpace(CurrentProjectRoot) && Directory.Exists(CurrentProjectRoot))
                    initialDirectory = Directory.GetParent(CurrentProjectRoot)?.FullName ?? CurrentProjectRoot;

                var dialog = new FolderPickerDialog
                {
                    Title = GetProjectStatusText("ProjectCreateParentDialogTitle", "Select parent folder for the new project"),
                    InitialDirectory = initialDirectory
                };

                string selectedPath;
                if (!dialog.Show(out selectedPath))
                    return;

                var root = Path.Combine(selectedPath, projectName);
                Directory.CreateDirectory(root);
                SetCurrentProjectRoot(root);

                var mainFile = Path.Combine(root, projectName + ext);
                if (!File.Exists(mainFile))
                    File.WriteAllText(mainFile, "");

                FileNew(mainFile, false);
                CommonData.Status.Clear();
                CommonData.Status.Add(GetProjectStatusText("ProjectCreated", "Project created:") + " " + CurrentProjectName);
            }
            catch (Exception ex)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(ex.Message);
            }
        }

        private static string GetProjectStatusText(string key, string fallback)
        {
            var text = GUILanguage.GetItem(key);
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        public static void FileNew(string path, bool start)
        {
            ProgramData pdOld = null;
            bool tcOld = false;
            if (CurrentProgram != null)
            {
                pdOld = Project.GetProgramData(CurrentProgram);
                tcOld = pdOld.TextChange;
            }

            FileInfo fi = new FileInfo(path);
            EnsureProjectRootFromFile(fi.FullName);
            if (Project.ContainsKey(fi.Name))
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(GetLocalization["prgFindName1"] + " " + fi.Name + " " + GetLocalization["prgFindName2"]);

                Topmost();
                if (start)
                {
                    StartNewServer();
                }
                return;
            }
            else if (!IsSupportedEditorExtension(fi.Extension))
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(GetLocalization["prgInvalidExt1"] + " " + fi.Extension + " " + GetLocalization["prgInvalidExt2"]);
                Topmost();
                if (start)
                {
                    StartNewServer();
                }
                return;
            }

            if (Project != null && Project.Count != 0)
                MainWindow.UpdateSize();

            string text = File.ReadAllText(path);
            var tmp = new NewTabItem().Create(Project.GetDictionary(), text, fi.Name);
            var pd = new ProgramData();
            pd.AddToObjects = IsBasicPlusExtension(fi.Extension);
            pd.OldName = fi.Name;
            pd.ClosedName = fi.Name;
            pd.Name = fi.Name;
            pd.Path = fi.DirectoryName;
            pd.FullPath = fi.FullName;
            pd.Item = tmp;
            ApplyProjectMembership(pd);
            pd.TextChange = false;
            currentPath = fi.FullName;

            if (Project.Add(pd.Name, pd))
                InsertProgramItem(pd.Item);

            if (pdOld != null)
                pdOld.TextChange = tcOld;

            Topmost();

            if (start)
            {
                StartNewServer();
            }
        }

        public static void FindAndReplaceFile(string name, string path)
        {
            var tmpName = "~" + name;
            if (Project.ContainsKey(tmpName))
            {
                var pd = Project.GetDictionary()[tmpName];

                if (pd.FullPath == path + tmpName.Replace(ext, "") + "\\" + tmpName)
                {
                    string text = File.ReadAllText(pd.FullPath);
                    pd.Editor.TextArea.Text = text;
                    pd.TextChange = false;
                }
            }
            Project.GetProgramData(CurrentProgram).TextChange = false;
        }

        public void FileOpen()
        {
            try
            {
                MainWindow.UpdateSize();
                string tmpName = "";
                string tmpDirectory = "";
                string tmpFullPath = "";
                bool changeCurPrg = false;
                ProgramData tmpCurPrg;
                if (CurrentProgram != null)
                {
                    tmpCurPrg = Project.GetProgramData(CurrentProgram);
                }
                else
                {
                    tmpCurPrg = null;
                }

                if (tmpCurPrg != null)
                {
                    if (tmpCurPrg.TextChange)
                    {
                        changeCurPrg = true;
                    }
                }
                var text = new OpenFile().Open(currentPath, out tmpFullPath, out tmpDirectory, out tmpName);
                if (text != null)
                {
                    EnsureProjectRootFromFile(tmpFullPath);
                    if (Project.Count > 0 && Project.ContainsKey(tmpName))
                    {
                        CommonData.Status.Clear();
                        CommonData.Status.Add(GetLocalization["prgFindName1"] + " " + tmpName + " " + GetLocalization["prgFindName2"]);
                        return;
                    }

                    if (IntellisenseParser.Data.ContainsKey(tmpName))
                    {
                        IntellisenseParser.Data.Remove(tmpName);
                    }

                    var tmp = new NewTabItem().Create(Project.GetDictionary(), text, tmpName);
                    var pd = new ProgramData();
                    pd.AddToObjects = IsBasicPlusExtension(Path.GetExtension(tmpName));
                    pd.OldName = tmpName;
                    pd.ClosedName = tmpName;
                    pd.Name = tmpName;
                    pd.Path = tmpDirectory;
                    pd.FullPath = tmpFullPath;
                    pd.Item = tmp;
                    ApplyProjectMembership(pd);
                    pd.TextChange = false;
                    currentPath = tmpFullPath;
                    if (Project.Add(pd.Name, pd))
                    {
                        InsertProgramItem(pd.Item);
                        if (!changeCurPrg && tmpCurPrg != null)
                        {
                            tmpCurPrg.TextChange = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(ex.Message);
            }
            ButtonsPanel.GetFilePopup.IsOpen = false;
        }

        public void FileSave()
        {
            if (GetOpenProgramCount() == 0)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(GetLocalization["prgNotPrograms"]);
                return;
            }
            try
            {
                var name = Project.SelectedItemName();
                if (name != "")
                {
                    var pd = Project.GetProgramData(CurrentProgram);
                    if (pd != null && pd.IsPreview)
                    {
                        var message = GUILanguage.GetItem("GitDiffReadOnly");
                        CommonData.Status.Clear();
                        CommonData.Status.Add(string.IsNullOrEmpty(message) ? "Read-only Git diff preview" : message);
                    }
                    else if (pd != null && pd.FullPath != "")
                    {
                        new SaveFile().Save(pd);
                        pd.TextChange = false;
                        GitPanelVM.NotifySavedWorkspaceFilesChanged();
                    }
                    else
                    {
                        FileSaveAsOther();
                    }
                }
            }
            catch (Exception ex)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(ex.Message);
            }
            ButtonsPanel.GetFilePopup.IsOpen = false;
        }

        public static void SaveAllAndClose()
        {
            if (GetOpenProgramCount() == 0)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(GetLocalization["prgNotPrograms"]);
                return;
            }
            try
            {
                foreach (var i in GetOpenProgramItems().ToList())
                {
                    var pd = Project.GetProgramData(i);
                    if (pd != null && pd.IsPreview)
                        continue;

                    if ((pd != null && pd.TextChange) || pd.GetHeaderChangeText != "")
                    {
                        if (pd.FullPath != "")
                        {
                            new SaveFile().Save(pd);
                            GitPanelVM.NotifySavedWorkspaceFilesChanged();
                        }
                        else
                        {
                            string tmpExt = "";
                            for (int a = pd.Name.IndexOf('.'); a < pd.Name.Length; a++)
                            {
                                tmpExt += pd.Name[a];
                            }
                            pd.CommitRenderedDocumentViewChanges();
                            new SaveFile().Save(pd.Editor.TextArea.Text, pd.Name, tmpExt);
                        }
                        pd.TextChange = false;
                    }
                }
            }
            catch (Exception ex)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(ex.Message);
            }
        }

        private void FileSaveAll()
        {
            if (GetOpenProgramCount() == 0)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(GetLocalization["prgNotPrograms"]);
                return;
            }

            try
            {
                foreach (var i in GetOpenProgramItems().ToList())
                {
                    var pd = Project.GetProgramData(i);
                    if (pd != null)
                    {
                        if (pd.IsPreview)
                            continue;

                        if (pd.FullPath != "")
                        {
                            new SaveFile().Save(pd);
                            pd.TextChange = false;
                            GitPanelVM.NotifySavedWorkspaceFilesChanged();
                        }
                        else
                        {
                            string tmpExt = "";
                            for (int a = pd.Name.IndexOf('.'); a < pd.Name.Length; a++)
                            {
                                tmpExt += pd.Name[a];
                            }
                            pd.CommitRenderedDocumentViewChanges();
                            string name = new SaveFile().Save(pd.Editor.TextArea.Text, pd.Name, tmpExt);
                            if (name != "")
                            {
                                FileInfo fi = new FileInfo(name);
                                string oldName = pd.Name;
                                var wasRendered = pd.IsRenderedDocumentView;
                                Project.Remove(oldName);
                                var newPd = new ProgramData();
                                newPd.AddToObjects = IsBasicPlusExtension(fi.Extension);
                                newPd.IsRenderedDocumentView = wasRendered;
                                newPd.Item = i;
                                //
                                //newPd.Name = fi.Name.Replace(ext, "");
                                newPd.Name = fi.Name;
                                //
                                newPd.Path = fi.DirectoryName;
                                newPd.FullPath = fi.FullName;
                                EnsureProjectRootFromFile(fi.FullName);
                                ApplyProjectMembership(newPd);
                                newPd.TextChange = false;
                                Project.Add(newPd.Name, newPd);
                                newPd.ApplyDocumentViewMode();
                                NotifyDocumentViewSwitchChanged();
                                currentPath = fi.FullName;
                                GitPanelVM.NotifySavedWorkspaceFilesChanged();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(ex.Message);
            }
            ButtonsPanel.GetFilePopup.IsOpen = false;
        }

        private void FileSaveAs()
        {
            if (GetOpenProgramCount() == 0)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(GetLocalization["prgNotPrograms"]);
                return;
            }

            try
            {
                var pd = Project.GetProgramData(CurrentProgram);
                if (pd != null && pd.IsPreview)
                {
                    var message = GUILanguage.GetItem("GitDiffReadOnly");
                    CommonData.Status.Clear();
                    CommonData.Status.Add(string.IsNullOrEmpty(message) ? "Read-only Git diff preview" : message);
                    ButtonsPanel.GetFilePopup.IsOpen = false;
                    return;
                }

                string tmpExt = "";
                for (int a = pd.Name.IndexOf('.'); a < pd.Name.Length; a++)
                {
                    tmpExt += pd.Name[a];
                }
                pd.CommitRenderedDocumentViewChanges();
                string name = new SaveFile().Save(pd.Editor.TextArea.Text, pd.Name, "");
                if (name != "")
                {
                    FileInfo fi = new FileInfo(name);
                    string oldName = pd.Name;
                    var wasRendered = pd.IsRenderedDocumentView;
                    Project.Remove(oldName);
                    var newPd = new ProgramData();
                    newPd.AddToObjects = IsBasicPlusExtension(fi.Extension);
                    newPd.IsRenderedDocumentView = wasRendered;
                    newPd.Item = CurrentProgram;
                    //
                    //newPd.Name = fi.Name.Replace(ext, "");
                    newPd.Name = fi.Name;
                    //
                    newPd.Path = fi.DirectoryName;
                    newPd.FullPath = fi.FullName;
                    EnsureProjectRootFromFile(fi.FullName);
                    ApplyProjectMembership(newPd);
                    newPd.TextChange = false;
                    Project.Add(newPd.Name, newPd);
                    newPd.ApplyDocumentViewMode();
                    NotifyDocumentViewSwitchChanged();
                    currentPath = fi.FullName;
                    GitPanelVM.NotifySavedWorkspaceFilesChanged();
                    //projectData = Project.GetProgramData(CurrentProgram);
                }
            }
            catch (Exception ex)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(ex.Message);
            }
            ButtonsPanel.GetFilePopup.IsOpen = false;
        }

        private void FileSaveAsOther()
        {
            if (GetOpenProgramCount() == 0)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(GetLocalization["prgNotPrograms"]);
                return;
            }

            try
            {
                var pd = Project.GetProgramData(CurrentProgram);
                if (pd != null && pd.IsPreview)
                {
                    var message = GUILanguage.GetItem("GitDiffReadOnly");
                    CommonData.Status.Clear();
                    CommonData.Status.Add(string.IsNullOrEmpty(message) ? "Read-only Git diff preview" : message);
                    ButtonsPanel.GetFilePopup.IsOpen = false;
                    return;
                }

                string tmpExt = "";
                for (int a = pd.Name.IndexOf('.'); a < pd.Name.Length; a++)
                {
                    tmpExt += pd.Name[a];
                }
                pd.CommitRenderedDocumentViewChanges();
                string name = new SaveFile().Save(pd.Editor.TextArea.Text, pd.Name, tmpExt);
                if (name != "")
                {
                    FileInfo fi = new FileInfo(name);
                    string oldName = pd.Name;
                    var wasRendered = pd.IsRenderedDocumentView;
                    Project.Remove(oldName);
                    var newPd = new ProgramData();
                    newPd.AddToObjects = IsBasicPlusExtension(fi.Extension);
                    newPd.IsRenderedDocumentView = wasRendered;
                    newPd.Item = CurrentProgram;
                    //
                    //newPd.Name = fi.Name.Replace(ext, "");
                    newPd.Name = fi.Name;
                    //
                    newPd.Path = fi.DirectoryName;
                    newPd.FullPath = fi.FullName;
                    EnsureProjectRootFromFile(fi.FullName);
                    ApplyProjectMembership(newPd);
                    newPd.TextChange = false;
                    Project.Add(newPd.Name, newPd);
                    newPd.ApplyDocumentViewMode();
                    NotifyDocumentViewSwitchChanged();
                    currentPath = fi.FullName;
                    GitPanelVM.NotifySavedWorkspaceFilesChanged();
                    //projectData = Project.GetProgramData(CurrentProgram);
                }
            }
            catch (Exception ex)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(ex.Message);
            }
            ButtonsPanel.GetFilePopup.IsOpen = false;
        }

        public void EditCopy()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.Copy();
                data.Menu.IsOpen = false;
            }
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        public void EditPaste()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.Paste();
                data.Menu.IsOpen = false;
            }
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        public void EditCut()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.Cut();
                data.Menu.IsOpen = false;
            }
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        public void EditUndo()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.Undo();
                data.Menu.IsOpen = false;
            }
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        public void EditRedo()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.Redo();
                data.Menu.IsOpen = false;
            }
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        private void EditSelectAll()
        {
            highlightAll = true;
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.SelectAll();
                data.Menu.IsOpen = false;
            }
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        private void EditFormat()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.Lexer.Format();
                data.Menu.IsOpen = false;
            }
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        private void EditShowLine()
        {
            //indent = !indent;
            if (GetOpenProgramCount() > 0)
            {
                foreach (var doc in GetOpenProgramItems())
                {
                    var data = Project.GetProgramData(doc);
                    if (data != null)
                        data.Editor.IndentationGuides = ShowLine ? IndentView.LookBoth : IndentView.None;
                }
            }
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        private void EditShowNumber()
        {
            if (GetOpenProgramCount() > 0)
            {
                foreach (var doc in GetOpenProgramItems())
                {
                    var data = Project.GetProgramData(doc);
                    if (data != null)
                        data.Editor.TextArea.Margins[TextEditor.NUMBER_MARGIN].Width = ShowNumber ? Math.Max(50, 10 * (int)Math.Log10(data.Editor.TextArea.Lines.Count)) : 0;
                }
            }
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        private void EditShowSpace()
        {
            if (GetOpenProgramCount() > 0)
            {
                foreach (var doc in GetOpenProgramItems())
                {
                    var data = Project.GetProgramData(doc);
                    if (data != null)
                        data.Editor.ViewWhitespace = ShowSpace ? WhitespaceMode.VisibleAlways : WhitespaceMode.Invisible;
                }
            }
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        private void EditWrap()
        {
            if (GetOpenProgramCount() > 0)
            {
                foreach (var doc in GetOpenProgramItems())
                {
                    var data = Project.GetProgramData(doc);
                    if (data != null)
                        data.Editor.WrapMode = ShowWrap ? WrapMode.Whitespace : WrapMode.None;
                }
            }
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        private void FindText()
        {
            FindNext();
            ButtonsPanel.GetEditPopup.IsOpen = false;
        }
        private void EditComment()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.Comment(true);
                data.Menu.IsOpen = false;
            }
        }
        private void EditUnComment()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.Comment(false);
                data.Menu.IsOpen = false;
            }
        }
        private void EditFoldAll()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.FoldAll();
                data.Menu.IsOpen = false;
            }
        }
        private void EditUnFoldAll()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.FoldAll();
                data.Menu.IsOpen = false;
            }
        }

        private void EditFoldFunc()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.FoldFunc();
                data.Menu.IsOpen = false;
            }
        }
        private void EditUnFoldFunc()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null)
            {
                data.Editor.FoldFunc();
                data.Menu.IsOpen = false;
            }
        }

        public void FindNext()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null && TextFind != "")
                data.Editor.searchManager.Find(true, TextFind);
        }
        private void FindPrevious()
        {
            var data = Project.GetProgramData(CurrentProgram);
            if (data != null && TextFind != "")
                data.Editor.searchManager.Find(false, TextFind);
        }
        private void FileCloseAll()
        {
            if (GetOpenProgramCount() == 0)
            {
                return;
            }

            try
            {
                if (_isEditorSplitActive)
                    DisableEditorSplit();

                int result = 0;
                ObservableCollection<TabItem> items = new ObservableCollection<TabItem>();
                foreach (var item in ProgramNameList)
                {
                    string name = Project.GetProgramData(item).Name;
                    new CloseProgram().CloseAll(ProgramNameList, name, out result);
                    if (result == -1)
                    {
                        items.Add(item);
                    }
                    else
                    {
                        Project.Remove(name);
                    }
                }

                if (items.Count > 0)
                {
                    items[0].Style = (System.Windows.Style)Application.Current.Resources["FirstTabItem"];
                    
                    ProgramNameList = items;
                    CurrentProgram = items[0];
                }
                else
                {
                    CurrentProgram = null;
                    ProgramNameList.Clear();
                    IntellisenseParser.Data.Clear();
                }
                NotifyEditorContentChanged();
            }
            catch (Exception ex)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(ex.Message);
            }
            ButtonsPanel.GetFilePopup.IsOpen = false;
        }
        private void CloseProgram()
        {
            try
            {
                int result = 0;
                var name = EditorHeader.CloseName;
                var targetList = ProgramNameList;
                if (Project.ContainsKey(name) && SecondaryProgramNameList.Contains(Project.GetDictionary()[name].Item))
                    targetList = SecondaryProgramNameList;
                var targetWasSecondary = targetList == SecondaryProgramNameList;

                var updatedList = new CloseProgram().Close(targetList, name, out result);
                if (targetWasSecondary)
                    SecondaryProgramNameList = updatedList;
                else
                    ProgramNameList = updatedList;

                if (result == 0)
                {
                    Project.Remove(name);

                    /*
                    if (BpObjects.IntelliData.ContainsKey(name))
                    {
                        BpObjects.IntelliData.Remove(name);
                    }
                    */
                }

                if (_isEditorSplitActive && (ProgramNameList.Count == 0 || SecondaryProgramNameList.Count == 0))
                    DisableEditorSplit();

                var preferredList = targetWasSecondary ? SecondaryProgramNameList : ProgramNameList;
                var nextProgram = preferredList.Count > 0 ? preferredList[0] : GetOpenProgramItems().FirstOrDefault();
                if (nextProgram != null)
                {
                    CurrentProgram = nextProgram;
                    NormalizeEditorTabStyles();
                }
                else
                {
                    CurrentProgram = null;
                    IntellisenseParser.Data.Clear();
                }
                NotifyEditorContentChanged();
                EditorPanel.GetPopup.IsOpen = false;
            }
            catch (Exception ex)
            {
                CommonData.Status.Clear();
                CommonData.Status.Add(ex.Message);
            }
        }

        

        public static Dictionary<string, string> GetLocalization
        {
            get
            {
                return GUILanguage.Items;
            }
        }

        private static string GetLocalizedOrDefault(string key, string fallback)
        {
            string value;
            return GetLocalization != null && GetLocalization.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
        }

        public static Dictionary<string, string> GetErrors
        {
            get
            {
                return App.Errors;
            }
        }

        public Visibility AiFunctionsVisibility
        {
            get { return AiAppSettingsStore.Settings.AiEnabled ? Visibility.Visible : Visibility.Collapsed; }
        }

        public bool AiCodeCompletionsEnabled
        {
            get { return AiAppSettingsStore.Settings.AiCodeCompletionsEnabled; }
        }

        public bool CanFixBugs
        {
            get { return false; }
        }

        public string FixBugsToolTip
        {
            get
            {
                return GetLocalizedOrDefault("tFixBugsUnavailable", "Fix bugs is temporarily unavailable");
            }
        }

        private static Visibility _hiddenProgramButton;
        public static Visibility HiddenProgramButton
        {
            get { return _hiddenProgramButton; }
            set
            {
                _hiddenProgramButton = value;
                OnStaticPropertyChanged("HiddenProgramButton");
            }
        }

        #region HomeButton

        private static Visibility _fileNewV;
        public static Visibility FileNewV
        {
            get { return _fileNewV; }
            set
            {
                _fileNewV = value;
                OnStaticPropertyChanged("FileNewV");
                FileDVisibility();
            }
        }

        private static Visibility _fileOpenV;
        public static Visibility FileOpenV
        {
            get { return _fileOpenV; }
            set
            {
                _fileOpenV = value;
                OnStaticPropertyChanged("FileOpenV");
                FileDVisibility();
            }
        }

        private static Visibility _fileCloseAllV;
        public static Visibility FileCloseAllV
        {
            get { return _fileCloseAllV; }
            set
            {
                _fileCloseAllV = value;
                OnStaticPropertyChanged("FileCloseAllV");
                FileDVisibility();
            }
        }

        private static Visibility _fileSaveV;
        public static Visibility FileSaveV
        {
            get { return _fileSaveV; }
            set
            {
                _fileSaveV = value;
                OnStaticPropertyChanged("FileSaveV");
                FileDVisibility();
            }
        }

        private static Visibility _fileSaveAllV;
        public static Visibility FileSaveAllV
        {
            get { return _fileSaveAllV; }
            set
            {
                _fileSaveAllV = value;
                OnStaticPropertyChanged("FileSaveAllV");
                FileDVisibility();
            }
        }

        private static Visibility _fileSaveAsV;
        public static Visibility FileSaveAsV
        {
            get { return _fileSaveAsV; }
            set
            {
                _fileSaveAsV = value;
                OnStaticPropertyChanged("FileSaveAsV");
                FileDVisibility();
            }
        }

        private static Visibility _fileDV;
        public static Visibility FileDV
        {
            get { return _fileDV; }
            set
            {
                _fileDV = value;
                OnStaticPropertyChanged("FileDV");
            }
        }

        private static Visibility _editCopyV;
        public static Visibility EditCopyV
        {
            get { return _editCopyV; }
            set
            {
                _editCopyV = value;
                OnStaticPropertyChanged("EditCopyV");
                EditDV1isibility();
            }
        }

        private static Visibility _editCutV;
        public static Visibility EditCutV
        {
            get { return _editCutV; }
            set
            {
                _editCutV = value;
                OnStaticPropertyChanged("EditCutV");
                EditDV1isibility();
            }
        }

        private static Visibility _editFormatV;
        public static Visibility EditFormatV
        {
            get { return _editFormatV; }
            set
            {
                _editFormatV = value;
                OnStaticPropertyChanged("EditFormatV");
                EditDV2isibility();
            }
        }

        private static Visibility _editPasteV;
        public static Visibility EditPasteV
        {
            get { return _editPasteV; }
            set
            {
                _editPasteV = value;
                OnStaticPropertyChanged("EditPasteV");
                EditDV1isibility();
            }
        }

        private static Visibility _editRedoV;
        public static Visibility EditRedoV
        {
            get { return _editRedoV; }
            set
            {
                _editRedoV = value;
                OnStaticPropertyChanged("EditRedoV");
                EditDV4isibility();
            }
        }

        private static Visibility _editSelectAllV;
        public static Visibility EditSelectAllV
        {
            get { return _editSelectAllV; }
            set
            {
                _editSelectAllV = value;
                OnStaticPropertyChanged("EditSelectAllV");
                EditDV1isibility();
            }
        }

        private static Visibility _editShowLineV;
        public static Visibility EditShowLineV
        {
            get { return _editShowLineV; }
            set
            {
                _editShowLineV = value;
                OnStaticPropertyChanged("EditShowLineV");
                EditDV2isibility();
            }
        }

        private static Visibility _editShowNumberV;
        public static Visibility EditShowNumberV
        {
            get { return _editShowNumberV; }
            set
            {
                _editShowNumberV = value;
                OnStaticPropertyChanged("EditShowNumberV");
                EditDV2isibility();
            }
        }

        private static Visibility _editShowSpaceV;
        public static Visibility EditShowSpaceV
        {
            get { return _editShowSpaceV; }
            set
            {
                _editShowSpaceV = value;
                OnStaticPropertyChanged("EditShowSpaceV");
                EditDV2isibility();
            }
        }

        private static Visibility _editUndoV;
        public static Visibility EditUndoV
        {
            get { return _editUndoV; }
            set
            {
                _editUndoV = value;
                OnStaticPropertyChanged("EditUndoV");
                EditDV4isibility();
            }
        }

        private static Visibility _editWrapV;
        public static Visibility EditWrapV
        {
            get { return _editWrapV; }
            set
            {
                _editWrapV = value;
                OnStaticPropertyChanged("EditWrapV");
                EditDV2isibility();
            }
        }

        private static Visibility _editFindV;
        public static Visibility EditFindV
        {
            get { return _editFindV; }
            set
            {
                _editFindV = value;
                OnStaticPropertyChanged("EditFindV");
                EditDV3isibility();
            }
        }

        private static Visibility _editDV1;
        public static Visibility EditDV1
        {
            get { return _editDV1; }
            set
            {
                _editDV1 = value;
                OnStaticPropertyChanged("EditDV1");
            }
        }

        private static Visibility _editDV2;
        public static Visibility EditDV2
        {
            get { return _editDV2; }
            set
            {
                _editDV2 = value;
                OnStaticPropertyChanged("EditDV2");
            }
        }

        private static Visibility _editDV3;
        public static Visibility EditDV3
        {
            get { return _editDV3; }
            set
            {
                _editDV3 = value;
                OnStaticPropertyChanged("EditDV3");
            }
        }

        private static Visibility _editDV4;
        public static Visibility EditDV4
        {
            get { return _editDV4; }
            set
            {
                _editDV4 = value;
                OnStaticPropertyChanged("EditDV4");
            }
        }

        private static Visibility _compileV;
        public static Visibility CompileV
        {
            get { return _compileV; }
            set
            {
                _compileV = value;
                OnStaticPropertyChanged("CompileV");
            }
        }

        private static Visibility _compileAndRunV;
        public static Visibility CompileAndRunV
        {
            get { return _compileAndRunV; }
            set
            {
                _compileAndRunV = value;
                OnStaticPropertyChanged("CompileAndRunV");
            }
        }

        private static Visibility _connectV;
        public static Visibility ConnectV
        {
            get { return _connectV; }
            set
            {
                _connectV = value;
                OnStaticPropertyChanged("ConnectV");
            }
        }

        private static Visibility _downloadV;
        public static Visibility DownloadV
        {
            get { return _downloadV; }
            set
            {
                _downloadV = value;
                OnStaticPropertyChanged("DownloadV");
            }
        }

        private static Visibility _stopV;
        public static Visibility StopV
        {
            get { return _stopV; }
            set
            {
                _stopV = value;
                OnStaticPropertyChanged("StopV");
            }
        }

        private static void FileDVisibility()
        {
            if (FileNewV != Visibility.Visible && FileOpenV != Visibility.Visible && FileCloseAllV != Visibility.Visible && FileSaveV != Visibility.Visible && FileSaveAllV != Visibility.Visible && FileSaveAsV != Visibility.Visible)
            {
                FileDV = Visibility.Collapsed;
            }
            else
            {
                FileDV = Visibility.Visible;
            }
        }

        private static void EditDV1isibility()
        {
            if (_editSelectAllV != Visibility.Visible && EditCopyV != Visibility.Visible && EditPasteV != Visibility.Visible && EditCutV != Visibility.Visible)
            {
                EditDV1 = Visibility.Collapsed;
            }
            else
            {
                EditDV1 = Visibility.Visible;
            }
        }

        private static void EditDV2isibility()
        {
            if (EditShowLineV != Visibility.Visible && EditShowNumberV != Visibility.Visible && EditShowSpaceV != Visibility.Visible && EditWrapV != Visibility.Visible && EditFormatV != Visibility.Visible)
            {
                EditDV2 = Visibility.Collapsed;
            }
            else
            {
                EditDV2 = Visibility.Visible;
            }
        }

        private static void EditDV3isibility()
        {
            if (EditFindV != Visibility.Visible)
            {
                EditDV3 = Visibility.Collapsed;
            }
            else
            {
                EditDV3 = Visibility.Visible;
            }
        }

        private static void EditDV4isibility()
        {
            if (EditUndoV != Visibility.Visible && EditRedoV != Visibility.Visible)
            {
                EditDV4 = Visibility.Collapsed;
            }
            else
            {
                EditDV4 = Visibility.Visible;
            }
        }

        #endregion

        private void EditShowLineMenu()
        {
            ShowLine = !ShowLine;
            EditShowLine();
        }
        private void EditShowNumberMenu()
        {
            ShowNumber = !ShowNumber;
            EditShowNumber();
        }
        private void EditShowSpaceMenu()
        {
            ShowSpace = !ShowSpace;
            EditShowSpace();
        }
        private void EditWrapMenu()
        {
            ShowWrap = !ShowWrap;
            EditWrap();
        }

        private static void SetProgramName()
        {
            var list = new StackPanel();
            var scroll = new ScrollViewer();
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            for (int i = 0; i < ProgramNameList.Count; i++)
            {
                TabItem pnl = ProgramNameList[i];
                if (pnl.Visibility == Visibility.Visible)
                {
                    continue;
                }
                var item = new ProgramNamePanel();
                var cbi = Project.GetProgramData(pnl).Name;
                item.HeaderName = cbi;
                item.GetTextBlock.PreviewMouseLeftButtonDown += Cbi_PreviewMouseLeftButtonDown;

                if (i == 0)
                {
                    item.GetBorder.BorderThickness = new Thickness(0, 0, 0, 0.5);
                }
                else if (i == ProgramNameList.Count - 1)
                {
                    item.GetBorder.BorderThickness = new Thickness(0, 0.5, 0, 0);
                }

                if (ProgramNameList.Count == 1)
                {
                    item.GetBorder.BorderThickness = new Thickness(0, 0, 0, 0);
                }

                list.Children.Add(item);
            }
            scroll.Content = list;

            if (ProgramNameList.Count > 20)
            {
                EditorPanel.GetPopup.Width = 200;
                EditorPanel.GetPopup.HorizontalOffset = -200;
            }
            else
            {
                EditorPanel.GetPopup.Width = 180;
                EditorPanel.GetPopup.HorizontalOffset = -180;
            }

            EditorPanel.GetPopup.Child = scroll;
            EditorPanel.GetPopup.IsOpen = true;
        }

        private static void Cbi_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ProgramNameList.Count < 2)
                return;

            var tb = (TextBlock)sender;
            string name = tb.Text;
            int iN = ProgramNameList.IndexOf(Project.GetDictionary()[name].Item);

            var tiN = ProgramNameList[iN];
            var ti0 = ProgramNameList[0];

            var ti0v = ti0.Visibility;
            var tiNv = tiN.Visibility;

            ti0.Visibility = tiNv;
            tiN.Visibility = ti0v;

            var pnl = new ObservableCollection<TabItem>();
            tiN.Style = (System.Windows.Style)Application.Current.Resources["FirstTabItem"];

            pnl.Add(tiN);
            for (int item = 0; item < ProgramNameList.Count; item++)
            {
                TabItem i;
                if (item != iN)
                {
                    i = ProgramNameList[item];
                    i.Style = (System.Windows.Style)Application.Current.Resources["NextTabItem"];
                    pnl.Add(i);
                }
            }

            ProgramNameList = pnl;
            ProgramNameList[0].IsSelected = true;
            CurrentProgram = ProgramNameList[0];
            EditorPanel.GetPopup.IsOpen = false;
        }

        private void About()
        {
            var about = new View.Dialogs.About();
            about.ShowDialog();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged([CallerMemberName]string prop = "")
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(prop));
            }
        }

        public static event PropertyChangedEventHandler StaticPropertyChanged;
        private static void OnStaticPropertyChanged(string propertyName)
        {
            if (_instance != null)
                _instance.OnPropertyChanged(propertyName);

            var handler = StaticPropertyChanged;
            if (handler != null)
            {
                handler(null, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
