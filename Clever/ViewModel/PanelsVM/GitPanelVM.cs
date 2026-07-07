using Clever.CommonData;
using Clever.Model.Utils;
using Clever.ViewModel;
using Clever.ViewModel.BaseVM;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Clever.ViewModel.PanelsVM
{
    internal class GitPanelVM : BaseViewModel
    {
        private enum GitDesignUiState
        {
            GitMissing,
            RepoMissing,
            DirtyNoRemote,
            CleanNoRemote,
            CleanAfterCommitNoRemote,
            CleanWithRemote
        }

        private readonly IList<GitChangeItemVM> _designChanges;
        private GitDesignUiState _state;
        private string _commitMessage;
        private string _statusMessage;
        private string _branchName;
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
        private string _remoteOwner;
        private string _remoteRepository;

        public GitPanelVM()
        {
            BranchName = "develop";
            _remoteUrl = "https://github.com/student/clev3r-lesson.git";
            _remoteOwner = "student";
            _remoteRepository = "clev3r-lesson";
            _designChanges = GitDesignData.CreateChanges();
            Branches = new ObservableCollection<GitBranchItemVM>
            {
                new GitBranchItemVM("develop", true),
                new GitBranchItemVM("feature/git-help", false),
                new GitBranchItemVM("backup/media", false),
                new GitBranchItemVM("main", false)
            };
            Commits = new ObservableCollection<GitCommitVM>(GitDesignData.CreateCommits());
            RebuildGraphRows();
            ApplyState(GitDesignUiState.GitMissing);

            SetupGitCommand = new RelayCommand(AdvanceSetupState, true);
            CommitCommand = new RelayCommand(ExecutePrimaryGitAction, true);
            PullCommand = new RelayCommand(param => SetLocalizedStatus("GitStatusPull", "Pull is a UI action in this pass."), true);
            PushCommand = new RelayCommand(param => SetLocalizedStatus("GitStatusPush", "Push is a UI action in this pass."), true);
            ReloadCommand = new RelayCommand(param => SetLocalizedStatus("GitStatusReload", "Design data refreshed."), true);
            SelectBranchCommand = new RelayCommand(param => IsBranchPopupOpen = !IsBranchPopupOpen, true);
            SelectBranchItemCommand = new RelayCommand(SelectBranch, true);
            OpenRemotePopupCommand = new RelayCommand(param => OpenRemotePopup(), true);
            RemoteCheckCommand = new RelayCommand(param => ShowRemoteCredentials(), true);
            RemoteConnectCommand = new RelayCommand(param => ConnectRemote(), true);
            ToggleCommitCommand = new RelayCommand(ToggleCommit, true);
            RevertCommitCommand = new RelayCommand(param => SetCommitActionStatus(param, "GitStatusRevert", "Revert commit is a UI action in this pass."), true);
            DiffCommitCommand = new RelayCommand(OpenCommitDiff, true);
            OpenChangeFileCommand = new RelayCommand(OpenChangeFile, true);
            GenerateCommitMessageCommand = new RelayCommand(param => GenerateCommitMessage(), true);
            AiAppSettingsStore.SettingsChanged += AiAppSettingsStore_SettingsChanged;

            if (GraphRows.Count > 0)
                SelectCommit(GraphRows[0].Commit, false);
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
            get { return _state == GitDesignUiState.GitMissing || _state == GitDesignUiState.RepoMissing ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility MainGitVisibility
        {
            get { return SetupVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string SetupButtonText
        {
            get
            {
                return _state == GitDesignUiState.GitMissing
                    ? GetText("GitInstallGit", "Install Git")
                    : GetText("GitInitRepoAndAdd", "Init repo and add .");
            }
        }

        public string GitActionButtonText
        {
            get
            {
                if (_state == GitDesignUiState.CleanAfterCommitNoRemote)
                    return GetText("GitConnectRemote", "Connect to remote");
                if (_state == GitDesignUiState.CleanWithRemote)
                    return GetText("GitSyncChanges", "Sync changes");

                return GetText("GitCommit", "Commit");
            }
        }

        public Visibility GitCommitActionIconVisibility
        {
            get { return _state == GitDesignUiState.DirtyNoRemote ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility GitRemoteActionIconVisibility
        {
            get { return _state == GitDesignUiState.CleanAfterCommitNoRemote ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility GitSyncActionIconVisibility
        {
            get { return _state == GitDesignUiState.CleanWithRemote ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility GitSyncCountersVisibility
        {
            get { return _state == GitDesignUiState.CleanWithRemote ? Visibility.Visible : Visibility.Collapsed; }
        }

        public int GitOutgoingCommitCount
        {
            get { return _state == GitDesignUiState.CleanWithRemote ? 2 : 0; }
        }

        public int GitIncomingCommitCount
        {
            get { return _state == GitDesignUiState.CleanWithRemote ? 1 : 0; }
        }

        public bool IsGitActionButtonEnabled
        {
            get { return _state == GitDesignUiState.DirtyNoRemote || _state == GitDesignUiState.CleanAfterCommitNoRemote || _state == GitDesignUiState.CleanWithRemote; }
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
            get { return _state == GitDesignUiState.CleanWithRemote; }
        }

        public Visibility RemoteConnectedVisibility
        {
            get { return _state == GitDesignUiState.CleanWithRemote ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility RemoteConnectVisibility
        {
            get { return RemoteConnectedVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string RemoteStatusText
        {
            get { return "Remote: " + _remoteOwner + ":" + _remoteRepository; }
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

        private void AdvanceSetupState(object parameter)
        {
            if (_state == GitDesignUiState.GitMissing)
            {
                ApplyState(GitDesignUiState.RepoMissing);
                SetLocalizedStatus("GitStatusInstallGit", "Git install step completed in design mode.");
                return;
            }

            if (_state == GitDesignUiState.RepoMissing)
            {
                ApplyState(GitDesignUiState.DirtyNoRemote);
                SetLocalizedStatus("GitStatusInitRepo", "Repository initialized in design mode.");
            }
        }

        private void ExecutePrimaryGitAction(object parameter)
        {
            if (_state == GitDesignUiState.DirtyNoRemote)
            {
                CommitMessage = "";
                ApplyState(GitDesignUiState.CleanAfterCommitNoRemote);
                SetLocalizedStatus("GitStatusCommitDone", "Commit saved. Remote is not connected yet.");
                return;
            }

            if (_state == GitDesignUiState.CleanAfterCommitNoRemote)
            {
                OpenRemotePopup();
                return;
            }

            if (_state == GitDesignUiState.CleanWithRemote)
                SetLocalizedStatus("GitStatusSync", "Changes synchronized in design mode.");
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

        private void SetStatus(string message)
        {
            StatusMessage = message;
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
            if (branch == null)
                return;

            foreach (var item in Branches)
                item.IsActive = item == branch;

            BranchName = branch.Name;
            IsBranchPopupOpen = false;
            SetLocalizedStatus("GitStatusBranchSelected", "Branch selected.");
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
            SetLocalizedStatus("GitStatusRemoteChecked", "Remote URL checked in design mode.");
        }

        private void ConnectRemote()
        {
            if (!IsRemoteConnectSubmitEnabled)
                return;

            var repoName = ParseRemoteName(RemoteUrl);
            if (!string.IsNullOrEmpty(repoName))
                _remoteRepository = repoName;

            IsRemotePopupOpen = false;
            ApplyState(GitDesignUiState.CleanWithRemote);
            SetLocalizedStatus("GitStatusRemoteConnected", "Remote connected in design mode.");
        }

        private void ApplyState(GitDesignUiState state)
        {
            _state = state;
            Changes = state == GitDesignUiState.DirtyNoRemote
                ? new ObservableCollection<GitChangeItemVM>(_designChanges)
                : new ObservableCollection<GitChangeItemVM>();

            OnPropertyChanged("SetupVisibility");
            OnPropertyChanged("MainGitVisibility");
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

        private static string ParseRemoteName(string remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
                return "";

            var trimmed = remoteUrl.Trim().TrimEnd('/');
            var lastSlash = trimmed.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < trimmed.Length - 1)
                trimmed = trimmed.Substring(lastSlash + 1);

            return trimmed.EndsWith(".git") ? trimmed.Substring(0, trimmed.Length - 4) : trimmed;
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

            MainWindowVM.OpenGitDiffPreview(commit);
            SetCommitActionStatus(commit, "GitStatusDiffOpened", "Opened read-only diff:");
        }

        private void OpenChangeFile(object parameter)
        {
            var file = parameter as GitChangeItemVM;
            if (file == null)
                return;

            MainWindowVM.OpenGitChangeFile(file);
            var message = GUILanguage.GetItem("GitStatusFileOpened");
            StatusMessage = (string.IsNullOrEmpty(message) ? "Opened file from Git changes:" : message) + " " + file.FileName;
        }

        private void GenerateCommitMessage()
        {
            if (!AiAppSettingsStore.Settings.AiEnabled)
                return;

            CommitMessage = Changes != null && Changes.Count > 0
                ? "Save current project changes"
                : "Update project backup";
            SetLocalizedStatus("GitStatusGenerateCommitMessage", "Commit message generated in design mode.");
        }

        private void AiAppSettingsStore_SettingsChanged(object sender, System.EventArgs e)
        {
            OnPropertyChanged("AiGenerateVisibility");
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
