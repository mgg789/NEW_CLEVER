using Clever.CommonData;
using Clever.ViewModel.BaseVM;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace Clever.ViewModel.PanelsVM
{
    internal class GitPanelVM : BaseViewModel
    {
        private string _commitMessage;
        private string _statusMessage;
        private GitCommitVM _selectedCommit;

        public GitPanelVM()
        {
            BranchName = "develop";
            Changes = GitDesignData.CreateChanges();
            Commits = new ObservableCollection<GitCommitVM>(GitDesignData.CreateCommits());
            GraphRows = GitGraphLayoutService.BuildRows(Commits);
            GraphWidth = GraphRows.Count == 0 ? 120 : GraphRows.Max(r => r.GraphWidth);

            CommitCommand = new RelayCommand(param => SetLocalizedStatus("GitStatusCommit", "Commit UI is ready. Git backend comes next."), true);
            PullCommand = new RelayCommand(param => SetLocalizedStatus("GitStatusPull", "Pull is a UI action in this pass."), true);
            PushCommand = new RelayCommand(param => SetLocalizedStatus("GitStatusPush", "Push is a UI action in this pass."), true);
            ReloadCommand = new RelayCommand(param => SetLocalizedStatus("GitStatusReload", "Design data refreshed."), true);
            SelectBranchCommand = new RelayCommand(param => SetLocalizedStatus("GitStatusBranch", "Branch selection will be connected later."), true);
            ToggleCommitCommand = new RelayCommand(ToggleCommit, true);

            if (GraphRows.Count > 0)
                SelectCommit(GraphRows[0].Commit, false);
        }

        public string BranchName { get; private set; }
        public ObservableCollection<GitChangeItemVM> Changes { get; private set; }
        public ObservableCollection<GitCommitVM> Commits { get; private set; }
        public ObservableCollection<GitGraphRowVM> GraphRows { get; private set; }
        public ICommand CommitCommand { get; private set; }
        public ICommand PullCommand { get; private set; }
        public ICommand PushCommand { get; private set; }
        public ICommand ReloadCommand { get; private set; }
        public ICommand SelectBranchCommand { get; private set; }
        public ICommand ToggleCommitCommand { get; private set; }
        public double GraphWidth { get; private set; }

        public Dictionary<string, string> GetLocalization
        {
            get { return GUILanguage.Items; }
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

        private void ToggleCommit(object parameter)
        {
            var row = parameter as GitGraphRowVM;
            if (row == null || row.Commit == null)
                return;

            SelectCommit(row.Commit, !row.Commit.IsExpanded);
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

        private void SetStatus(string message)
        {
            StatusMessage = message;
        }

        private void SetLocalizedStatus(string key, string fallback)
        {
            var message = GUILanguage.GetItem(key);
            StatusMessage = string.IsNullOrEmpty(message) ? fallback : message;
        }
    }
}
