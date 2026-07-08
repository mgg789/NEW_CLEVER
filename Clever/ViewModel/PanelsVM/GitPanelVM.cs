using Clever.CommonData;
using Clever.Model.Utils;
using Clever.ViewModel;
using Clever.ViewModel.BaseVM;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clever.ViewModel.PanelsVM
{
    internal class GitPanelVM : BaseViewModel
    {
        private enum GitUiState
        {
            GitMissing,
            FileMissing,
            RepoMissing,
            Ready
        }

        private readonly GitRepositoryService _git;
        private GitUiState _state;
        private string _commitMessage;
        private string _statusMessage;
        private string _branchName;
        private string _workingDirectory;
        private string _repositoryRoot;
        private GitCommitVM _selectedCommit;
        private ObservableCollection<GitGraphRowVM> _graphRows;
        private ObservableCollection<GitChangeItemVM> _changes;
        private double _graphWidth;
        private bool _isBranchPopupOpen;
        private bool _isRemotePopupOpen;
        private bool _isRemoteCredentialsVisible;
        private bool _useTokenAuth;
        private string _remoteUrl;
        private string _remoteLogin;
        private string _remotePassword;
        private string _remoteToken;
        private readonly DispatcherTimer _savedFilesRefreshTimer;

        public GitPanelVM()
        {
            _git = new GitRepositoryService();
            _remoteUrl = "https://github.com/student/clev3r-lesson.git";
            Branches = new ObservableCollection<GitBranchItemVM>();
            Commits = new ObservableCollection<GitCommitVM>();
            Changes = new ObservableCollection<GitChangeItemVM>();
            GraphRows = new ObservableCollection<GitGraphRowVM>();
            BranchName = "";

            SetupGitCommand = new RelayCommand(ExecuteSetupAction, true);
            CommitCommand = new RelayCommand(ExecuteCommit, true);
            PullCommand = new RelayCommand(param => SetLocalizedStatus("GitStatusRemoteStub", "Remote/cloud Git is not implemented yet."), true);
            PushCommand = new RelayCommand(param => SetLocalizedStatus("GitStatusRemoteStub", "Remote/cloud Git is not implemented yet."), true);
            ReloadCommand = new RelayCommand(param => RefreshRepositoryState(), true);
            SelectBranchCommand = new RelayCommand(param => IsBranchPopupOpen = !IsBranchPopupOpen, true);
            SelectBranchItemCommand = new RelayCommand(SelectBranch, true);
            OpenRemotePopupCommand = new RelayCommand(param => OpenRemotePopup(), true);
            RemoteCheckCommand = new RelayCommand(param => ShowRemoteCredentials(), true);
            RemoteConnectCommand = new RelayCommand(param => ConnectRemote(), true);
            ToggleCommitCommand = new RelayCommand(ToggleCommit, true);
            RevertCommitCommand = new RelayCommand(RevertCommit, true);
            DiffCommitCommand = new RelayCommand(OpenCommitDiff, true);
            OpenChangeFileCommand = new RelayCommand(OpenChangeFile, true);
            GenerateCommitMessageCommand = new RelayCommand(param => GenerateCommitMessage(), true);
            AiAppSettingsStore.SettingsChanged += AiAppSettingsStore_SettingsChanged;
            SavedWorkspaceFilesChanged += GitPanelVM_SavedWorkspaceFilesChanged;
            _savedFilesRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _savedFilesRefreshTimer.Tick += SavedFilesRefreshTimer_Tick;

            RefreshRepositoryState();
        }

        internal static event EventHandler SavedWorkspaceFilesChanged;

        internal static void NotifySavedWorkspaceFilesChanged()
        {
            var handler = SavedWorkspaceFilesChanged;
            if (handler != null)
                handler(null, EventArgs.Empty);
        }

        public string BranchName
        {
            get { return _branchName; }
            private set
            {
                _branchName = value;
                OnPropertyChanged("BranchName");
            }
        }

        public ObservableCollection<GitChangeItemVM> Changes
        {
            get { return _changes; }
            private set
            {
                _changes = value;
                OnPropertyChanged("Changes");
                OnPropertyChanged("IsGitActionButtonEnabled");
                OnPropertyChanged("GitActionButtonBackground");
                OnPropertyChanged("GitActionButtonForeground");
            }
        }

        public ObservableCollection<GitBranchItemVM> Branches { get; private set; }
        public ObservableCollection<GitCommitVM> Commits { get; private set; }
        public ICommand SetupGitCommand { get; private set; }
        public ICommand CommitCommand { get; private set; }
        public ICommand PullCommand { get; private set; }
        public ICommand PushCommand { get; private set; }
        public ICommand ReloadCommand { get; private set; }
        public ICommand SelectBranchCommand { get; private set; }
        public ICommand SelectBranchItemCommand { get; private set; }
        public ICommand OpenRemotePopupCommand { get; private set; }
        public ICommand RemoteCheckCommand { get; private set; }
        public ICommand RemoteConnectCommand { get; private set; }
        public ICommand ToggleCommitCommand { get; private set; }
        public ICommand RevertCommitCommand { get; private set; }
        public ICommand DiffCommitCommand { get; private set; }
        public ICommand OpenChangeFileCommand { get; private set; }
        public ICommand GenerateCommitMessageCommand { get; private set; }

        public ObservableCollection<GitGraphRowVM> GraphRows
        {
            get { return _graphRows; }
            private set
            {
                _graphRows = value;
                OnPropertyChanged("GraphRows");
            }
        }

        public double GraphWidth
        {
            get { return _graphWidth; }
            private set
            {
                _graphWidth = value;
                OnPropertyChanged("GraphWidth");
            }
        }

        public Dictionary<string, string> GetLocalization
        {
            get { return GUILanguage.Items; }
        }

        public Visibility AiGenerateVisibility
        {
            get { return AiAppSettingsStore.Settings.AiEnabled ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility SetupVisibility
        {
            get { return _state == GitUiState.Ready ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility MainGitVisibility
        {
            get { return _state == GitUiState.Ready ? Visibility.Visible : Visibility.Collapsed; }
        }

        public bool IsSetupButtonEnabled
        {
            get { return _state != GitUiState.Ready; }
        }

        public string SetupButtonText
        {
            get
            {
                if (_state == GitUiState.GitMissing)
                    return GetText("GitInstallGit", "Install Git");
                if (_state == GitUiState.RepoMissing)
                    return GetText("GitInitRepoAndAdd", "Init repository");

                return GetText("GitOpenSavedFile", "Open saved .bp file");
            }
        }

        public string GitActionButtonText
        {
            get { return GetText("GitCommit", "Commit"); }
        }

        public Visibility GitCommitActionIconVisibility
        {
            get { return _state == GitUiState.Ready ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility GitRemoteActionIconVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public Visibility GitSyncActionIconVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public Visibility GitSyncCountersVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public int GitOutgoingCommitCount
        {
            get { return 0; }
        }

        public int GitIncomingCommitCount
        {
            get { return 0; }
        }

        public bool IsGitActionButtonEnabled
        {
            get { return _state == GitUiState.Ready && Changes != null && Changes.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage); }
        }

        public Brush GitActionButtonBackground
        {
            get { return IsGitActionButtonEnabled ? CreateBrush("#0A86FF") : CreateBrush("#D6DAE4"); }
        }

        public Brush GitActionButtonForeground
        {
            get { return IsGitActionButtonEnabled ? Brushes.White : CreateBrush("#8F98AB"); }
        }

        public bool RemoteToolsEnabled
        {
            get { return _state == GitUiState.Ready; }
        }

        public Visibility RemoteConnectedVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public Visibility RemoteConnectVisibility
        {
            get { return _state == GitUiState.Ready ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string RemoteStatusText
        {
            get { return GetText("GitStatusRemoteStub", "Remote/cloud Git is not implemented yet."); }
        }

        public bool IsBranchPopupOpen
        {
            get { return _isBranchPopupOpen; }
            set
            {
                _isBranchPopupOpen = value;
                OnPropertyChanged("IsBranchPopupOpen");
            }
        }

        public bool IsRemotePopupOpen
        {
            get { return _isRemotePopupOpen; }
            set
            {
                _isRemotePopupOpen = value;
                OnPropertyChanged("IsRemotePopupOpen");
            }
        }

        public Visibility RemoteCredentialsVisibility
        {
            get { return _isRemoteCredentialsVisible ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility RemoteCheckVisibility
        {
            get { return _isRemoteCredentialsVisible ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility RemoteConnectSubmitVisibility
        {
            get { return _isRemoteCredentialsVisible ? Visibility.Visible : Visibility.Collapsed; }
        }

        public bool IsRemoteConnectSubmitEnabled
        {
            get
            {
                if (!_isRemoteCredentialsVisible)
                    return false;

                if (UseTokenAuth)
                    return !string.IsNullOrWhiteSpace(RemoteToken);

                return !string.IsNullOrWhiteSpace(RemoteLogin) && !string.IsNullOrWhiteSpace(RemotePassword);
            }
        }

        public bool UseTokenAuth
        {
            get { return _useTokenAuth; }
            set
            {
                _useTokenAuth = value;
                OnPropertyChanged("UseTokenAuth");
                OnPropertyChanged("UseLoginPasswordAuth");
                OnPropertyChanged("LoginPasswordVisibility");
                OnPropertyChanged("TokenVisibility");
                OnPropertyChanged("IsRemoteConnectSubmitEnabled");
            }
        }

        public bool UseLoginPasswordAuth
        {
            get { return !UseTokenAuth; }
            set { UseTokenAuth = !value; }
        }

        public Visibility LoginPasswordVisibility
        {
            get { return UseTokenAuth ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility TokenVisibility
        {
            get { return UseTokenAuth ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string RemoteUrl
        {
            get { return _remoteUrl; }
            set
            {
                _remoteUrl = value;
                OnPropertyChanged("RemoteUrl");
            }
        }

        public string RemoteLogin
        {
            get { return _remoteLogin; }
            set
            {
                _remoteLogin = value;
                OnPropertyChanged("RemoteLogin");
                OnPropertyChanged("IsRemoteConnectSubmitEnabled");
            }
        }

        public string RemotePassword
        {
            get { return _remotePassword; }
            set
            {
                _remotePassword = value;
                OnPropertyChanged("RemotePassword");
                OnPropertyChanged("IsRemoteConnectSubmitEnabled");
            }
        }

        public string RemoteToken
        {
            get { return _remoteToken; }
            set
            {
                _remoteToken = value;
                OnPropertyChanged("RemoteToken");
                OnPropertyChanged("IsRemoteConnectSubmitEnabled");
            }
        }

        public string CommitMessage
        {
            get { return _commitMessage; }
            set
            {
                _commitMessage = value;
                OnPropertyChanged("CommitMessage");
                OnPropertyChanged("IsGitActionButtonEnabled");
                OnPropertyChanged("GitActionButtonBackground");
                OnPropertyChanged("GitActionButtonForeground");
            }
        }

        public string StatusMessage
        {
            get { return _statusMessage; }
            set
            {
                _statusMessage = value;
                OnPropertyChanged("StatusMessage");
            }
        }

        public GitCommitVM SelectedCommit
        {
            get { return _selectedCommit; }
            set
            {
                _selectedCommit = value;
                OnPropertyChanged("SelectedCommit");
            }
        }

        private void RefreshRepositoryState()
        {
            if (!_git.IsGitInstalled())
            {
                _repositoryRoot = "";
                _workingDirectory = "";
                BranchName = "";
                ClearGitData();
                ApplyState(GitUiState.GitMissing);
                SetLocalizedStatus("GitStatusGitMissing", "Git is not installed. Install Git for Windows, then refresh.");
                return;
            }

            var currentFile = ResolveCurrentFilePath();
            if (string.IsNullOrWhiteSpace(currentFile))
            {
                _repositoryRoot = "";
                _workingDirectory = "";
                BranchName = "";
                ClearGitData();
                ApplyState(GitUiState.FileMissing);
                SetLocalizedStatus("GitStatusOpenSavedFile", "Open and save a .bp, .bpi, or .bpm file to use Git.");
                return;
            }

            _workingDirectory = Path.GetDirectoryName(currentFile);
            _repositoryRoot = GitRepositoryService.FindRepositoryRoot(_workingDirectory);
            if (string.IsNullOrWhiteSpace(_repositoryRoot))
            {
                BranchName = new DirectoryInfo(_workingDirectory).Name;
                ClearGitData();
                ApplyState(GitUiState.RepoMissing);
                SetLocalizedStatus("GitStatusRepoMissing", "Git repository is not initialized for this folder.");
                return;
            }

            var snapshot = _git.GetSnapshot(_repositoryRoot);
            BranchName = string.IsNullOrWhiteSpace(snapshot.BranchName)
                ? GetText("GitNoCommitsYet", "No commits yet")
                : snapshot.BranchName;

            Branches.Clear();
            foreach (var branch in snapshot.Branches)
                Branches.Add(new GitBranchItemVM(branch.Name, branch.IsActive));

            ApplyChanges(snapshot.Changes);

            Commits.Clear();
            foreach (var commit in snapshot.Commits)
                Commits.Add(commit);

            RebuildGraphRows();
            ApplyState(GitUiState.Ready);

            if (Changes.Count > 0)
                StatusMessage = string.Format(GetText("GitStatusChangesFound", "{0} local changes."), Changes.Count);
            else
                SetLocalizedStatus("GitStatusClean", "Working tree is clean.");
        }

        private void RefreshWorkingTreeState()
        {
            if (_state != GitUiState.Ready || string.IsNullOrWhiteSpace(_repositoryRoot))
            {
                RefreshRepositoryState();
                return;
            }

            var currentFile = ResolveCurrentFilePath();
            var currentRoot = GitRepositoryService.FindRepositoryRoot(string.IsNullOrWhiteSpace(currentFile)
                ? _workingDirectory
                : Path.GetDirectoryName(currentFile));
            if (!string.Equals(currentRoot, _repositoryRoot, StringComparison.OrdinalIgnoreCase))
            {
                RefreshRepositoryState();
                return;
            }

            ApplyChanges(_git.GetWorkingTreeChanges(_repositoryRoot));
            if (Changes.Count > 0)
                StatusMessage = string.Format(GetText("GitStatusChangesFound", "{0} local changes."), Changes.Count);
            else
                SetLocalizedStatus("GitStatusClean", "Working tree is clean.");
        }

        private void ApplyChanges(IEnumerable<GitChangeInfo> changes)
        {
            Changes = new ObservableCollection<GitChangeItemVM>(
                changes.Select(item => GitRepositoryService.CreateChangeItem(
                    _repositoryRoot,
                    item.RelativePath,
                    item.Status,
                    item.StatusText,
                    item.IsDeleted,
                    item.OldPath)));
        }

        private void ExecuteSetupAction(object parameter)
        {
            if (_state == GitUiState.GitMissing)
            {
                if (_git.IsGitInstalled())
                {
                    RefreshRepositoryState();
                    return;
                }

                OpenGitInstallPage();
                SetLocalizedStatus("GitStatusInstallGit", "Opened Git for Windows download page.");
                return;
            }

            if (_state == GitUiState.FileMissing)
            {
                RefreshRepositoryState();
                if (_state == GitUiState.FileMissing)
                    SetLocalizedStatus("GitStatusOpenSavedFile", "Open and save a .bp, .bpi, or .bpm file to use Git.");
                return;
            }

            if (_state == GitUiState.RepoMissing)
            {
                var result = _git.InitRepository(_workingDirectory);
                RefreshRepositoryState();
                StatusMessage = result.Success
                    ? GetText("GitStatusInitRepo", "Repository initialized.")
                    : result.Message;
            }
        }

        private void ExecuteCommit(object parameter)
        {
            if (_state != GitUiState.Ready)
                return;

            if (string.IsNullOrWhiteSpace(CommitMessage))
            {
                SetLocalizedStatus("GitStatusCommitMessageRequired", "Commit message is required.");
                return;
            }

            var result = _git.CommitAll(_repositoryRoot, CommitMessage.Trim());
            if (result.Success)
            {
                CommitMessage = "";
                RefreshRepositoryState();
                SetLocalizedStatus("GitStatusCommitDone", "Commit saved.");
            }
            else
            {
                RefreshRepositoryState();
                StatusMessage = result.Message;
            }
        }

        private void ToggleCommit(object parameter)
        {
            var row = parameter as GitGraphRowVM;
            if (row == null || row.Commit == null)
                return;

            SelectCommit(row.Commit, !row.Commit.IsExpanded);
            RebuildGraphRows();
        }

        private void SelectCommit(GitCommitVM commit, bool expanded)
        {
            if (commit == null)
                return;

            foreach (var item in Commits)
            {
                item.IsSelected = item == commit;
                if (item != commit)
                    item.IsExpanded = false;
            }

            commit.IsExpanded = expanded;
            SelectedCommit = commit;
        }

        private void RebuildGraphRows()
        {
            GraphRows = GitGraphLayoutService.BuildRows(Commits);
            GraphWidth = GraphRows.Count == 0 ? 120 : GraphRows.Max(r => r.GraphWidth);
        }

        private void SetLocalizedStatus(string key, string fallback)
        {
            StatusMessage = GetText(key, fallback);
        }

        private void SetCommitActionStatus(object parameter, string key, string fallback)
        {
            var commit = parameter as GitCommitVM;
            var message = GetText(key, fallback);

            StatusMessage = commit == null ? message : message + " " + commit.ShortHash;
        }

        private void SelectBranch(object parameter)
        {
            var branch = parameter as GitBranchItemVM;
            if (branch == null || _state != GitUiState.Ready)
                return;

            if (branch.IsActive)
            {
                IsBranchPopupOpen = false;
                return;
            }

            var result = _git.SwitchBranch(_repositoryRoot, branch.Name);
            IsBranchPopupOpen = false;
            if (result.Success)
                MainWindowVM.ReloadOpenProjectFilesFromDisk(_repositoryRoot);
            RefreshRepositoryState();
            StatusMessage = result.Success
                ? GetText("GitStatusBranchSelected", "Branch selected.")
                : result.Message;
        }

        private void OpenRemotePopup()
        {
            IsBranchPopupOpen = false;
            _isRemoteCredentialsVisible = false;
            UseTokenAuth = false;
            OnPropertyChanged("RemoteCredentialsVisibility");
            OnPropertyChanged("RemoteCheckVisibility");
            OnPropertyChanged("RemoteConnectSubmitVisibility");
            OnPropertyChanged("IsRemoteConnectSubmitEnabled");
            IsRemotePopupOpen = true;
        }

        private void ShowRemoteCredentials()
        {
            _isRemoteCredentialsVisible = true;
            OnPropertyChanged("RemoteCredentialsVisibility");
            OnPropertyChanged("RemoteCheckVisibility");
            OnPropertyChanged("RemoteConnectSubmitVisibility");
            OnPropertyChanged("IsRemoteConnectSubmitEnabled");
            SetLocalizedStatus("GitStatusRemoteStub", "Remote/cloud Git is not implemented yet.");
        }

        private void ConnectRemote()
        {
            IsRemotePopupOpen = false;
            SetLocalizedStatus("GitStatusRemoteStub", "Remote/cloud Git is not implemented yet.");
        }

        private void ApplyState(GitUiState state)
        {
            _state = state;

            OnPropertyChanged("SetupVisibility");
            OnPropertyChanged("MainGitVisibility");
            OnPropertyChanged("IsSetupButtonEnabled");
            OnPropertyChanged("SetupButtonText");
            OnPropertyChanged("GitActionButtonText");
            OnPropertyChanged("GitCommitActionIconVisibility");
            OnPropertyChanged("GitRemoteActionIconVisibility");
            OnPropertyChanged("GitSyncActionIconVisibility");
            OnPropertyChanged("GitSyncCountersVisibility");
            OnPropertyChanged("GitOutgoingCommitCount");
            OnPropertyChanged("GitIncomingCommitCount");
            OnPropertyChanged("IsGitActionButtonEnabled");
            OnPropertyChanged("GitActionButtonBackground");
            OnPropertyChanged("GitActionButtonForeground");
            OnPropertyChanged("RemoteToolsEnabled");
            OnPropertyChanged("RemoteConnectedVisibility");
            OnPropertyChanged("RemoteConnectVisibility");
            OnPropertyChanged("RemoteStatusText");
            OnPropertyChanged("IsRemoteConnectSubmitEnabled");
        }

        private static string GetText(string key, string fallback)
        {
            var message = GUILanguage.GetItem(key);
            return string.IsNullOrEmpty(message) ? fallback : message;
        }

        private static Brush CreateBrush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private void OpenCommitDiff(object parameter)
        {
            var commit = parameter as GitCommitVM;
            if (commit == null)
            {
                SetLocalizedStatus("GitDiffNoData", "No diff data for this commit.");
                return;
            }

            if (commit.DiffDocument == null)
                commit.DiffDocument = _git.GetCommitDiff(_repositoryRoot, commit);

            if (commit.DiffDocument == null || commit.DiffDocument.Files.Count == 0)
            {
                SetLocalizedStatus("GitDiffNoData", "No diff data for this commit.");
                return;
            }

            MainWindowVM.OpenGitDiffPreview(commit);
            SetCommitActionStatus(commit, "GitStatusDiffOpened", "Opened read-only diff:");
        }

        private void RevertCommit(object parameter)
        {
            var commit = parameter as GitCommitVM;
            if (commit == null || _state != GitUiState.Ready)
                return;

            if (!ConfirmRestoreToCommit(commit))
                return;

            var result = _git.RestoreToCommitSnapshot(_repositoryRoot, commit.Hash);
            if (result.Success)
                MainWindowVM.ReloadOpenProjectFilesFromDisk(_repositoryRoot);
            RefreshRepositoryState();
            if (result.Success)
            {
                if (string.IsNullOrWhiteSpace(CommitMessage))
                    CommitMessage = "Restore project to " + commit.ShortHash;
                SetLocalizedStatus("GitStatusRollbackPrepared", "Files restored to the selected version. Review and commit the rollback.");
            }
            else
            {
                StatusMessage = result.Message;
            }
        }

        private bool ConfirmRestoreToCommit(GitCommitVM commit)
        {
            var title = GetText("GitConfirmRestoreTitle", "Restore project version");
            var message = string.Format(
                GetText("GitConfirmRestoreMessage", "Restore all project files to commit {0}? Open files will reload from disk. Review and commit the rollback after this."),
                commit.ShortHash);

            return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private void OpenChangeFile(object parameter)
        {
            var file = parameter as GitChangeItemVM;
            if (file == null)
                return;

            if (file.IsDeleted && string.IsNullOrEmpty(file.PreviewText))
            {
                string text;
                if (GitRepositoryService.TryGetFileText(file.RepositoryRoot, file.RelativePath, out text))
                    file.PreviewText = text;
            }

            MainWindowVM.OpenGitChangeFile(file);
            var message = GUILanguage.GetItem("GitStatusFileOpened");
            StatusMessage = (string.IsNullOrEmpty(message) ? "Opened file from Git changes:" : message) + " " + file.FileName;
        }

        private void GenerateCommitMessage()
        {
            if (!AiAppSettingsStore.Settings.AiEnabled)
                return;

            RefreshWorkingTreeState();

            CommitMessage = Changes != null && Changes.Count > 0
                ? "Save " + Changes.Count + " local Git changes"
                : "Update project";
            SetLocalizedStatus("GitStatusGenerateCommitMessage", "Commit message generated.");
        }

        private void AiAppSettingsStore_SettingsChanged(object sender, EventArgs e)
        {
            OnPropertyChanged("AiGenerateVisibility");
        }

        private void GitPanelVM_SavedWorkspaceFilesChanged(object sender, EventArgs e)
        {
            _savedFilesRefreshTimer.Stop();
            _savedFilesRefreshTimer.Start();
        }

        private void SavedFilesRefreshTimer_Tick(object sender, EventArgs e)
        {
            _savedFilesRefreshTimer.Stop();
            RefreshWorkingTreeState();
        }

        private void ClearGitData()
        {
            Changes = new ObservableCollection<GitChangeItemVM>();
            Branches.Clear();
            Commits.Clear();
            SelectedCommit = null;
            RebuildGraphRows();
        }

        private static string ResolveCurrentFilePath()
        {
            if (MainWindowVM.Project == null || MainWindowVM.CurrentProgram == null)
                return "";

            var data = MainWindowVM.Project.GetProgramData(MainWindowVM.CurrentProgram);
            if (data == null || data.IsPreview || string.IsNullOrWhiteSpace(data.FullPath))
                return "";

            var extension = Path.GetExtension(data.FullPath).ToLowerInvariant();
            if (extension != ".bp" && extension != ".bpi" && extension != ".bpm")
                return "";

            return File.Exists(data.FullPath) ? data.FullPath : "";
        }

        private static void OpenGitInstallPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://git-scm.com/download/win",
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
    }

    internal class GitBranchItemVM : BaseViewModel
    {
        private bool _isActive;

        public GitBranchItemVM(string name, bool isActive)
        {
            Name = name;
            IsActive = isActive;
        }

        public string Name { get; private set; }

        public bool IsActive
        {
            get { return _isActive; }
            set
            {
                _isActive = value;
                OnPropertyChanged("IsActive");
                OnPropertyChanged("ActiveVisibility");
            }
        }

        public Visibility ActiveVisibility
        {
            get { return IsActive ? Visibility.Visible : Visibility.Hidden; }
        }
    }
}
