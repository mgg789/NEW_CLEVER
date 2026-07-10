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
using System.Threading;
using System.Threading.Tasks;
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
            ProjectMissing,
            RepoMissing,
            Ready
        }

        private enum GitPrimaryAction
        {
            Commit,
            Sync,
            Pull,
            ConnectRemote
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
        private bool _isCreateBranchPopupOpen;
        private bool _isRemotePopupOpen;
        private bool _isRemoteCredentialsVisible;
        private bool _useTokenAuth;
        private string _newBranchName;
        private string _remoteUrl;
        private string _remoteLogin;
        private string _remotePassword;
        private string _remoteToken;
        private bool _isRemoteConnected;
        private string _remoteDisplayUrl;
        private string _remotePopupStatusMessage;
        private int _remoteOutgoingCommitCount;
        private int _remoteIncomingCommitCount;
        private bool _isMergePopupOpen;
        private bool _isMergeConflictPopupOpen;
        private string _mergeSourceBranch;
        private string _mergeTargetBranch;
        private string _mergeConflictMessage;
        private readonly DispatcherTimer _savedFilesRefreshTimer;
        private readonly DispatcherTimer _projectRefreshTimer;
        private FileSystemWatcher _projectWatcher;
        private string _watchedProjectRoot;
        private readonly object _gitOperationLock = new object();
        private bool _isGitOperationRunning;
        private bool _deferredRepositoryRefresh;
        private bool _deferredWorkingTreeRefresh;
        private int _repositoryRefreshVersion;
        private int _workingTreeRefreshVersion;
        private bool _isGenerating;
        private CancellationTokenSource _commitMessageGenerationCancellation;
        private int _commitMessageGenerationVersion;
        private int _commitMessageVersion;

        private sealed class RepositoryRefreshResult
        {
            public GitUiState State { get; set; }
            public string WorkingDirectory { get; set; }
            public string RepositoryRoot { get; set; }
            public string BranchName { get; set; }
            public string WatchedRoot { get; set; }
            public string StatusMessage { get; set; }
            public GitRepositorySnapshot Snapshot { get; set; }
        }

        private sealed class WorkingTreeRefreshResult
        {
            public bool NeedsRepositoryRefresh { get; set; }
            public List<GitChangeInfo> Changes { get; set; }
            public string StatusMessage { get; set; }
        }

        private sealed class GitActionRefreshResult
        {
            public GitActionResult ActionResult { get; set; }
            public RepositoryRefreshResult RefreshResult { get; set; }
        }

        public GitPanelVM()
        {
            _git = new GitRepositoryService();
            _remoteUrl = "";
            Branches = new ObservableCollection<GitBranchItemVM>();
            Commits = new ObservableCollection<GitCommitVM>();
            Changes = new ObservableCollection<GitChangeItemVM>();
            GraphRows = new ObservableCollection<GitGraphRowVM>();
            BranchName = "";

            SetupGitCommand = new RelayCommand(ExecuteSetupAction, true);
            SetupNewProjectCommand = new RelayCommand(param => MainWindowVM.RequestNewProject(), true);
            SetupOpenProjectCommand = new RelayCommand(param => MainWindowVM.RequestOpenProject(), true);
            CommitCommand = new RelayCommand(ExecuteCommit, true);
            GitActionCommand = new RelayCommand(param => ExecutePrimaryAction(), true);
            PullCommand = new RelayCommand(param => ExecutePull(), true);
            PushCommand = new RelayCommand(param => ExecutePush(), true);
            ReloadCommand = new RelayCommand(param => ExecuteReload(), true);
            SelectBranchCommand = new RelayCommand(param => IsBranchPopupOpen = !IsBranchPopupOpen, true);
            SelectBranchItemCommand = new RelayCommand(SelectBranch, true);
            OpenCreateBranchPopupCommand = new RelayCommand(param => OpenCreateBranchPopup(), true);
            CreateBranchCommand = new RelayCommand(param => CreateBranch(), true);
            CancelCreateBranchCommand = new RelayCommand(param => CancelCreateBranch(), true);
            OpenMergePopupCommand = new RelayCommand(param => OpenMergePopup(), true);
            CancelMergeCommand = new RelayCommand(param => CancelMerge(), true);
            SoftMergeCommand = new RelayCommand(param => ExecuteSoftMerge(), true);
            MergeUseSourceCommand = new RelayCommand(param => ExecuteMergeTakingSource(), true);
            MergeUseTargetCommand = new RelayCommand(param => ExecuteMergeTakingTarget(), true);
            CancelConflictMergeCommand = new RelayCommand(param => CancelConflictMerge(), true);
            OpenRemotePopupCommand = new RelayCommand(param => OpenRemotePopup(), true);
            RemoteCheckCommand = new RelayCommand(param => ShowRemoteCredentials(), true);
            RemoteConnectCommand = new RelayCommand(param => ConnectRemote(), true);
            RemoteDisconnectCommand = new RelayCommand(param => DisconnectRemote(), true);
            RemoteCancelCommand = new RelayCommand(param => CancelRemote(), true);
            ToggleCommitCommand = new RelayCommand(ToggleCommit, true);
            RevertCommitCommand = new RelayCommand(RevertCommit, true);
            DiffCommitCommand = new RelayCommand(OpenCommitDiff, true);
            OpenChangeFileCommand = new RelayCommand(OpenChangeFile, true);
            GenerateCommitMessageCommand = new RelayCommand(param => GenerateCommitMessage(), true);
            AiAppSettingsStore.SettingsChanged += AiAppSettingsStore_SettingsChanged;
            SavedWorkspaceFilesChanged += GitPanelVM_SavedWorkspaceFilesChanged;
            ProjectContextChanged += GitPanelVM_ProjectContextChanged;
            _savedFilesRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _savedFilesRefreshTimer.Tick += SavedFilesRefreshTimer_Tick;
            _projectRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            _projectRefreshTimer.Tick += ProjectRefreshTimer_Tick;

            RefreshRepositoryState();
        }

        internal static event EventHandler SavedWorkspaceFilesChanged;
        internal static event EventHandler ProjectContextChanged;

        internal static void NotifySavedWorkspaceFilesChanged()
        {
            var handler = SavedWorkspaceFilesChanged;
            if (handler != null)
                handler(null, EventArgs.Empty);
        }

        internal static void NotifyProjectContextChanged()
        {
            var handler = ProjectContextChanged;
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
                OnPropertyChanged("IsAiGenerateEnabled");
                OnGitActionStateChanged();
            }
        }

        public ObservableCollection<GitBranchItemVM> Branches { get; private set; }
        public ObservableCollection<GitCommitVM> Commits { get; private set; }
        public ICommand SetupGitCommand { get; private set; }
        public ICommand SetupNewProjectCommand { get; private set; }
        public ICommand SetupOpenProjectCommand { get; private set; }
        public ICommand CommitCommand { get; private set; }
        public ICommand GitActionCommand { get; private set; }
        public ICommand PullCommand { get; private set; }
        public ICommand PushCommand { get; private set; }
        public ICommand ReloadCommand { get; private set; }
        public ICommand SelectBranchCommand { get; private set; }
        public ICommand SelectBranchItemCommand { get; private set; }
        public ICommand OpenCreateBranchPopupCommand { get; private set; }
        public ICommand CreateBranchCommand { get; private set; }
        public ICommand CancelCreateBranchCommand { get; private set; }
        public ICommand OpenMergePopupCommand { get; private set; }
        public ICommand CancelMergeCommand { get; private set; }
        public ICommand SoftMergeCommand { get; private set; }
        public ICommand MergeUseSourceCommand { get; private set; }
        public ICommand MergeUseTargetCommand { get; private set; }
        public ICommand CancelConflictMergeCommand { get; private set; }
        public ICommand OpenRemotePopupCommand { get; private set; }
        public ICommand RemoteCheckCommand { get; private set; }
        public ICommand RemoteConnectCommand { get; private set; }
        public ICommand RemoteDisconnectCommand { get; private set; }
        public ICommand RemoteCancelCommand { get; private set; }
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

        public bool IsGenerating
        {
            get { return _isGenerating; }
            private set
            {
                if (_isGenerating == value)
                    return;

                _isGenerating = value;
                OnPropertyChanged("IsGenerating");
                OnPropertyChanged("AiGenerateIconVisibility");
                OnPropertyChanged("IsAiGenerateEnabled");
            }
        }

        public bool IsAiGenerateEnabled
        {
            get
            {
                return AiAppSettingsStore.Settings.AiEnabled
                    && !IsGenerating
                    && !_isGitOperationRunning
                    && _state == GitUiState.Ready
                    && !string.IsNullOrWhiteSpace(_repositoryRoot);
            }
        }

        public Visibility AiGenerateIconVisibility
        {
            get { return IsGenerating ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string AiGenerateToolTip
        {
            get { return GetText("GitGenerateCommitMessage", "Generate commit message"); }
        }

        public Visibility SetupVisibility
        {
            get { return _state == GitUiState.Ready ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility SetupMainButtonVisibility
        {
            get { return _state == GitUiState.ProjectMissing ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility SetupProjectButtonsVisibility
        {
            get { return _state == GitUiState.ProjectMissing ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility MainGitVisibility
        {
            get { return _state == GitUiState.Ready ? Visibility.Visible : Visibility.Collapsed; }
        }

        public bool IsSetupButtonEnabled
        {
            get { return _state != GitUiState.Ready && !_isGitOperationRunning; }
        }

        public string SetupButtonText
        {
            get
            {
                if (_state == GitUiState.GitMissing)
                    return GetText("GitInstallGit", "Install Git");
                if (_state == GitUiState.RepoMissing)
                    return GetText("GitInitRepoAndAdd", "Init repository");

                return GetText("GitOpenProject", "Open project");
            }
        }

        public string GitActionButtonText
        {
            get
            {
                switch (GetPrimaryAction())
                {
                    case GitPrimaryAction.Sync:
                        return GetText("GitSyncChanges", "Sync changes");
                    case GitPrimaryAction.Pull:
                        return GetText("GitPullChanges", "Pull changes");
                    case GitPrimaryAction.ConnectRemote:
                        return GetText("GitConnectRemote", "Connect remote");
                    default:
                        return GetText("GitCommit", "Commit");
                }
            }
        }

        public string GitActionButtonToolTip
        {
            get
            {
                switch (GetPrimaryAction())
                {
                    case GitPrimaryAction.Sync:
                        return GetText("tGitPush", "Upload history to cloud");
                    case GitPrimaryAction.Pull:
                        return GetText("tGitPull", "Download history from cloud");
                    case GitPrimaryAction.ConnectRemote:
                        return GetText("GitConnectRemote", "Connect remote");
                    default:
                        return GetText("tGitCommit", "Create backup");
                }
            }
        }

        public Visibility GitCommitActionIconVisibility
        {
            get { return GetPrimaryAction() == GitPrimaryAction.Commit ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility GitRemoteActionIconVisibility
        {
            get { return GetPrimaryAction() == GitPrimaryAction.ConnectRemote ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility GitSyncActionIconVisibility
        {
            get { return GetPrimaryAction() == GitPrimaryAction.Sync ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility GitPullActionIconVisibility
        {
            get { return GetPrimaryAction() == GitPrimaryAction.Pull ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility GitSyncCountersVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public int GitOutgoingCommitCount
        {
            get { return _remoteOutgoingCommitCount; }
        }

        public int GitIncomingCommitCount
        {
            get { return _remoteIncomingCommitCount; }
        }

        public bool IsGitActionButtonEnabled
        {
            get
            {
                if (_state != GitUiState.Ready)
                    return false;

                if (_isGitOperationRunning)
                    return false;

                switch (GetPrimaryAction())
                {
                    case GitPrimaryAction.ConnectRemote:
                        return Changes == null || Changes.Count == 0;
                    case GitPrimaryAction.Pull:
                        return _isRemoteConnected && _remoteIncomingCommitCount > 0 && (Changes == null || Changes.Count == 0);
                    case GitPrimaryAction.Sync:
                        return _isRemoteConnected && _remoteOutgoingCommitCount > 0 && (Changes == null || Changes.Count == 0);
                    default:
                        return Changes != null && Changes.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);
                }
            }
        }

        public Brush GitActionButtonBackground
        {
            get { return IsGitActionButtonEnabled ? CreateBrush("#0A86FF") : CreateBrush("#D6DAE4"); }
        }

        public Brush GitActionButtonForeground
        {
            get { return IsGitActionButtonEnabled ? Brushes.White : CreateBrush("#8F98AB"); }
        }

        public bool LocalToolsEnabled
        {
            get { return _state == GitUiState.Ready && !_isGitOperationRunning; }
        }

        public bool RemoteSyncToolsEnabled
        {
            get { return _state == GitUiState.Ready && _isRemoteConnected && !_isGitOperationRunning; }
        }

        public bool RemoteToolsEnabled
        {
            get { return LocalToolsEnabled; }
        }

        public Visibility RemoteConnectedVisibility
        {
            get { return _state == GitUiState.Ready && _isRemoteConnected ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility RemoteConnectVisibility
        {
            get { return _state == GitUiState.Ready && !_isRemoteConnected ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility RemoteDisconnectVisibility
        {
            get { return _state == GitUiState.Ready && _isRemoteConnected ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string RemoteStatusText
        {
            get
            {
                return _isRemoteConnected
                    ? "origin: " + _remoteDisplayUrl
                    : GetText("GitStatusRemoteMissing", "Remote is not connected.");
            }
        }

        public string RemotePopupStatusMessage
        {
            get { return _remotePopupStatusMessage; }
            set
            {
                _remotePopupStatusMessage = value;
                OnPropertyChanged("RemotePopupStatusMessage");
            }
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

        public bool IsCreateBranchPopupOpen
        {
            get { return _isCreateBranchPopupOpen; }
            set
            {
                _isCreateBranchPopupOpen = value;
                OnPropertyChanged("IsCreateBranchPopupOpen");
            }
        }

        public string NewBranchName
        {
            get { return _newBranchName; }
            set
            {
                _newBranchName = value;
                OnPropertyChanged("NewBranchName");
                OnPropertyChanged("IsCreateBranchEnabled");
            }
        }

        public bool IsCreateBranchEnabled
        {
            get { return _state == GitUiState.Ready && !_isGitOperationRunning && !string.IsNullOrWhiteSpace(NewBranchName); }
        }

        public bool IsMergePopupOpen
        {
            get { return _isMergePopupOpen; }
            set
            {
                _isMergePopupOpen = value;
                OnPropertyChanged("IsMergePopupOpen");
            }
        }

        public bool IsMergeConflictPopupOpen
        {
            get { return _isMergeConflictPopupOpen; }
            set
            {
                _isMergeConflictPopupOpen = value;
                OnPropertyChanged("IsMergeConflictPopupOpen");
            }
        }

        public string MergeSourceBranch
        {
            get { return _mergeSourceBranch; }
            set
            {
                _mergeSourceBranch = value;
                OnPropertyChanged("MergeSourceBranch");
                OnPropertyChanged("IsMergeActionEnabled");
                OnPropertyChanged("MergeSourceActionText");
                OnPropertyChanged("MergeTargetActionText");
            }
        }

        public string MergeTargetBranch
        {
            get { return _mergeTargetBranch; }
            set
            {
                _mergeTargetBranch = value;
                OnPropertyChanged("MergeTargetBranch");
                OnPropertyChanged("IsMergeActionEnabled");
                OnPropertyChanged("MergeSourceActionText");
                OnPropertyChanged("MergeTargetActionText");
            }
        }

        public string MergeConflictMessage
        {
            get { return _mergeConflictMessage; }
            set
            {
                _mergeConflictMessage = value;
                OnPropertyChanged("MergeConflictMessage");
            }
        }

        public bool IsMergeActionEnabled
        {
            get
            {
                return _state == GitUiState.Ready
                    && !_isGitOperationRunning
                    && !string.IsNullOrWhiteSpace(MergeSourceBranch)
                    && !string.IsNullOrWhiteSpace(MergeTargetBranch)
                    && !string.Equals(MergeSourceBranch, MergeTargetBranch, StringComparison.Ordinal);
            }
        }

        public string MergeSourceActionText
        {
            get { return string.Format(GetText("GitMergeRebaseFrom", "Rebase from {0}"), MergeSourceBranch ?? ""); }
        }

        public string MergeTargetActionText
        {
            get { return string.Format(GetText("GitMergeRebaseFrom", "Rebase from {0}"), MergeTargetBranch ?? ""); }
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
            get { return Visibility.Collapsed; }
        }

        public Visibility RemoteConnectSubmitVisibility
        {
            get { return _isRemoteCredentialsVisible ? Visibility.Visible : Visibility.Collapsed; }
        }

        public bool IsRemoteConnectSubmitEnabled
        {
            get
            {
                if (_state != GitUiState.Ready || _isGitOperationRunning || !_isRemoteCredentialsVisible)
                    return false;

                if (string.IsNullOrWhiteSpace(RemoteUrl) || string.IsNullOrWhiteSpace(RemoteLogin))
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
                OnPropertyChanged("IsRemoteConnectSubmitEnabled");
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
                if (!string.Equals(_commitMessage, value, StringComparison.Ordinal))
                    _commitMessageVersion++;
                _commitMessage = value;
                OnPropertyChanged("CommitMessage");
                OnGitActionStateChanged();
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
            RefreshRepositoryState(null, false);
        }

        private async void RefreshRepositoryState(string statusOverride, bool showBusyStatus)
        {
            if (_isGitOperationRunning)
            {
                _deferredRepositoryRefresh = true;
                return;
            }

            var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
            var refreshVersion = ++_repositoryRefreshVersion;
            if (!BeginGitOperation(showBusyStatus ? GetText("GitStatusReload", "Local Git data refreshed.") : null))
            {
                _deferredRepositoryRefresh = true;
                return;
            }

            try
            {
                var refresh = await Task.Run(() => RunGitService(() => BuildRepositoryRefreshResult(currentProjectRoot)));
                if (refreshVersion != _repositoryRefreshVersion)
                    return;

                ApplyRepositoryRefreshResult(refresh, statusOverride);
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                EndGitOperation();
            }
        }

        private RepositoryRefreshResult BuildRepositoryRefreshResult(string currentProjectRoot)
        {
            if (string.IsNullOrWhiteSpace(currentProjectRoot))
            {
                return new RepositoryRefreshResult
                {
                    State = GitUiState.ProjectMissing,
                    WorkingDirectory = "",
                    RepositoryRoot = "",
                    BranchName = "",
                    WatchedRoot = "",
                    StatusMessage = GetText("GitStatusOpenProject", "Create or open a project to use Git.")
                };
            }

            if (!_git.IsGitInstalled())
            {
                return new RepositoryRefreshResult
                {
                    State = GitUiState.GitMissing,
                    WorkingDirectory = currentProjectRoot,
                    RepositoryRoot = "",
                    BranchName = "",
                    WatchedRoot = currentProjectRoot,
                    StatusMessage = GetText("GitStatusGitMissing", "Git is not installed. Install Git for Windows, then refresh.")
                };
            }

            var workingDirectory = currentProjectRoot;
            var repositoryRoot = GitRepositoryService.FindRepositoryRoot(workingDirectory);
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                return new RepositoryRefreshResult
                {
                    State = GitUiState.RepoMissing,
                    WorkingDirectory = workingDirectory,
                    RepositoryRoot = "",
                    BranchName = new DirectoryInfo(workingDirectory).Name,
                    WatchedRoot = workingDirectory,
                    StatusMessage = GetText("GitStatusRepoMissing", "Git repository is not initialized for this project.")
                };
            }

            var snapshot = _git.GetSnapshot(repositoryRoot);
            return new RepositoryRefreshResult
            {
                State = GitUiState.Ready,
                WorkingDirectory = workingDirectory,
                RepositoryRoot = repositoryRoot,
                BranchName = string.IsNullOrWhiteSpace(snapshot.BranchName)
                    ? GetText("GitNoCommitsYet", "No commits yet")
                    : snapshot.BranchName,
                WatchedRoot = repositoryRoot,
                Snapshot = snapshot,
                StatusMessage = snapshot.Changes.Count > 0
                    ? string.Format(GetText("GitStatusChangesFound", "{0} local changes."), snapshot.Changes.Count)
                    : GetText("GitStatusClean", "Working tree is clean.")
            };
        }

        private bool ApplyRepositoryRefreshResult(RepositoryRefreshResult refresh, string statusOverride)
        {
            if (refresh == null)
                return false;

            var currentProjectRoot = MainWindowVM.CurrentProjectRoot ?? "";
            if (!AreSameDirectory(refresh.WorkingDirectory, currentProjectRoot))
            {
                _deferredRepositoryRefresh = true;
                return false;
            }

            _workingDirectory = refresh.WorkingDirectory ?? "";
            if (!AreSameDirectory(_repositoryRoot, refresh.RepositoryRoot))
                CancelCommitMessageGeneration();
            _repositoryRoot = refresh.RepositoryRoot ?? "";
            BranchName = refresh.BranchName ?? "";
            UpdateProjectWatcher(refresh.WatchedRoot ?? "");

            if (refresh.State != GitUiState.Ready || refresh.Snapshot == null)
            {
                ClearGitData();
                ApplyRemoteInfo(null);
                ApplyState(refresh.State);
                StatusMessage = string.IsNullOrWhiteSpace(statusOverride) ? refresh.StatusMessage : statusOverride;
                return true;
            }

            var snapshot = refresh.Snapshot;
            Branches.Clear();
            foreach (var branch in snapshot.Branches)
                Branches.Add(new GitBranchItemVM(branch.Name, branch.IsActive));

            ApplyChanges(snapshot.Changes);
            _remoteOutgoingCommitCount = snapshot.OutgoingCommitCount;
            _remoteIncomingCommitCount = snapshot.IncomingCommitCount;
            ApplyRemoteInfo(snapshot.Remote);

            Commits.Clear();
            foreach (var commit in snapshot.Commits)
                Commits.Add(commit);

            RebuildGraphRows();
            ApplyState(GitUiState.Ready);
            StatusMessage = string.IsNullOrWhiteSpace(statusOverride) ? refresh.StatusMessage : statusOverride;
            return true;
        }

        private void RefreshWorkingTreeState()
        {
            RefreshWorkingTreeState(null);
        }

        private async void RefreshWorkingTreeState(string statusOverride)
        {
            if (_isGitOperationRunning)
            {
                _deferredWorkingTreeRefresh = true;
                return;
            }

            if (_state != GitUiState.Ready || string.IsNullOrWhiteSpace(_repositoryRoot))
            {
                RefreshRepositoryState(statusOverride, false);
                return;
            }

            var workingDirectory = _workingDirectory;
            var repositoryRoot = _repositoryRoot;
            var refreshVersion = ++_workingTreeRefreshVersion;
            var refreshRepositoryAfter = false;
            if (!BeginGitOperation(null))
            {
                _deferredWorkingTreeRefresh = true;
                return;
            }

            try
            {
                var refresh = await Task.Run(() => RunGitService(() => BuildWorkingTreeRefreshResult(workingDirectory, repositoryRoot)));
                if (refreshVersion != _workingTreeRefreshVersion)
                    return;

                if (refresh.NeedsRepositoryRefresh)
                {
                    refreshRepositoryAfter = true;
                    return;
                }

                if (!IsCurrentRepositoryContext(workingDirectory, repositoryRoot))
                {
                    _deferredRepositoryRefresh = true;
                    return;
                }

                ApplyChanges(refresh.Changes);
                OnGitActionStateChanged();
                StatusMessage = string.IsNullOrWhiteSpace(statusOverride) ? refresh.StatusMessage : statusOverride;
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                if (_isGitOperationRunning)
                    EndGitOperation();

                if (refreshRepositoryAfter)
                    RefreshRepositoryState(statusOverride, false);
            }
        }

        private WorkingTreeRefreshResult BuildWorkingTreeRefreshResult(string workingDirectory, string repositoryRoot)
        {
            var currentRoot = GitRepositoryService.FindRepositoryRoot(workingDirectory);
            if (!string.Equals(currentRoot, repositoryRoot, StringComparison.OrdinalIgnoreCase))
                return new WorkingTreeRefreshResult { NeedsRepositoryRefresh = true };

            var changes = _git.GetWorkingTreeChanges(repositoryRoot);
            return new WorkingTreeRefreshResult
            {
                Changes = changes,
                StatusMessage = changes.Count > 0
                    ? string.Format(GetText("GitStatusChangesFound", "{0} local changes."), changes.Count)
                    : GetText("GitStatusClean", "Working tree is clean.")
            };
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

            if (_state == GitUiState.ProjectMissing)
            {
                RefreshRepositoryState();
                if (_state == GitUiState.ProjectMissing)
                    SetLocalizedStatus("GitStatusOpenProject", "Create or open a project to use Git.");
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

        private async void ExecuteCommit(object parameter)
        {
            if (_state != GitUiState.Ready)
                return;

            if (string.IsNullOrWhiteSpace(CommitMessage))
            {
                SetLocalizedStatus("GitStatusCommitMessageRequired", "Commit message is required.");
                return;
            }

            var repositoryRoot = _repositoryRoot;
            var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
            var message = CommitMessage.Trim();
            if (!BeginGitOperation(null))
                return;

            try
            {
                var refresh = await Task.Run(() => RunGitService(() =>
                    RunActionWithRepositoryRefresh(() => _git.CommitAll(repositoryRoot, message), currentProjectRoot)));

                var applied = ApplyRepositoryRefreshResult(
                    refresh.RefreshResult,
                    refresh.ActionResult.Success
                        ? GetText("GitStatusCommitDone", "Commit saved.")
                        : refresh.ActionResult.Message);

                if (applied && refresh.ActionResult.Success)
                    CommitMessage = "";
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                EndGitOperation();
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
            GraphRows = GitGraphLayoutService.BuildRows(Commits, BranchName);
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

        private async void SelectBranch(object parameter)
        {
            var branch = parameter as GitBranchItemVM;
            if (branch == null || _state != GitUiState.Ready)
                return;

            if (branch.IsActive)
            {
                IsBranchPopupOpen = false;
                return;
            }

            if (MainWindowVM.HasUnsavedProjectEditorChanges(_repositoryRoot))
            {
                IsBranchPopupOpen = false;
                SetLocalizedStatus("GitStatusUnsavedOpenFiles", "Save open project files before this Git action.");
                return;
            }

            var repositoryRoot = _repositoryRoot;
            var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
            var branchName = branch.Name;
            IsBranchPopupOpen = false;
            if (!BeginGitOperation(null))
                return;

            try
            {
                var refresh = await Task.Run(() => RunGitService(() =>
                    RunActionWithRepositoryRefresh(() => _git.SwitchBranch(repositoryRoot, branchName), currentProjectRoot)));

                var applied = ApplyRepositoryRefreshResult(
                    refresh.RefreshResult,
                    refresh.ActionResult.Success
                        ? GetText("GitStatusBranchSelected", "Branch selected.")
                        : refresh.ActionResult.Message);

                if (applied && refresh.ActionResult.Success)
                    MainWindowVM.ReloadOpenProjectFilesFromDisk(repositoryRoot);
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                EndGitOperation();
            }
        }

        private void OpenCreateBranchPopup()
        {
            if (_state != GitUiState.Ready)
                return;

            NewBranchName = "";
            IsBranchPopupOpen = false;
            IsCreateBranchPopupOpen = true;
        }

        private void CancelCreateBranch()
        {
            NewBranchName = "";
            IsCreateBranchPopupOpen = false;
        }

        private async void CreateBranch()
        {
            if (_state != GitUiState.Ready)
                return;

            if (string.IsNullOrWhiteSpace(NewBranchName))
            {
                SetLocalizedStatus("GitStatusBranchNameRequired", "Branch name is required.");
                return;
            }

            var repositoryRoot = _repositoryRoot;
            var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
            var branchName = NewBranchName.Trim();
            IsCreateBranchPopupOpen = false;
            if (!BeginGitOperation(null))
                return;

            try
            {
                var refresh = await Task.Run(() => RunGitService(() =>
                    RunActionWithRepositoryRefresh(() => _git.CreateBranch(repositoryRoot, branchName), currentProjectRoot)));

                ApplyRepositoryRefreshResult(
                    refresh.RefreshResult,
                    refresh.ActionResult.Success
                        ? GetText("GitStatusBranchCreated", "Branch created.")
                        : refresh.ActionResult.Message);
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                EndGitOperation();
            }
        }

        private void ExecutePrimaryAction()
        {
            switch (GetPrimaryAction())
            {
                case GitPrimaryAction.ConnectRemote:
                    OpenRemotePopup();
                    break;
                case GitPrimaryAction.Pull:
                    ExecutePull();
                    break;
                case GitPrimaryAction.Sync:
                    ExecutePush();
                    break;
                default:
                    ExecuteCommit(null);
                    break;
            }
        }

        private void OpenMergePopup()
        {
            if (_state != GitUiState.Ready)
                return;

            if (Branches.Count < 2)
            {
                SetLocalizedStatus("GitStatusMergeNeedBranches", "Create another branch before merge.");
                return;
            }

            var currentBranch = GetActiveBranchName();
            MergeSourceBranch = currentBranch;
            MergeTargetBranch = PickDefaultMergeTarget(currentBranch);
            MergeConflictMessage = "";
            IsBranchPopupOpen = false;
            IsCreateBranchPopupOpen = false;
            IsRemotePopupOpen = false;
            IsMergeConflictPopupOpen = false;
            IsMergePopupOpen = true;
        }

        private void CancelMerge()
        {
            IsMergePopupOpen = false;
            MergeConflictMessage = "";
        }

        private void ExecuteSoftMerge()
        {
            ExecuteMerge(false, false);
        }

        private void ExecuteMergeTakingSource()
        {
            if (IsMergeConflictPopupOpen)
                ResolveCurrentMergeConflicts(true);
            else
                ExecuteMerge(true, true);
        }

        private void ExecuteMergeTakingTarget()
        {
            if (IsMergeConflictPopupOpen)
                ResolveCurrentMergeConflicts(false);
            else
                ExecuteMerge(true, false);
        }

        private void ExecuteMerge(bool autoResolveConflicts, bool takeSourceBranch)
        {
            if (_state != GitUiState.Ready)
                return;

            if (!IsMergeActionEnabled)
            {
                SetLocalizedStatus("GitStatusMergeBranchesRequired", "Select two different branches to merge.");
                return;
            }

            if (MainWindowVM.HasUnsavedProjectEditorChanges(_repositoryRoot))
            {
                SetLocalizedStatus("GitStatusUnsavedOpenFiles", "Save open project files before this Git action.");
                return;
            }

            var result = _git.SoftMerge(_repositoryRoot, MergeSourceBranch, MergeTargetBranch);
            if (result.Success)
            {
                IsMergePopupOpen = false;
                MainWindowVM.ReloadOpenProjectFilesFromDisk(_repositoryRoot);
                RefreshRepositoryState();
                SetLocalizedStatus("GitStatusMergePrepared", "Merge prepared. Review and commit.");
                return;
            }

            if (result.HasConflicts)
            {
                RefreshWorkingTreeState();
                if (autoResolveConflicts)
                {
                    ResolveCurrentMergeConflicts(takeSourceBranch);
                    return;
                }

                MainWindowVM.ReloadOpenProjectFilesFromDisk(_repositoryRoot);
                IsMergePopupOpen = false;
                MergeConflictMessage = BuildMergeConflictMessage(result);
                IsMergeConflictPopupOpen = true;
                SetLocalizedStatus("GitStatusMergeConflict", "Merge has conflicts.");
                return;
            }

            RefreshRepositoryState();
            StatusMessage = result.Message;
        }

        private void ResolveCurrentMergeConflicts(bool takeSourceBranch)
        {
            if (_state != GitUiState.Ready)
                return;

            var result = _git.ResolveMergeConflicts(_repositoryRoot, takeSourceBranch);
            IsMergePopupOpen = false;
            IsMergeConflictPopupOpen = false;
            if (result.Success)
            {
                MainWindowVM.ReloadOpenProjectFilesFromDisk(_repositoryRoot);
                RefreshRepositoryState();
                StatusMessage = string.Format(
                    GetText("GitStatusMergeResolvedFrom", "Merge conflicts resolved from {0}. Review and commit."),
                    takeSourceBranch ? MergeSourceBranch : MergeTargetBranch);
            }
            else
            {
                RefreshWorkingTreeState();
                StatusMessage = result.Message;
            }
        }

        private void CancelConflictMerge()
        {
            var result = _git.AbortMerge(_repositoryRoot);
            IsMergeConflictPopupOpen = false;
            MergeConflictMessage = "";
            MainWindowVM.ReloadOpenProjectFilesFromDisk(_repositoryRoot);
            RefreshRepositoryState();
            StatusMessage = result.Success
                ? GetText("GitStatusMergeAborted", "Merge cancelled.")
                : result.Message;
        }

        private string GetActiveBranchName()
        {
            var branch = Branches.FirstOrDefault(item => item.IsActive);
            if (branch != null && !string.IsNullOrWhiteSpace(branch.Name))
                return branch.Name;

            return BranchName;
        }

        private string PickDefaultMergeTarget(string sourceBranch)
        {
            if (!string.Equals(sourceBranch, "master", StringComparison.Ordinal)
                && Branches.Any(item => string.Equals(item.Name, "master", StringComparison.Ordinal)))
                return "master";

            if (!string.Equals(sourceBranch, "main", StringComparison.Ordinal)
                && Branches.Any(item => string.Equals(item.Name, "main", StringComparison.Ordinal)))
                return "main";

            var otherBranch = Branches.FirstOrDefault(item => !string.Equals(item.Name, sourceBranch, StringComparison.Ordinal));
            return otherBranch == null ? "" : otherBranch.Name;
        }

        private string BuildMergeConflictMessage(GitMergeResult result)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine(GetText("GitMergeConflictIntro", "Merge stopped on conflicts. Choose how to continue."));
            builder.AppendLine();

            foreach (var conflict in result.Conflicts)
                builder.AppendLine(conflict.DisplayText);

            return builder.ToString().TrimEnd();
        }

        private GitPrimaryAction GetPrimaryAction()
        {
            if (_state != GitUiState.Ready)
                return GitPrimaryAction.Commit;

            if (Changes != null && Changes.Count > 0)
                return GitPrimaryAction.Commit;

            if (!_isRemoteConnected)
                return GitPrimaryAction.ConnectRemote;

            if (_remoteIncomingCommitCount > 0)
                return GitPrimaryAction.Pull;

            if (_remoteOutgoingCommitCount > 0)
                return GitPrimaryAction.Sync;

            return GitPrimaryAction.Commit;
        }

        private void OnGitActionStateChanged()
        {
            OnPropertyChanged("GitActionButtonText");
            OnPropertyChanged("GitActionButtonToolTip");
            OnPropertyChanged("GitCommitActionIconVisibility");
            OnPropertyChanged("GitRemoteActionIconVisibility");
            OnPropertyChanged("GitSyncActionIconVisibility");
            OnPropertyChanged("GitPullActionIconVisibility");
            OnPropertyChanged("GitSyncCountersVisibility");
            OnPropertyChanged("GitOutgoingCommitCount");
            OnPropertyChanged("GitIncomingCommitCount");
            OnPropertyChanged("IsGitActionButtonEnabled");
            OnPropertyChanged("GitActionButtonBackground");
            OnPropertyChanged("GitActionButtonForeground");
        }

        private bool BeginGitOperation(string statusMessage)
        {
            if (_isGitOperationRunning)
                return false;

            CancelCommitMessageGeneration();
            _isGitOperationRunning = true;
            if (!string.IsNullOrWhiteSpace(statusMessage))
                StatusMessage = statusMessage;

            OnGitOperationStateChanged();
            return true;
        }

        private void EndGitOperation()
        {
            _isGitOperationRunning = false;
            OnGitOperationStateChanged();

            if (_deferredRepositoryRefresh)
            {
                _deferredRepositoryRefresh = false;
                _deferredWorkingTreeRefresh = false;
                RefreshRepositoryState();
                return;
            }

            if (_deferredWorkingTreeRefresh)
            {
                _deferredWorkingTreeRefresh = false;
                RefreshWorkingTreeState();
            }
        }

        private void OnGitOperationStateChanged()
        {
            OnPropertyChanged("IsSetupButtonEnabled");
            OnPropertyChanged("LocalToolsEnabled");
            OnPropertyChanged("RemoteSyncToolsEnabled");
            OnPropertyChanged("RemoteToolsEnabled");
            OnPropertyChanged("IsCreateBranchEnabled");
            OnPropertyChanged("IsMergeActionEnabled");
            OnPropertyChanged("IsRemoteConnectSubmitEnabled");
            OnPropertyChanged("IsAiGenerateEnabled");
            OnGitActionStateChanged();
        }

        private T RunGitService<T>(Func<T> action)
        {
            lock (_gitOperationLock)
            {
                return action();
            }
        }

        private GitActionRefreshResult RunActionWithRepositoryRefresh(Func<GitActionResult> action, string currentProjectRoot)
        {
            var result = action();
            return new GitActionRefreshResult
            {
                ActionResult = result,
                RefreshResult = BuildRepositoryRefreshResult(currentProjectRoot)
            };
        }

        private bool IsCurrentRepositoryContext(string workingDirectory, string repositoryRoot)
        {
            return AreSameDirectory(workingDirectory, MainWindowVM.CurrentProjectRoot)
                && AreSameDirectory(repositoryRoot, _repositoryRoot);
        }

        private async void ExecuteReload()
        {
            if (_state != GitUiState.Ready)
            {
                RefreshRepositoryState();
                return;
            }

            var hadRemote = _isRemoteConnected;
            var repositoryRoot = _repositoryRoot;
            var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
            if (!BeginGitOperation(null))
                return;

            try
            {
                var refresh = await Task.Run(() => RunGitService(() =>
                {
                    var result = hadRemote ? _git.RefreshRemote(repositoryRoot) : GitActionResult.Ok("");
                    return new GitActionRefreshResult
                    {
                        ActionResult = result,
                        RefreshResult = BuildRepositoryRefreshResult(currentProjectRoot)
                    };
                }));

                ApplyRepositoryRefreshResult(
                    refresh.RefreshResult,
                    refresh.ActionResult.Success
                        ? (hadRemote ? GetText("GitStatusRemoteFetchDone", "Remote data refreshed.") : GetText("GitStatusReload", "Local Git data refreshed."))
                        : refresh.ActionResult.Message);
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                EndGitOperation();
            }
        }

        private async void ExecutePush()
        {
            if (_state != GitUiState.Ready)
                return;

            if (!_isRemoteConnected)
            {
                SetLocalizedStatus("GitStatusRemoteMissing", "Remote is not connected.");
                return;
            }

            var repositoryRoot = _repositoryRoot;
            var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
            if (!BeginGitOperation(null))
                return;

            try
            {
                var refresh = await Task.Run(() => RunGitService(() =>
                    RunActionWithRepositoryRefresh(() => _git.PushAllBranches(repositoryRoot), currentProjectRoot)));

                ApplyRepositoryRefreshResult(
                    refresh.RefreshResult,
                    refresh.ActionResult.Success
                        ? GetText("GitStatusRemotePushDone", "Remote push completed.")
                        : refresh.ActionResult.Message);
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                EndGitOperation();
            }
        }

        private async void ExecutePull()
        {
            if (_state != GitUiState.Ready)
                return;

            if (!_isRemoteConnected)
            {
                SetLocalizedStatus("GitStatusRemoteMissing", "Remote is not connected.");
                return;
            }

            if (MainWindowVM.HasUnsavedProjectEditorChanges(_repositoryRoot))
            {
                SetLocalizedStatus("GitStatusUnsavedOpenFiles", "Save open project files before this Git action.");
                return;
            }

            var repositoryRoot = _repositoryRoot;
            var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
            if (!BeginGitOperation(null))
                return;

            try
            {
                var refresh = await Task.Run(() => RunGitService(() =>
                    RunActionWithRepositoryRefresh(() => _git.PullAllBranches(repositoryRoot), currentProjectRoot)));

                var applied = ApplyRepositoryRefreshResult(
                    refresh.RefreshResult,
                    refresh.ActionResult.Success
                        ? GetText("GitStatusRemotePullDone", "Remote pull completed.")
                        : refresh.ActionResult.Message);

                if (applied && refresh.ActionResult.Success)
                    MainWindowVM.ReloadOpenProjectFilesFromDisk(repositoryRoot);
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                EndGitOperation();
            }
        }

        private void OpenRemotePopup()
        {
            if (_state != GitUiState.Ready)
                return;

            IsBranchPopupOpen = false;
            IsMergePopupOpen = false;
            IsMergeConflictPopupOpen = false;
            _isRemoteCredentialsVisible = true;
            UseTokenAuth = false;
            RemotePopupStatusMessage = _isRemoteConnected ? RemoteStatusText : "";
            OnPropertyChanged("RemoteCredentialsVisibility");
            OnPropertyChanged("RemoteCheckVisibility");
            OnPropertyChanged("RemoteConnectSubmitVisibility");
            OnPropertyChanged("IsRemoteConnectSubmitEnabled");
            IsRemotePopupOpen = true;
        }

        private void ShowRemoteCredentials()
        {
            _isRemoteCredentialsVisible = true;
            RemotePopupStatusMessage = "";
            OnPropertyChanged("RemoteCredentialsVisibility");
            OnPropertyChanged("RemoteCheckVisibility");
            OnPropertyChanged("RemoteConnectSubmitVisibility");
            OnPropertyChanged("IsRemoteConnectSubmitEnabled");
        }

        private async void ConnectRemote()
        {
            if (_state != GitUiState.Ready)
                return;

            var secret = UseTokenAuth ? RemoteToken : RemotePassword;
            var mode = UseTokenAuth ? GitRemoteAuthMode.LoginToken : GitRemoteAuthMode.LoginPassword;
            var repositoryRoot = _repositoryRoot;
            var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
            var remoteUrl = RemoteUrl;
            var remoteLogin = RemoteLogin;
            RemotePopupStatusMessage = "";
            if (!BeginGitOperation(null))
                return;

            try
            {
                var refresh = await Task.Run(() => RunGitService(() =>
                    RunActionWithRepositoryRefresh(() => _git.ConnectRemote(repositoryRoot, remoteUrl, remoteLogin, secret, mode), currentProjectRoot)));

                var applied = ApplyRepositoryRefreshResult(
                    refresh.RefreshResult,
                    refresh.ActionResult.Success
                        ? GetText("GitStatusRemoteConnected", "Remote connected.")
                        : refresh.ActionResult.Message);

                if (!applied)
                    return;

                if (refresh.ActionResult.Success)
                {
                    RemotePassword = "";
                    RemoteToken = "";
                    IsRemotePopupOpen = false;
                }
                else
                {
                    RemotePopupStatusMessage = refresh.ActionResult.Message;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                RemotePopupStatusMessage = ex.Message;
            }
            finally
            {
                EndGitOperation();
            }
        }

        private async void DisconnectRemote()
        {
            if (_state != GitUiState.Ready)
                return;

            var repositoryRoot = _repositoryRoot;
            var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
            RemotePassword = "";
            RemoteToken = "";
            if (!BeginGitOperation(null))
                return;

            try
            {
                var refresh = await Task.Run(() => RunGitService(() =>
                    RunActionWithRepositoryRefresh(() => _git.DisconnectRemote(repositoryRoot), currentProjectRoot)));

                var applied = ApplyRepositoryRefreshResult(
                    refresh.RefreshResult,
                    refresh.ActionResult.Success
                        ? GetText("GitStatusRemoteDisconnected", "Remote disconnected.")
                        : refresh.ActionResult.Message);

                if (!applied)
                    return;

                if (refresh.ActionResult.Success)
                    IsRemotePopupOpen = false;
                else
                    RemotePopupStatusMessage = refresh.ActionResult.Message;
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                RemotePopupStatusMessage = ex.Message;
            }
            finally
            {
                EndGitOperation();
            }
        }

        private void CancelRemote()
        {
            IsRemotePopupOpen = false;
            RemotePopupStatusMessage = "";
        }

        private void ApplyRemoteInfo(GitRemoteInfo remote)
        {
            _isRemoteConnected = remote != null && remote.IsConnected;
            _remoteDisplayUrl = _isRemoteConnected ? remote.DisplayUrl : "";
            if (!_isRemoteConnected)
            {
                _remoteOutgoingCommitCount = 0;
                _remoteIncomingCommitCount = 0;
            }
            if (_isRemoteConnected)
            {
                _remoteUrl = remote.Url;
                OnPropertyChanged("RemoteUrl");
            }

            OnRemoteStateChanged();
            OnGitActionStateChanged();
        }

        private void OnRemoteStateChanged()
        {
            OnPropertyChanged("GitRemoteActionIconVisibility");
            OnPropertyChanged("LocalToolsEnabled");
            OnPropertyChanged("RemoteSyncToolsEnabled");
            OnPropertyChanged("RemoteToolsEnabled");
            OnPropertyChanged("RemoteConnectedVisibility");
            OnPropertyChanged("RemoteConnectVisibility");
            OnPropertyChanged("RemoteDisconnectVisibility");
            OnPropertyChanged("RemoteStatusText");
            OnPropertyChanged("RemoteCredentialsVisibility");
            OnPropertyChanged("RemoteCheckVisibility");
            OnPropertyChanged("RemoteConnectSubmitVisibility");
            OnPropertyChanged("IsRemoteConnectSubmitEnabled");
            OnGitActionStateChanged();
        }

        private void ApplyState(GitUiState state)
        {
            _state = state;

            OnPropertyChanged("SetupVisibility");
            OnPropertyChanged("SetupMainButtonVisibility");
            OnPropertyChanged("SetupProjectButtonsVisibility");
            OnPropertyChanged("MainGitVisibility");
            OnPropertyChanged("IsSetupButtonEnabled");
            OnPropertyChanged("SetupButtonText");
            OnPropertyChanged("IsCreateBranchEnabled");
            OnGitActionStateChanged();
            OnPropertyChanged("LocalToolsEnabled");
            OnPropertyChanged("RemoteSyncToolsEnabled");
            OnPropertyChanged("RemoteToolsEnabled");
            OnPropertyChanged("RemoteConnectedVisibility");
            OnPropertyChanged("RemoteConnectVisibility");
            OnPropertyChanged("RemoteDisconnectVisibility");
            OnPropertyChanged("RemoteStatusText");
            OnPropertyChanged("RemoteCredentialsVisibility");
            OnPropertyChanged("RemoteCheckVisibility");
            OnPropertyChanged("RemoteConnectSubmitVisibility");
            OnPropertyChanged("IsRemoteConnectSubmitEnabled");
            OnPropertyChanged("IsMergeActionEnabled");
            OnPropertyChanged("IsAiGenerateEnabled");
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

        private async void OpenCommitDiff(object parameter)
        {
            var commit = parameter as GitCommitVM;
            if (commit == null)
            {
                SetLocalizedStatus("GitDiffNoData", "No diff data for this commit.");
                return;
            }

            if (commit.DiffDocument == null)
            {
                var repositoryRoot = _repositoryRoot;
                var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
                if (!BeginGitOperation(null))
                    return;

                try
                {
                    var diffDocument = await Task.Run(() => RunGitService(() => _git.GetCommitDiff(repositoryRoot, commit)));
                    if (!IsCurrentRepositoryContext(currentProjectRoot, repositoryRoot))
                    {
                        _deferredRepositoryRefresh = true;
                        return;
                    }

                    commit.DiffDocument = diffDocument;
                }
                catch (Exception ex)
                {
                    StatusMessage = ex.Message;
                    return;
                }
                finally
                {
                    EndGitOperation();
                }
            }

            if (commit.DiffDocument == null || commit.DiffDocument.Files.Count == 0)
            {
                SetLocalizedStatus("GitDiffNoData", "No diff data for this commit.");
                return;
            }

            MainWindowVM.OpenGitDiffPreview(commit);
            SetCommitActionStatus(commit, "GitStatusDiffOpened", "Opened read-only diff:");
        }

        private async void RevertCommit(object parameter)
        {
            var commit = parameter as GitCommitVM;
            if (commit == null || _state != GitUiState.Ready)
                return;

            if (MainWindowVM.HasUnsavedProjectEditorChanges(_repositoryRoot))
            {
                SetLocalizedStatus("GitStatusUnsavedOpenFiles", "Save open project files before this Git action.");
                return;
            }

            if (!ConfirmRestoreToCommit(commit))
                return;

            var repositoryRoot = _repositoryRoot;
            var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
            var hash = commit.Hash;
            if (!BeginGitOperation(null))
                return;

            try
            {
                var refresh = await Task.Run(() => RunGitService(() =>
                    RunActionWithRepositoryRefresh(() => _git.RestoreToCommitSnapshot(repositoryRoot, hash), currentProjectRoot)));

                var applied = ApplyRepositoryRefreshResult(
                    refresh.RefreshResult,
                    refresh.ActionResult.Success
                        ? GetText("GitStatusRollbackPrepared", "Files restored to the selected version. Review and commit the rollback.")
                        : refresh.ActionResult.Message);

                if (applied && refresh.ActionResult.Success)
                    MainWindowVM.ReloadOpenProjectFilesFromDisk(repositoryRoot);

                if (applied && refresh.ActionResult.Success && string.IsNullOrWhiteSpace(CommitMessage))
                    CommitMessage = "Restore project to " + commit.ShortHash;
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                EndGitOperation();
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

        private async void GenerateCommitMessage()
        {
            if (!IsAiGenerateEnabled)
                return;

            if (MainWindowVM.HasUnsavedProjectEditorChanges(_repositoryRoot))
            {
                SetLocalizedStatus("GitStatusUnsavedOpenFiles", "Save open project files before this Git action.");
                return;
            }

            var repositoryRoot = _repositoryRoot;
            var currentProjectRoot = MainWindowVM.CurrentProjectRoot;
            var commitMessageVersion = _commitMessageVersion;
            var workingTreeVersion = _workingTreeRefreshVersion;
            var generationVersion = Interlocked.Increment(ref _commitMessageGenerationVersion);
            var cancellation = new CancellationTokenSource();
            Interlocked.Exchange(ref _commitMessageGenerationCancellation, cancellation);
            IsGenerating = true;

            try
            {
                SetLocalizedStatus("GitStatusAiCommitGenerating", "Generating commit message...");
                var patch = await Task.Run(
                    () => RunGitService(() => _git.GetWorkingTreePatchForAi(repositoryRoot, cancellation.Token)),
                    cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!IsCommitMessageGenerationContextCurrent(generationVersion, workingTreeVersion, currentProjectRoot, repositoryRoot))
                    return;

                if (string.IsNullOrWhiteSpace(patch))
                {
                    SetLocalizedStatus("GitStatusAiCommitNoChanges", "There are no text changes to describe.");
                    return;
                }

                var generatedMessage = await OpenAiTextGenerationService.Instance.GenerateCommitMessageAsync(
                    patch,
                    Configurations.Get.Language,
                    cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!IsCommitMessageGenerationContextCurrent(generationVersion, workingTreeVersion, currentProjectRoot, repositoryRoot))
                    return;

                if (commitMessageVersion != _commitMessageVersion)
                {
                    SetLocalizedStatus("GitStatusAiCommitTextChanged", "Commit message was edited; the generated result was not applied.");
                    return;
                }

                CommitMessage = generatedMessage;
                SetLocalizedStatus("GitStatusGenerateCommitMessage", "Commit message generated.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (AiConfigurationException)
            {
                if (IsCommitMessageGenerationContextCurrent(generationVersion, workingTreeVersion, currentProjectRoot, repositoryRoot))
                    SetLocalizedStatus("GitStatusAiNotConfigured", "Configure the AI provider URL, model and token first.");
            }
            catch (Exception ex)
            {
                if (IsCommitMessageGenerationContextCurrent(generationVersion, workingTreeVersion, currentProjectRoot, repositoryRoot))
                {
                    StatusMessage = string.Format(
                        GetText("GitStatusAiCommitFailed", "Could not generate a commit message: {0}"),
                        ex.Message);
                }
            }
            finally
            {
                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _commitMessageGenerationCancellation, null, cancellation),
                    cancellation))
                {
                    IsGenerating = false;
                }

                cancellation.Dispose();
            }
        }

        private bool IsCommitMessageGenerationContextCurrent(int generationVersion, int workingTreeVersion, string projectRoot, string repositoryRoot)
        {
            return generationVersion == Volatile.Read(ref _commitMessageGenerationVersion)
                && workingTreeVersion == _workingTreeRefreshVersion
                && IsCurrentRepositoryContext(projectRoot, repositoryRoot);
        }

        private void CancelCommitMessageGeneration()
        {
            var cancellation = Interlocked.CompareExchange(ref _commitMessageGenerationCancellation, null, null);
            if (cancellation == null)
                return;

            Interlocked.Increment(ref _commitMessageGenerationVersion);
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void AiAppSettingsStore_SettingsChanged(object sender, EventArgs e)
        {
            if (IsGenerating)
            {
                CancelCommitMessageGeneration();
                SetLocalizedStatus("GitStatusAiCommitCancelled", "Commit message generation cancelled.");
            }
            OnPropertyChanged("AiGenerateVisibility");
            OnPropertyChanged("IsAiGenerateEnabled");
            OnPropertyChanged("AiGenerateToolTip");
        }

        private void GitPanelVM_SavedWorkspaceFilesChanged(object sender, EventArgs e)
        {
            CancelCommitMessageGeneration();
            _savedFilesRefreshTimer.Stop();
            _savedFilesRefreshTimer.Start();
        }

        private void GitPanelVM_ProjectContextChanged(object sender, EventArgs e)
        {
            CancelCommitMessageGeneration();
            RefreshRepositoryState();
        }

        private void SavedFilesRefreshTimer_Tick(object sender, EventArgs e)
        {
            _savedFilesRefreshTimer.Stop();
            RefreshWorkingTreeState();
        }

        private void ProjectRefreshTimer_Tick(object sender, EventArgs e)
        {
            _projectRefreshTimer.Stop();
            RefreshWorkingTreeState();
        }

        private void ProjectWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (IsGitInternalPath(e.FullPath))
                return;

            CancelCommitMessageGeneration();

            var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
            if (dispatcher == null)
                return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                _projectRefreshTimer.Stop();
                _projectRefreshTimer.Start();
            }));
        }

        private void UpdateProjectWatcher(string projectRoot)
        {
            if (string.Equals(_watchedProjectRoot, projectRoot, StringComparison.OrdinalIgnoreCase))
                return;

            if (_projectWatcher != null)
            {
                _projectWatcher.EnableRaisingEvents = false;
                _projectWatcher.Changed -= ProjectWatcher_Changed;
                _projectWatcher.Created -= ProjectWatcher_Changed;
                _projectWatcher.Deleted -= ProjectWatcher_Changed;
                _projectWatcher.Renamed -= ProjectWatcher_Renamed;
                _projectWatcher.Dispose();
                _projectWatcher = null;
            }

            _watchedProjectRoot = projectRoot;
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
                return;

            _projectWatcher = new FileSystemWatcher(projectRoot);
            _projectWatcher.IncludeSubdirectories = true;
            _projectWatcher.NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime;
            _projectWatcher.Changed += ProjectWatcher_Changed;
            _projectWatcher.Created += ProjectWatcher_Changed;
            _projectWatcher.Deleted += ProjectWatcher_Changed;
            _projectWatcher.Renamed += ProjectWatcher_Renamed;
            _projectWatcher.EnableRaisingEvents = true;
        }

        private void ProjectWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            ProjectWatcher_Changed(sender, e);
        }

        private static bool IsGitInternalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return path.IndexOf(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
                || path.EndsWith(Path.DirectorySeparatorChar + ".git", StringComparison.OrdinalIgnoreCase)
                || path.IndexOf(Path.AltDirectorySeparatorChar + ".git" + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
                || path.EndsWith(Path.AltDirectorySeparatorChar + ".git", StringComparison.OrdinalIgnoreCase);
        }

        private static bool AreSameDirectory(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
                return true;

            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                var normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void ClearGitData()
        {
            Changes = new ObservableCollection<GitChangeItemVM>();
            Branches.Clear();
            Commits.Clear();
            SelectedCommit = null;
            RebuildGraphRows();
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
