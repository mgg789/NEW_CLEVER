using Clev3r.AgentCore;
using Clever.Model.Agent;
using Clever.Model.Utils;
using Clever.ViewModel.BaseVM;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace Clever.ViewModel.PanelsVM
{
    internal sealed class AiChatPanelVM : BaseViewModel, IDisposable
    {
        private readonly Clev3rAgentServices _services;
        private readonly ICodexSessionBackend _backend;
        private readonly SlashCommandCatalog _slashCatalog;
        private readonly IList<CodexModelInfo> _modelInfos = new List<CodexModelInfo>();
        private readonly Dictionary<string, StringBuilder> _pendingDeltas = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        private readonly Dictionary<string, AiTurnWorkGroupVM> _workGroups = new Dictionary<string, AiTurnWorkGroupVM>(StringComparer.Ordinal);
        private readonly DispatcherTimer _deltaFlushTimer;
        private readonly DispatcherTimer _historySearchTimer;
        private CancellationTokenSource _turnCancellation;
        private CancellationTokenSource _historyCancellation;
        private string _inputText = "";
        private string _historySearch = "";
        private bool _isBotWorking;
        private bool _isHistoryOpen;
        private bool _isSettingsOpen;
        private bool _isSlashOpen;
        private string _selectedModel;
        private string _selectedReasoning;
        private AiServiceTierChoiceVM _selectedSpeed;
        private string _preferredSpeedId;
        private string _wheelDiameter;
        private string _wheelBaseWidth;
        private string _robotDescription;
        private string _currentThreadId;
        private string _nextHistoryCursor;
        private string _nextTurnsCursor;
        private string _activeTurnId;
        private string _statusTooltip;
        private int _selectedSlashIndex;
        private int _historyVersion;
        private bool _suppressSettingsUpdate;
        private bool _turnStartPending;
        private CodexStatusSnapshot _latestStatus;
        private int _contextUsedPercent;
        private AiChatMessageVM _waitingMessage;
        private AiTurnWorkGroupVM _currentWorkGroup;
        private bool _disposed;

        public AiChatPanelVM()
        {
            _services = Clev3rAgentServices.Current;
            _backend = _services.SessionBackend;
            _slashCatalog = _services.SlashCommands;

            Models = new ObservableCollection<string>();
            ReasoningModes = new ObservableCollection<string>();
            SpeedTiers = new ObservableCollection<AiServiceTierChoiceVM>();
            Messages = new ObservableCollection<AiChatMessageVM>();
            Attachments = new ObservableCollection<AiAttachmentVM>();
            HistoryItems = new ObservableCollection<AiChatHistoryItemVM>();
            SlashItems = new ObservableCollection<AiSlashCommandItemVM>();

            _wheelDiameter = AiAppSettingsStore.Settings.WheelDiameter ?? "";
            _wheelBaseWidth = AiAppSettingsStore.Settings.WheelBaseWidth ?? "";
            _robotDescription = AiAppSettingsStore.Settings.RobotDescription ?? "";
            _selectedModel = AiAppSettingsStore.Settings.CodexModel ?? "";
            _selectedReasoning = AiAppSettingsStore.Settings.CodexReasoningEffort ?? "";
            _preferredSpeedId = NullIfBlank(AiAppSettingsStore.Settings.CodexServiceTier);
            _statusTooltip = Localize("AiStatusLoading", "Codex status is loading.");

            NewChatCommand = new RelayCommand(param => StartNewChat(), true);
            HistoryCommand = new RelayCommand(param => OpenHistory(), true);
            ReloadHistoryCommand = new RelayCommand(param => ReloadHistory(), true);
            LoadMoreHistoryCommand = new RelayCommand(param => LoadMoreHistory(), true);
            OpenHistoryItemCommand = new RelayCommand(param => OpenHistoryItem(param as AiChatHistoryItemVM), true);
            RenameHistoryItemCommand = new RelayCommand(param => RenameHistoryItem(param as AiChatHistoryItemVM), true);
            ArchiveHistoryItemCommand = new RelayCommand(param => ArchiveHistoryItem(param as AiChatHistoryItemVM), true);
            DeleteHistoryItemCommand = new RelayCommand(param => DeleteHistoryItem(param as AiChatHistoryItemVM), true);
            SelectSlashCommand = new RelayCommand(param => ExecuteSlashItem(param as AiSlashCommandItemVM), true);
            ToggleSettingsCommand = new RelayCommand(param => IsSettingsOpen = !IsSettingsOpen, true);
            AttachFileCommand = new RelayCommand(param => PickFiles(), true);
            RemoveAttachmentCommand = new RelayCommand(RemoveAttachment, true);
            SendOrStopCommand = new RelayCommand(param => SendOrStop(), true);
            SteerCommand = new RelayCommand(param => SteerAsync(), true);
            LoadMoreTurnsCommand = new RelayCommand(param => LoadMoreTurns(), true);

            var dispatcher = Application.Current == null ? Dispatcher.CurrentDispatcher : Application.Current.Dispatcher;
            _deltaFlushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, DeltaFlushTimer_Tick, dispatcher);
            _deltaFlushTimer.Stop();
            _historySearchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, HistorySearchTimer_Tick, dispatcher);
            _historySearchTimer.Stop();

            _backend.StateChanged += Backend_StateChanged;
            _backend.EventReceived += Backend_EventReceived;
            MainWindowVM.StaticPropertyChanged += MainWindowVM_StaticPropertyChanged;
            StartNewChat();
            StartBackend();
        }

        public ObservableCollection<AiChatMessageVM> Messages { get; private set; }
        public ObservableCollection<AiAttachmentVM> Attachments { get; private set; }
        public ObservableCollection<AiChatHistoryItemVM> HistoryItems { get; private set; }
        public ObservableCollection<AiSlashCommandItemVM> SlashItems { get; private set; }
        public ObservableCollection<string> Models { get; private set; }
        public ObservableCollection<string> ReasoningModes { get; private set; }
        public ObservableCollection<AiServiceTierChoiceVM> SpeedTiers { get; private set; }

        public ICommand NewChatCommand { get; private set; }
        public ICommand HistoryCommand { get; private set; }
        public ICommand ReloadHistoryCommand { get; private set; }
        public ICommand LoadMoreHistoryCommand { get; private set; }
        public ICommand OpenHistoryItemCommand { get; private set; }
        public ICommand RenameHistoryItemCommand { get; private set; }
        public ICommand ArchiveHistoryItemCommand { get; private set; }
        public ICommand DeleteHistoryItemCommand { get; private set; }
        public ICommand SelectSlashCommand { get; private set; }
        public ICommand ToggleSettingsCommand { get; private set; }
        public ICommand AttachFileCommand { get; private set; }
        public ICommand RemoveAttachmentCommand { get; private set; }
        public ICommand SendOrStopCommand { get; private set; }
        public ICommand SteerCommand { get; private set; }
        public ICommand LoadMoreTurnsCommand { get; private set; }

        public string NewChatText { get { return Localize("AiNewChat", "New chat"); } }
        public string HistoryText { get { return Localize("AiHistory", "Chat history"); } }
        public string HistorySearchText { get { return Localize("AiHistorySearch", "Search chats"); } }
        public string ReloadText { get { return Localize("AiReload", "Reload"); } }
        public string LoadMoreText { get { return Localize("AiLoadMore", "Load more"); } }
        public string RenameText { get { return Localize("AiRename", "Rename"); } }
        public string ArchiveText { get { return Localize("AiArchive", "Archive"); } }
        public string DeleteText { get { return Localize("AiDelete", "Delete"); } }
        public string MessageText { get { return Localize("AiMessage", "Message"); } }
        public string AttachText { get { return Localize("AiAttach", "Attach file"); } }
        public string SendStopText { get { return Localize("AiSendStop", "Send / stop"); } }
        public string RobotSettingsText { get { return Localize("AiRobotSettings", "Robot settings"); } }
        public string WheelDiameterText { get { return Localize("AiWheelDiameter", "Wheel diameter, mm"); } }
        public string WheelbaseText { get { return Localize("AiWheelbase", "Wheelbase, mm"); } }
        public string RobotDescriptionText { get { return Localize("AiRobotDescription", "Robot description"); } }
        public string ModelText { get { return Localize("AiModel", "Model"); } }
        public string ReasoningText { get { return Localize("AiReasoning", "Reasoning"); } }
        public string SpeedText { get { return Localize("AiSpeed", "Speed"); } }
        public string LoadMoreTurnsText { get { return Localize("AiLoadMoreTurns", "Load earlier turns"); } }

        public string InputText
        {
            get { return _inputText; }
            set
            {
                _inputText = value ?? "";
                OnPropertyChanged("InputText");
                OnPropertyChanged("CanSend");
                OnPropertyChanged("SendButtonEnabled");
                RefreshSlashItems();
            }
        }

        public string HistorySearch
        {
            get { return _historySearch; }
            set
            {
                _historySearch = value ?? "";
                OnPropertyChanged("HistorySearch");
                if (IsHistoryOpen)
                {
                    _historySearchTimer.Stop();
                    _historySearchTimer.Start();
                }
            }
        }

        public bool IsBotWorking
        {
            get { return _isBotWorking; }
            private set
            {
                _isBotWorking = value;
                OnPropertyChanged("IsBotWorking");
                OnPropertyChanged("SelectorsEnabled");
                OnPropertyChanged("ModelSelectorEnabled");
                OnPropertyChanged("ReasoningSelectorEnabled");
                OnPropertyChanged("SpeedSelectorEnabled");
                OnPropertyChanged("SendButtonEnabled");
                OnPropertyChanged("SendIconVisibility");
                OnPropertyChanged("StopIconVisibility");
            }
        }

        public bool IsHistoryOpen
        {
            get { return _isHistoryOpen; }
            private set
            {
                _isHistoryOpen = value;
                OnPropertyChanged("IsHistoryOpen");
                OnPropertyChanged("ConversationVisibility");
                OnPropertyChanged("HistoryVisibility");
                OnPropertyChanged("ComposerVisibility");
            }
        }

        public bool IsSettingsOpen
        {
            get { return _isSettingsOpen; }
            set { _isSettingsOpen = value; OnPropertyChanged("IsSettingsOpen"); }
        }

        public bool IsSlashOpen
        {
            get { return _isSlashOpen; }
            private set
            {
                _isSlashOpen = value;
                OnPropertyChanged("IsSlashOpen");
                OnPropertyChanged("SlashVisibility");
            }
        }

        public string SelectedModel
        {
            get { return _selectedModel; }
            set
            {
                if (_selectedModel == value)
                    return;
                _selectedModel = value;
                OnPropertyChanged("SelectedModel");
                RefreshModelDependentOptions();
                PersistCodexDefaults();
                RequestSettingsUpdate();
            }
        }

        public string SelectedReasoning
        {
            get { return _selectedReasoning; }
            set
            {
                if (_selectedReasoning == value)
                    return;
                _selectedReasoning = value;
                OnPropertyChanged("SelectedReasoning");
                PersistCodexDefaults();
                RequestSettingsUpdate();
            }
        }

        public AiServiceTierChoiceVM SelectedSpeed
        {
            get { return _selectedSpeed; }
            set
            {
                if (_selectedSpeed == value)
                    return;
                _selectedSpeed = value;
                _preferredSpeedId = value == null ? null : value.Id;
                OnPropertyChanged("SelectedSpeed");
                PersistCodexDefaults();
                RequestSettingsUpdate();
            }
        }

        public string WheelDiameter
        {
            get { return _wheelDiameter; }
            set
            {
                _wheelDiameter = value ?? "";
                AiAppSettingsStore.Settings.WheelDiameter = _wheelDiameter;
                SaveRobotSettings();
                OnPropertyChanged("WheelDiameter");
            }
        }

        public string WheelBaseWidth
        {
            get { return _wheelBaseWidth; }
            set
            {
                _wheelBaseWidth = value ?? "";
                AiAppSettingsStore.Settings.WheelBaseWidth = _wheelBaseWidth;
                SaveRobotSettings();
                OnPropertyChanged("WheelBaseWidth");
            }
        }

        public string RobotDescription
        {
            get { return _robotDescription; }
            set
            {
                _robotDescription = value ?? "";
                AiAppSettingsStore.Settings.RobotDescription = _robotDescription;
                SaveRobotSettings();
                OnPropertyChanged("RobotDescription");
                OnPropertyChanged("RobotDescriptionCounter");
            }
        }

        public string RobotDescriptionCounter { get { return RobotDescription.Length + "/800"; } }
        public bool HasActiveProject { get { return !string.IsNullOrWhiteSpace(NormalizePath(MainWindowVM.CurrentProjectRoot)); } }
        public bool SelectorsEnabled { get { return HasActiveProject && !IsBotWorking && (_backend.Capabilities.Models || _backend.Capabilities.Reasoning || _backend.Capabilities.Speed); } }
        public bool ModelSelectorEnabled { get { return HasActiveProject && !IsBotWorking && _backend.Capabilities.Models && Models.Count > 0; } }
        public bool ReasoningSelectorEnabled { get { return HasActiveProject && !IsBotWorking && _backend.Capabilities.Reasoning && ReasoningModes.Count > 0; } }
        public bool SpeedSelectorEnabled { get { return HasActiveProject && !IsBotWorking && _backend.Capabilities.Speed && SpeedTiers.Count > 0; } }
        public bool CanSend { get { return HasActiveProject && !string.IsNullOrWhiteSpace(InputText); } }
        public bool SendButtonEnabled { get { return IsBotWorking || (HasActiveProject && !_turnStartPending && CanSend); } }
        public Visibility SendIconVisibility { get { return IsBotWorking ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility StopIconVisibility { get { return IsBotWorking ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility ConversationVisibility { get { return IsHistoryOpen ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility HistoryVisibility { get { return IsHistoryOpen ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility ComposerVisibility { get { return IsHistoryOpen ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility AttachmentVisibility { get { return Attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility SlashVisibility { get { return IsSlashOpen ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility LoadMoreVisibility { get { return string.IsNullOrWhiteSpace(_nextHistoryCursor) ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility LoadMoreTurnsVisibility { get { return string.IsNullOrWhiteSpace(_nextTurnsCursor) ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility ChatContentVisibility { get { return HasActiveProject ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility ProjectRequiredVisibility { get { return HasActiveProject ? Visibility.Collapsed : Visibility.Visible; } }
        public string ProjectRequiredText { get { return Localize("AiOpenProjectSend", "Open or create a project before starting a Codex chat."); } }
        public string ChatInputTooltip { get { return HasActiveProject ? MessageText : ProjectRequiredText; } }
        public string ContextTooltip { get { return _statusTooltip; } }
        public int ContextUsedPercent { get { return _contextUsedPercent; } }
        public Geometry ContextArcGeometry { get { return CreateContextArcGeometry(_contextUsedPercent); } }

        public void AttachDroppedFile(string path)
        {
            string fullPath;
            if (!TryNormalizeProjectFile(path, out fullPath))
            {
                ShowBackendMessage(Localize("AiAttachmentOutsideProject", "Attachments must be files inside the active project."));
                return;
            }
            Attachments.Add(new AiAttachmentVM(Path.GetFileName(fullPath), IsImage(fullPath) ? "IMG" : "FILE", fullPath));
            OnPropertyChanged("AttachmentVisibility");
        }

        public void AttachEditorTab(string tabName)
        {
            if (string.IsNullOrWhiteSpace(tabName))
                return;
            try
            {
                var data = MainWindowVM.Project == null || !MainWindowVM.Project.ContainsKey(tabName)
                    ? null
                    : MainWindowVM.Project.GetDictionary()[tabName];
                AttachDroppedFile(data == null ? null : data.FullPath);
            }
            catch
            {
                ShowBackendMessage(Localize("AiAttachmentUnsaved", "Save this editor tab inside the active project before attaching it."));
            }
        }

        public bool MoveSlashSelection(int offset)
        {
            if (!IsSlashOpen || SlashItems.Count == 0)
                return false;
            _selectedSlashIndex = (_selectedSlashIndex + offset + SlashItems.Count) % SlashItems.Count;
            UpdateSlashSelection();
            return true;
        }

        public bool ExecuteSelectedSlash()
        {
            if (!IsSlashOpen || SlashItems.Count == 0)
                return false;
            ExecuteSlashItem(SlashItems[_selectedSlashIndex]);
            return true;
        }

        public void CloseSlashPalette()
        {
            IsSlashOpen = false;
        }

        private void StartNewChat()
        {
            if (IsBotWorking || _turnStartPending)
                StopTurn();
            CancelTurn();
            _currentThreadId = null;
            _activeTurnId = null;
            _nextTurnsCursor = null;
            _turnStartPending = false;
            _waitingMessage = null;
            _currentWorkGroup = null;
            _workGroups.Clear();
            _contextUsedPercent = 0;
            IsHistoryOpen = false;
            IsBotWorking = false;
            Messages.Clear();
            Attachments.Clear();
            InputText = "";
            OnPropertyChanged("AttachmentVisibility");
            OnPropertyChanged("LoadMoreTurnsVisibility");
            OnPropertyChanged("ContextUsedPercent");
            OnPropertyChanged("ContextArcGeometry");
        }

        private void OpenHistory()
        {
            IsHistoryOpen = true;
            ReloadHistory();
        }

        private async void ReloadHistory()
        {
            _historyVersion++;
            var version = _historyVersion;
            var cancellation = ReplaceCancellation(ref _historyCancellation);
            _nextHistoryCursor = null;
            OnPropertyChanged("LoadMoreVisibility");
            await LoadHistoryPageAsync(null, true, version, cancellation.Token);
        }

        private async void LoadMoreHistory()
        {
            if (string.IsNullOrWhiteSpace(_nextHistoryCursor))
                return;
            await LoadHistoryPageAsync(_nextHistoryCursor, false, _historyVersion, _historyCancellation == null ? CancellationToken.None : _historyCancellation.Token);
        }

        private async Task LoadHistoryPageAsync(string cursor, bool replace, int version, CancellationToken cancellationToken)
        {
            var root = NormalizePath(MainWindowVM.CurrentProjectRoot);
            if (string.IsNullOrWhiteSpace(root))
            {
                OnUi(() =>
                {
                    if (replace) HistoryItems.Clear();
                    MessagesForHistoryStatus(Localize("AiOpenProject", "Open a project to view Codex chats."));
                });
                return;
            }

            BackendResult<CodexThreadPage> result;
            try
            {
                result = await _backend.ListThreadsAsync(root, cursor, HistorySearch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                result = BackendResult<CodexThreadPage>.Fail("history_error", ex.Message);
            }

            OnUi(() =>
            {
                if (version != _historyVersion)
                    return;
                if (replace)
                    HistoryItems.Clear();
                if (!result.Success)
                {
                    MessagesForHistoryStatus(BackendMessage(result.Message));
                    _nextHistoryCursor = null;
                }
                else
                {
                    var page = result.Data ?? new CodexThreadPage();
                    foreach (var thread in page.Threads.Where(item => SamePath(item.Cwd, root)))
                        HistoryItems.Add(new AiChatHistoryItemVM(thread));
                    _nextHistoryCursor = page.NextCursor;
                }
                OnPropertyChanged("LoadMoreVisibility");
            });
        }

        private async void OpenHistoryItem(AiChatHistoryItemVM item)
        {
            if (item == null)
                return;
            if (IsBotWorking || _turnStartPending)
            {
                ShowBackendMessage(Localize("AiTurnAlreadyRunning", "Stop the active turn before switching chats."));
                return;
            }
            var result = await _backend.ResumeThreadAsync(item.Id, CancellationToken.None);
            OnUi(() =>
            {
                if (!result.Success || result.Data == null || result.Data.Thread == null)
                {
                    ShowBackendMessage(result.Message);
                    return;
                }
                if (!SamePath(result.Data.Thread.Cwd, MainWindowVM.CurrentProjectRoot))
                {
                    ShowBackendMessage(Localize("AiThreadWrongProject", "This chat belongs to another project."));
                    return;
                }
                _currentThreadId = result.Data.Thread.Id;
                IsHistoryOpen = false;
                Messages.Clear();
                _workGroups.Clear();
                if (result.Data.Items.Count > 0)
                    AddHistoryItems(result.Data.Items, false);
                else
                    foreach (var message in result.Data.Messages)
                        Messages.Add(AiChatMessageVM.Create(message.Markdown, message.IsUser));
                _nextTurnsCursor = result.Data.NextTurnsCursor;
                OnPropertyChanged("LoadMoreTurnsVisibility");
                RefreshBackendState();
                RequestSettingsUpdate();
            });
        }

        private async void LoadMoreTurns()
        {
            var cursor = _nextTurnsCursor;
            if (string.IsNullOrWhiteSpace(_currentThreadId) || string.IsNullOrWhiteSpace(cursor))
                return;
            var result = await _backend.ListTurnsAsync(_currentThreadId, cursor, CancellationToken.None);
            OnUi(() =>
            {
                if (!result.Success || result.Data == null)
                {
                    ShowBackendMessage(result.Message);
                    return;
                }
                var older = new List<AiChatMessageVM>();
                if (result.Data.Items.Count > 0)
                    older.AddRange(CreateHistoryItems(result.Data.Items));
                else
                    foreach (var message in result.Data.Messages)
                        older.Add(AiChatMessageVM.Create(message.Markdown, message.IsUser));
                for (var i = older.Count - 1; i >= 0; i--)
                    Messages.Insert(0, older[i]);
                _nextTurnsCursor = result.Data.NextTurnsCursor;
                OnPropertyChanged("LoadMoreTurnsVisibility");
            });
        }

        private void AddHistoryItems(IEnumerable<CodexThreadItem> items, bool prepend)
        {
            var timeline = CreateHistoryItems(items);
            if (!prepend)
            {
                foreach (var entry in timeline) Messages.Add(entry);
                return;
            }
            for (var i = timeline.Count - 1; i >= 0; i--) Messages.Insert(0, timeline[i]);
        }

        private static List<AiChatMessageVM> CreateHistoryItems(IEnumerable<CodexThreadItem> items)
        {
            var result = new List<AiChatMessageVM>();
            var groups = new Dictionary<string, AiTurnWorkGroupVM>(StringComparer.Ordinal);
            foreach (var item in items ?? Enumerable.Empty<CodexThreadItem>())
            {
                if (item == null) continue;
                if (AiChatMessageVM.IsProcessKind(item.Kind) || item.Kind == CodexItemKind.AgentMessage)
                {
                    var key = item.TurnId ?? "history";
                    AiTurnWorkGroupVM group;
                    if (!groups.TryGetValue(key, out group))
                    {
                        group = new AiTurnWorkGroupVM(key, DateTime.UtcNow);
                        groups[key] = group;
                        result.Add(AiChatMessageVM.CreateWorkSummary(group));
                    }
                    var process = AiChatMessageVM.CreateItem(item);
                    if (AiChatMessageVM.IsProcessKind(item.Kind))
                    {
                        process.AssignWorkGroup(group);
                        group.AddProcessItem();
                    }
                    else
                    {
                        process.AssignWorkGroup(group, true);
                        group.AddNarrativeItem();
                    }
                    result.Add(process);
                }
                else
                    result.Add(AiChatMessageVM.CreateItem(item));
            }
            foreach (var pair in groups)
            {
                RevealFinalAssistantMessage(result, pair.Value);
                var duration = (items ?? Enumerable.Empty<CodexThreadItem>())
                    .Where(item => item != null && string.Equals(item.TurnId ?? "history", pair.Key, StringComparison.Ordinal))
                    .Select(item => item.TurnDurationMs)
                    .FirstOrDefault(value => value.HasValue);
                pair.Value.Complete(duration);
            }
            return result;
        }

        private async void RenameHistoryItem(AiChatHistoryItemVM item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.EditableTitle))
                return;
            var result = await _backend.RenameThreadAsync(item.Id, item.EditableTitle.Trim(), CancellationToken.None);
            OnUi(() =>
            {
                if (result.Success) item.CommitTitle();
                else ShowBackendMessage(result.Message);
            });
        }

        private async void ArchiveHistoryItem(AiChatHistoryItemVM item)
        {
            if (item == null)
                return;
            var result = await _backend.ArchiveThreadAsync(item.Id, CancellationToken.None);
            OnUi(() => { if (result.Success) ReloadHistory(); else ShowBackendMessage(result.Message); });
        }

        private async void DeleteHistoryItem(AiChatHistoryItemVM item)
        {
            if (item == null)
                return;
            var message = string.Format(Localize("AiDeleteConfirm", "Delete chat '{0}'?"), item.Title);
            if (MessageBox.Show(message, Localize("AiDeleteTitle", "Delete chat"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            var result = await _backend.DeleteThreadAsync(item.Id, CancellationToken.None);
            OnUi(() => { if (result.Success) ReloadHistory(); else ShowBackendMessage(result.Message); });
        }

        private void PickFiles()
        {
            if (!HasActiveProject)
            {
                ShowBackendMessage(ProjectRequiredText);
                return;
            }
            var dialog = new OpenFileDialog { Multiselect = true, InitialDirectory = Directory.Exists(MainWindowVM.CurrentProjectRoot) ? MainWindowVM.CurrentProjectRoot : null };
            if (dialog.ShowDialog() != true)
                return;
            foreach (var fileName in dialog.FileNames)
                AttachDroppedFile(fileName);
        }

        private void RemoveAttachment(object parameter)
        {
            var attachment = parameter as AiAttachmentVM;
            if (attachment != null)
                Attachments.Remove(attachment);
            OnPropertyChanged("AttachmentVisibility");
        }

        private void SendOrStop()
        {
            if (IsBotWorking)
            {
                StopTurn();
                return;
            }
            SendAsync();
        }

        private void ShowWaitingIndicator()
        {
            RemoveWaitingIndicator();
            _waitingMessage = AiChatMessageVM.CreateWaiting();
            Messages.Add(_waitingMessage);
        }

        private void RemoveWaitingIndicator()
        {
            if (_waitingMessage == null)
                return;
            Messages.Remove(_waitingMessage);
            _waitingMessage = null;
        }

        private async void SendAsync()
        {
            if (_disposed || !CanSend || _turnStartPending)
                return;

            SlashCommandDefinition command;
            string argument;
            if (_slashCatalog.TryParseExact(InputText, out command, out argument))
            {
                InputText = "";
                await ExecuteSlashCommandAsync(command, argument);
                return;
            }

            var text = InputText.Trim();
            var root = NormalizePath(MainWindowVM.CurrentProjectRoot);
            if (string.IsNullOrWhiteSpace(root))
            {
                ShowBackendMessage(Localize("AiOpenProjectSend", "Open or create a project before starting a Codex chat."));
                return;
            }

            IList<CodexTurnInput> inputs;
            string attachmentError;
            if (!TryCreateTurnInputs(text, root, out inputs, out attachmentError))
            {
                ShowBackendMessage(attachmentError);
                return;
            }

            InputText = "";
            Attachments.Clear();
            OnPropertyChanged("AttachmentVisibility");
            Messages.Add(AiChatMessageVM.CreateUser(text));
            ShowWaitingIndicator();

            var cancellation = ReplaceCancellation(ref _turnCancellation);
            SetTurnStartPending(true);
            var turnAccepted = false;
            try
            {
                if (_services.WorkspaceContext == null)
                {
                    ShowBackendMessage(Localize("AiFoundationUnavailable", "The CLEV3R agent context is not initialized."));
                    return;
                }

                var context = await _services.WorkspaceContext.SaveAllAndCaptureAsync(cancellation.Token);
                if (!context.Success || context.Data == null)
                {
                    ShowBackendMessage(BackendMessage(context.Message));
                    return;
                }

                if (string.IsNullOrWhiteSpace(_currentThreadId))
                {
                    var guide = _services.Documentation == null ? "" : _services.Documentation.GetCompactGuide();
                    var developerInstructions = new Clev3rPromptBuilder().BuildDeveloperInstructions(context.Data, guide);
                    var started = await _backend.StartThreadAsync(new CodexThreadStartRequest
                    {
                        Cwd = root,
                        DeveloperInstructions = developerInstructions,
                        Settings = CurrentSettings(),
                        ApprovalNever = true,
                        WorkspaceWrite = true,
                        NetworkAccess = true
                    }, cancellation.Token);
                    if (!started.Success || started.Data == null)
                    {
                        ShowBackendMessage(BackendMessage(started.Message));
                        return;
                    }
                    _currentThreadId = started.Data.Id;
                }

                var request = new CodexTurnRequest
                {
                    ThreadId = _currentThreadId,
                    Cwd = root,
                    Text = text,
                    Inputs = inputs,
                    Settings = CurrentSettings(),
                    NetworkAccess = true
                };
                request.AdditionalContext["workspace"] = new CodexAdditionalContextEntry
                {
                    Kind = "untrusted",
                    Value = JsonSerializer.Serialize(context.Data)
                };
                var turn = await _backend.StartTurnAsync(request, cancellation.Token);
                if (!turn.Success || turn.Data == null)
                    ShowBackendMessage(BackendMessage(turn.Message));
                else
                {
                    var acceptedThreadId = turn.Data.ThreadId ?? _currentThreadId;
                    var acceptedTurnId = turn.Data.TurnId;
                    _currentThreadId = acceptedThreadId;
                    _activeTurnId = acceptedTurnId;
                    OnUi(() => BeginAcceptedTurn(acceptedThreadId, acceptedTurnId));
                    turnAccepted = true;
                    _ = ReconcileAcceptedTurnAsync(acceptedThreadId, acceptedTurnId);
                }
            }
            catch (OperationCanceledException)
            {
                ShowBackendMessage(Localize("AiTurnStopped", "The turn was stopped."));
            }
            catch (Exception ex)
            {
                ShowBackendMessage(ex.Message);
            }
            finally
            {
                if (!turnAccepted)
                    OnUi(RemoveWaitingIndicator);
                OnUi(() => SetTurnStartPending(false));
            }
        }

        private async void SteerAsync()
        {
            if (!IsBotWorking || string.IsNullOrWhiteSpace(_currentThreadId) ||
                string.IsNullOrWhiteSpace(_activeTurnId) || !CanSend)
                return;

            var root = NormalizePath(MainWindowVM.CurrentProjectRoot);
            var text = InputText.Trim();
            IList<CodexTurnInput> inputs;
            string attachmentError;
            if (!TryCreateTurnInputs(text, root, out inputs, out attachmentError))
            {
                ShowBackendMessage(attachmentError);
                return;
            }

            InputText = "";
            Attachments.Clear();
            OnPropertyChanged("AttachmentVisibility");
            Messages.Add(AiChatMessageVM.CreateUser(text));
            try
            {
                var result = await _backend.SteerTurnAsync(_currentThreadId, _activeTurnId, inputs, CancellationToken.None);
                if (!result.Success)
                    ShowBackendMessage(BackendMessage(result.Message));
                else if (!string.IsNullOrWhiteSpace(result.Data))
                    _activeTurnId = result.Data;
            }
            catch (Exception ex)
            {
                ShowBackendMessage(ex.Message);
            }
        }

        private async void StopTurn()
        {
            var threadId = _currentThreadId;
            var turnId = _activeTurnId;
            CancelTurn();
            RemoveWaitingIndicator();
            if (!string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(turnId))
            {
                var result = await _backend.InterruptTurnAsync(threadId, turnId, CancellationToken.None);
                if (!result.Success && !result.Unsupported)
                    OnUi(() => ShowBackendMessage(result.Message));
            }
        }

        private async Task ExecuteSlashCommandAsync(SlashCommandDefinition command, string argument)
        {
            if (!IsCapabilityAvailable(command.Capability))
            {
                ShowBackendMessage(Localize("AiCapabilityUnavailable", "This command requires a capability that the connected Codex app-server does not provide."));
                return;
            }

            try
            {
                switch (command.Name)
                {
                    case "model": await ExecuteModelCommandAsync(argument); break;
                    case "reasoning": await ExecuteReasoningCommandAsync(argument); break;
                    case "speed": await ExecuteSpeedCommandAsync(argument); break;
                    case "goal": await ExecuteGoalCommandAsync(argument); break;
                    case "compact": await ShowBooleanResult(() => _backend.CompactAsync(_currentThreadId, CancellationToken.None), "AiCompactDone", "Context compaction started."); break;
                    case "status": await ShowStatusAsync(); break;
                }
            }
            catch (Exception ex)
            {
                ShowBackendMessage(ex.Message);
            }
        }

        private async Task ExecuteModelCommandAsync(string argument)
        {
            await LoadModelsAsync();
            if (string.IsNullOrWhiteSpace(argument))
            {
                ShowBackendMessage(Models.Count == 0 ? Localize("AiNoModels", "No models were returned by Codex.") : Localize("AiModelsAvailable", "Available models:") + "\n- " + string.Join("\n- ", Models));
                return;
            }
            var match = _modelInfos.FirstOrDefault(item => string.Equals(item.Id, argument, StringComparison.OrdinalIgnoreCase) || string.Equals(item.DisplayName, argument, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                ShowBackendMessage(Localize("AiUnknownModel", "The requested model is not available."));
                return;
            }
            SelectedModel = match.Id;
            await UpdateSettingsAsync();
        }

        private async Task ExecuteReasoningCommandAsync(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                ShowBackendMessage(ReasoningModes.Count == 0 ? Localize("AiNoReasoning", "No reasoning levels are available for the selected model.") : Localize("AiReasoningAvailable", "Available reasoning levels:") + "\n- " + string.Join("\n- ", ReasoningModes));
                return;
            }
            var value = ReasoningModes.FirstOrDefault(item => string.Equals(item, argument, StringComparison.OrdinalIgnoreCase));
            if (value == null) { ShowBackendMessage(Localize("AiUnknownReasoning", "The requested reasoning level is not available.")); return; }
            SelectedReasoning = value;
            await UpdateSettingsAsync();
        }

        private async Task ExecuteSpeedCommandAsync(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                ShowBackendMessage(SpeedTiers.Count == 0 ? Localize("AiNoSpeed", "No service tiers are available for the selected model.") : Localize("AiSpeedAvailable", "Available service tiers:") + "\n- " + string.Join("\n- ", SpeedTiers.Select(item => item.DisplayName)));
                return;
            }
            var value = SpeedTiers.FirstOrDefault(item => string.Equals(item.Id ?? "default", argument, StringComparison.OrdinalIgnoreCase) || string.Equals(item.DisplayName, argument, StringComparison.OrdinalIgnoreCase));
            if (value == null) { ShowBackendMessage(Localize("AiUnknownSpeed", "The requested service tier is not available.")); return; }
            SelectedSpeed = value;
            await UpdateSettingsAsync();
        }

        private async Task ExecuteGoalCommandAsync(string argument)
        {
            if (string.IsNullOrWhiteSpace(_currentThreadId))
            {
                ShowBackendMessage(Localize("AiCommandNeedsThread", "Start a chat before using this command."));
                return;
            }
            if (string.Equals(argument, "clear", StringComparison.OrdinalIgnoreCase))
            {
                await ShowBooleanResult(() => _backend.ClearGoalAsync(_currentThreadId, CancellationToken.None), "AiGoalCleared", "Goal cleared.");
                return;
            }
            BackendResult<CodexGoal> result = string.IsNullOrWhiteSpace(argument)
                ? await _backend.GetGoalAsync(_currentThreadId, CancellationToken.None)
                : await _backend.SetGoalAsync(_currentThreadId, new CodexGoal { Objective = argument }, CancellationToken.None);
            if (!result.Success) ShowBackendMessage(BackendMessage(result.Message));
            else if (result.Data == null) ShowBackendMessage(Localize("AiNoGoal", "No goal is set."));
            else ShowBackendMessage(Localize("AiGoal", "Goal") + ": " + result.Data.Objective + "\n" + Localize("AiGoalState", "State") + ": " + (result.Data.Status ?? ""));
        }

        private async Task ShowStatusAsync()
        {
            var result = await _backend.GetStatusAsync(_currentThreadId, CancellationToken.None);
            if (!result.Success || result.Data == null) { ShowBackendMessage(BackendMessage(result.Message)); return; }
            ShowBackendMessage(FormatStatus(result.Data));
        }

        private async Task ShowBooleanResult(Func<Task<BackendResult<bool>>> action, string successKey, string successFallback)
        {
            if (string.IsNullOrWhiteSpace(_currentThreadId))
            {
                ShowBackendMessage(Localize("AiCommandNeedsThread", "Start a chat before using this command."));
                return;
            }
            var result = await action();
            ShowBackendMessage(result.Success ? Localize(successKey, successFallback) : BackendMessage(result.Message));
        }

        private async void ExecuteSlashItem(AiSlashCommandItemVM item)
        {
            if (item == null)
                return;
            IsSlashOpen = false;
            InputText = "";
            await ExecuteSlashCommandAsync(item.Command, "");
        }

        private void RefreshSlashItems()
        {
            var matches = _slashCatalog.Filter(InputText, _backend.Capabilities);
            SlashItems.Clear();
            foreach (var match in matches)
            {
                SlashItems.Add(new AiSlashCommandItemVM(
                    match.Command,
                    Localize(match.Command.TitleKey, HumanizeCommandName(match.Command.Name)),
                    GetSlashStatus(match.Command),
                    GetSlashIcon(match.Command),
                    match.IsAvailable,
                    match.IsAvailable ? "" : Localize("AiCapabilityUnavailableShort", "Unavailable until a compatible Codex app-server is connected.")));
            }
            _selectedSlashIndex = 0;
            UpdateSlashSelection();
            IsSlashOpen = SlashItems.Count > 0;
        }

        private string GetSlashStatus(SlashCommandDefinition command)
        {
            if (command == null) return "";
            switch (command.Name)
            {
                case "model": return SelectedModel ?? "";
                case "reasoning": return DisplayReasoning(SelectedReasoning);
                case "speed": return SelectedSpeed == null ? Localize("AiSpeedDefault", "Default") : SelectedSpeed.DisplayName;
                case "goal": return _latestStatus != null && _latestStatus.Goal != null && !string.IsNullOrWhiteSpace(_latestStatus.Goal.Objective)
                    ? _latestStatus.Goal.Objective : Localize("AiGoalNotSet", "Not set");
                case "compact": return _contextUsedPercent + "%";
                case "status": return _latestStatus == null ? Localize("AiStatusLoadingShort", "Loading") : _latestStatus.ConnectionState.ToString();
                default: return "";
            }
        }

        private static Geometry GetSlashIcon(SlashCommandDefinition command)
        {
            switch (command == null ? "" : command.Name)
            {
                case "model": return AiIconGeometry.Model;
                case "reasoning": return AiIconGeometry.Reasoning;
                case "speed": return AiIconGeometry.Speed;
                case "goal": return AiIconGeometry.Goal;
                case "compact": return AiIconGeometry.Compact;
                default: return AiIconGeometry.SystemTool;
            }
        }

        private static string DisplayReasoning(string value)
        {
            var text = (value ?? "").Trim();
            var normalized = text.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
            if (normalized == "medium") return "mid";
            if (normalized == "extrahigh") return "xhigh";
            return text;
        }

        private static string HumanizeCommandName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return char.ToUpperInvariant(value[0]) + value.Substring(1).Replace('_', ' ');
        }

        private void UpdateSlashSelection()
        {
            for (var i = 0; i < SlashItems.Count; i++)
                SlashItems[i].IsSelected = i == _selectedSlashIndex;
        }

        private async void RefreshBackendState()
        {
            try
            {
                await LoadModelsAsync();
                var result = await _backend.GetStatusAsync(_currentThreadId, CancellationToken.None);
                OnUi(() =>
                {
                    if (result.Success && result.Data != null && !string.IsNullOrWhiteSpace(_currentThreadId))
                        ApplyBackendSettings(result.Data.Settings);
                    ApplyStatus(result.Success ? result.Data : null, result.Success ? null : result.Message);
                    NotifySelectorState();
                    RefreshSlashItems();
                });
            }
            catch (Exception ex)
            {
                OnUi(() => ApplyStatus(null, ex.Message));
            }
        }

        private async void RefreshStatusOnly()
        {
            try
            {
                var result = await _backend.GetStatusAsync(_currentThreadId, CancellationToken.None);
                OnUi(() =>
                {
                    ApplyStatus(result.Success ? result.Data : null, result.Success ? null : result.Message);
                    RefreshSlashItems();
                });
            }
            catch (Exception ex)
            {
                OnUi(() => ApplyStatus(null, ex.Message));
            }
        }

        private async Task LoadModelsAsync()
        {
            if (!_backend.Capabilities.Models)
            {
                OnUi(() =>
                {
                    _suppressSettingsUpdate = true;
                    try
                    {
                        _modelInfos.Clear();
                        Models.Clear();
                        ReasoningModes.Clear();
                        SpeedTiers.Clear();
                    }
                    finally { _suppressSettingsUpdate = false; }
                    NotifySelectorState();
                });
                return;
            }
            var result = await _backend.ListModelsAsync(CancellationToken.None);
            if (!result.Success || result.Data == null)
                return;
            OnUi(() =>
            {
                _suppressSettingsUpdate = true;
                try
                {
                    _modelInfos.Clear();
                    Models.Clear();
                    foreach (var model in result.Data)
                    {
                        _modelInfos.Add(model);
                        Models.Add(model.Id);
                    }
                    if (!Models.Contains(SelectedModel))
                        SelectedModel = result.Data.FirstOrDefault(item => item.IsDefault)?.Id ?? Models.FirstOrDefault();
                    RefreshModelDependentOptions();
                }
                finally { _suppressSettingsUpdate = false; }
                NotifySelectorState();
            });
        }

        private void RefreshModelDependentOptions()
        {
            var model = _modelInfos.FirstOrDefault(item => string.Equals(item.Id, SelectedModel, StringComparison.OrdinalIgnoreCase));
            var previousSuppression = _suppressSettingsUpdate;
            var selectedTierId = SelectedSpeed == null ? _preferredSpeedId : SelectedSpeed.Id;
            _suppressSettingsUpdate = true;
            try
            {
                ReasoningModes.Clear();
                SpeedTiers.Clear();
                if (model != null)
                {
                    foreach (var value in model.SupportedReasoningEfforts) ReasoningModes.Add(value);
                    SpeedTiers.Add(new AiServiceTierChoiceVM(null, Localize("AiSpeedDefault", "Default"), ""));
                    foreach (var value in model.ServiceTiers.Where(item =>
                        !item.IsDefault &&
                        !string.IsNullOrWhiteSpace(item.Id) &&
                        !string.Equals(item.Id, "default", StringComparison.OrdinalIgnoreCase) &&
                        !SpeedTiers.Any(existing => !string.IsNullOrWhiteSpace(existing.Id) &&
                            string.Equals(existing.Id, item.Id, StringComparison.OrdinalIgnoreCase))))
                        SpeedTiers.Add(new AiServiceTierChoiceVM(value.Id, string.IsNullOrWhiteSpace(value.Name) ? (value.IsDefault ? Localize("AiSpeedDefault", "Default") : value.Id) : value.Name, value.Description));
                }
                if (!ReasoningModes.Contains(SelectedReasoning)) SelectedReasoning = ReasoningModes.FirstOrDefault();
                SelectedSpeed = SpeedTiers.FirstOrDefault(item => string.Equals(item.Id, selectedTierId, StringComparison.OrdinalIgnoreCase))
                    ?? SpeedTiers.FirstOrDefault();
            }
            finally { _suppressSettingsUpdate = previousSuppression; }
            NotifySelectorState();
        }

        private void ApplyBackendSettings(CodexThreadSettings settings)
        {
            if (settings == null) return;
            var previousSuppression = _suppressSettingsUpdate;
            _suppressSettingsUpdate = true;
            try
            {
                var preferredModel = AiAppSettingsStore.Settings.CodexModel;
                if (string.IsNullOrWhiteSpace(preferredModel) && !string.IsNullOrWhiteSpace(settings.Model))
                {
                    if (!Models.Any(item => string.Equals(item, settings.Model, StringComparison.OrdinalIgnoreCase)))
                        Models.Add(settings.Model);
                    SelectedModel = settings.Model;
                }
                RefreshModelDependentOptions();

                var preferredReasoning = AiAppSettingsStore.Settings.CodexReasoningEffort;
                if (string.IsNullOrWhiteSpace(preferredReasoning) && !string.IsNullOrWhiteSpace(settings.ReasoningEffort))
                {
                    if (!ReasoningModes.Any(item => string.Equals(item, settings.ReasoningEffort, StringComparison.OrdinalIgnoreCase)))
                        ReasoningModes.Add(settings.ReasoningEffort);
                    SelectedReasoning = settings.ReasoningEffort;
                }

                var preferredTier = NullIfBlank(AiAppSettingsStore.Settings.CodexServiceTier);
                var effectiveTier = preferredTier ?? settings.ServiceTier;
                var selectedTier = SpeedTiers.FirstOrDefault(item =>
                    string.Equals(item.Id, effectiveTier, StringComparison.OrdinalIgnoreCase));
                if (selectedTier == null && preferredTier == null && !string.IsNullOrWhiteSpace(effectiveTier))
                {
                    selectedTier = new AiServiceTierChoiceVM(effectiveTier, effectiveTier, "");
                    SpeedTiers.Add(selectedTier);
                }
                SelectedSpeed = selectedTier ?? SpeedTiers.FirstOrDefault(item => item.Id == null);
            }
            finally { _suppressSettingsUpdate = previousSuppression; }
            NotifySelectorState();
        }

        private async void RequestSettingsUpdate()
        {
            try
            {
                if (!_suppressSettingsUpdate && !string.IsNullOrWhiteSpace(_currentThreadId))
                    await UpdateSettingsAsync();
            }
            catch (Exception ex)
            {
                OnUi(() => ShowBackendMessage(ex.Message));
            }
        }

        private async Task UpdateSettingsAsync()
        {
            if (string.IsNullOrWhiteSpace(_currentThreadId))
                return;
            var result = await _backend.UpdateSettingsAsync(_currentThreadId, CurrentSettings(), CancellationToken.None);
            if (!result.Success)
                OnUi(() => ShowBackendMessage(result.Message));
        }

        private CodexThreadSettings CurrentSettings()
        {
            return new CodexThreadSettings { Model = SelectedModel, ReasoningEffort = SelectedReasoning, ServiceTier = SelectedSpeed == null ? null : SelectedSpeed.Id };
        }

        private async void StartBackend()
        {
            BackendResult<CodexStatusSnapshot> result;
            try { result = await _backend.StartAsync(CancellationToken.None); }
            catch (Exception ex) { result = BackendResult<CodexStatusSnapshot>.Fail("backend_start_failed", ex.Message); }
            OnUi(() =>
            {
                ApplyStatus(result.Success ? result.Data : null, result.Success ? null : result.Message);
                RefreshBackendState();
            });
        }

        private void SetTurnStartPending(bool value)
        {
            _turnStartPending = value;
            OnPropertyChanged("SendButtonEnabled");
        }

        private bool TryCreateTurnInputs(string text, string root, out IList<CodexTurnInput> inputs, out string error)
        {
            inputs = new List<CodexTurnInput> { new CodexTurnInput { Type = "text", Text = text } };
            error = null;
            foreach (var attachment in Attachments)
            {
                string fullPath;
                if (!TryNormalizeProjectFile(attachment.Source, out fullPath) || !File.Exists(fullPath))
                {
                    error = Localize("AiAttachmentOutsideProject", "Attachments must be files inside the active project.");
                    return false;
                }
                inputs.Add(new CodexTurnInput
                {
                    Type = IsImage(fullPath) ? "localImage" : "mention",
                    Path = fullPath,
                    Name = MakeRelativePath(root, fullPath)
                });
            }
            return true;
        }

        private static bool TryNormalizeProjectFile(string path, out string fullPath)
        {
            fullPath = "";
            var root = NormalizePath(MainWindowVM.CurrentProjectRoot);
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                fullPath = Path.GetFullPath(path);
                var rootPrefix = root + Path.DirectorySeparatorChar;
                return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string MakeRelativePath(string root, string fullPath)
        {
            try
            {
                var rootUri = new Uri(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(fullPath)).ToString()).Replace('/', Path.DirectorySeparatorChar);
            }
            catch { return Path.GetFileName(fullPath); }
        }

        private static bool IsImage(string path)
        {
            switch ((Path.GetExtension(path) ?? "").ToLowerInvariant())
            {
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".webp": case ".bmp": return true;
                default: return false;
            }
        }

        private void Backend_EventReceived(object sender, CodexBackendEventArgs e)
        {
            if (e == null)
                return;
            OnUi(() => HandleBackendEvent(e));
        }

        private void HandleBackendEvent(CodexBackendEventArgs e)
        {
            var belongsToCurrentThread = string.IsNullOrWhiteSpace(e.ThreadId)
                || string.Equals(e.ThreadId, _currentThreadId, StringComparison.Ordinal)
                || (string.IsNullOrWhiteSpace(_currentThreadId) && _turnStartPending);
            switch (e.Kind)
            {
                case CodexBackendEventKind.ConnectionChanged:
                    RefreshBackendState();
                    break;
                case CodexBackendEventKind.AccountUpdated:
                case CodexBackendEventKind.RateLimitsUpdated:
                    RefreshStatusOnly();
                    break;
                case CodexBackendEventKind.TokenUsageUpdated:
                    if (belongsToCurrentThread && e.TokenUsage != null)
                    {
                        if (_latestStatus == null) _latestStatus = new CodexStatusSnapshot();
                        _latestStatus.TokenUsage = e.TokenUsage;
                        _latestStatus.ContextUsedPercent = e.TokenUsage.UsedPercent;
                        ApplyStatus(_latestStatus, null);
                        RefreshSlashItems();
                    }
                    break;
                case CodexBackendEventKind.ThreadStarted:
                    if (_turnStartPending && e.Thread != null && SamePath(e.Thread.Cwd, MainWindowVM.CurrentProjectRoot))
                        _currentThreadId = e.Thread.Id;
                    break;
                case CodexBackendEventKind.ThreadDeleted:
                    if (string.Equals(e.ThreadId, _currentThreadId, StringComparison.Ordinal))
                        StartNewChat();
                    break;
                case CodexBackendEventKind.TurnStarted:
                    if (belongsToCurrentThread)
                    {
                        _currentThreadId = string.IsNullOrWhiteSpace(e.ThreadId) ? _currentThreadId : e.ThreadId;
                        _activeTurnId = e.TurnId;
                        SetTurnStartPending(false);
                        IsBotWorking = true;
                        _currentWorkGroup = GetOrCreateWorkGroup(e.TurnId);
                    }
                    break;
                case CodexBackendEventKind.TurnCompleted:
                    if (belongsToCurrentThread && (string.IsNullOrWhiteSpace(_activeTurnId) || string.Equals(e.TurnId, _activeTurnId, StringComparison.Ordinal)))
                    {
                        RemoveWaitingIndicator();
                        FlushAllDeltas();
                        CompleteWorkGroup(e.TurnId, e.TurnDurationMs);
                        RefreshOpenEditorsFromAgentChanges(null);
                        IsBotWorking = false;
                        _activeTurnId = null;
                    }
                    break;
                case CodexBackendEventKind.ItemStarted:
                    if (belongsToCurrentThread && (e.Item == null || e.Item.Kind != CodexItemKind.UserMessage))
                    {
                        RemoveWaitingIndicator();
                        AddOrUpdateItem(e.Item, e.ItemId);
                    }
                    break;
                case CodexBackendEventKind.ItemDelta:
                    if (belongsToCurrentThread)
                    {
                        RemoveWaitingIndicator();
                        QueueDelta(e.ItemId, e.Delta, e.Item);
                    }
                    break;
                case CodexBackendEventKind.ItemCompleted:
                    if (belongsToCurrentThread && (e.Item == null || e.Item.Kind != CodexItemKind.UserMessage))
                    {
                        RemoveWaitingIndicator();
                        FlushDelta(e.ItemId);
                        AddOrUpdateItem(e.Item, e.ItemId);
                        if (e.Item != null && e.Item.Kind == CodexItemKind.FileChange)
                            RefreshOpenEditorsFromAgentChanges(e.Item);
                    }
                    break;
                case CodexBackendEventKind.Error:
                    if (belongsToCurrentThread)
                    {
                        RemoveWaitingIndicator();
                        CompleteWorkGroup(e.TurnId, e.TurnDurationMs);
                        IsBotWorking = false;
                        _activeTurnId = null;
                        SetTurnStartPending(false);
                        Messages.Add(AiChatMessageVM.CreateError(BackendMessage(e.Message)));
                    }
                    break;
            }
        }

        private void AddOrUpdateItem(CodexThreadItem item, string fallbackId)
        {
            if (item == null)
                return;
            var itemId = string.IsNullOrWhiteSpace(item.Id) ? fallbackId : item.Id;
            var existing = FindTimelineItem(itemId);
            if (existing == null)
            {
                existing = AiChatMessageVM.CreateItem(item, itemId);
                if (AiChatMessageVM.IsProcessKind(item.Kind))
                {
                    var group = GetOrCreateWorkGroup(item.TurnId);
                    existing.AssignWorkGroup(group);
                    group.AddProcessItem();
                }
                else if (item.Kind == CodexItemKind.AgentMessage)
                {
                    var group = GetOrCreateWorkGroup(item.TurnId);
                    existing.AssignWorkGroup(group, true);
                    group.AddNarrativeItem();
                    if (group.IsCompleted) existing.RevealAsFinalResponse();
                }
                Messages.Add(existing);
            }
            else
            {
                existing.Update(item);
                if (AiChatMessageVM.IsProcessKind(item.Kind) && existing.WorkGroup == null)
                {
                    var group = GetOrCreateWorkGroup(item.TurnId);
                    existing.AssignWorkGroup(group);
                    group.AddProcessItem();
                }
                else if (item.Kind == CodexItemKind.AgentMessage && existing.WorkGroup == null)
                {
                    var group = GetOrCreateWorkGroup(item.TurnId);
                    existing.AssignWorkGroup(group, true);
                    group.AddNarrativeItem();
                    if (group.IsCompleted) existing.RevealAsFinalResponse();
                }
            }
        }

        private void QueueDelta(string itemId, string delta, CodexThreadItem item)
        {
            itemId = !string.IsNullOrWhiteSpace(itemId) ? itemId : item == null ? null : item.Id;
            if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrEmpty(delta))
                return;
            if (FindTimelineItem(itemId) == null)
            {
                var created = item == null ? AiChatMessageVM.CreateStreaming(itemId) : AiChatMessageVM.CreateItem(item, itemId);
                if (item != null && AiChatMessageVM.IsProcessKind(item.Kind))
                {
                    var group = GetOrCreateWorkGroup(item.TurnId);
                    created.AssignWorkGroup(group);
                    group.AddProcessItem();
                }
                Messages.Add(created);
            }
            StringBuilder builder;
            if (!_pendingDeltas.TryGetValue(itemId, out builder))
            {
                builder = new StringBuilder();
                _pendingDeltas[itemId] = builder;
            }
            builder.Append(delta);
            if (!_deltaFlushTimer.IsEnabled)
                _deltaFlushTimer.Start();
        }

        private void DeltaFlushTimer_Tick(object sender, EventArgs e)
        {
            FlushAllDeltas();
            _deltaFlushTimer.Stop();
        }

        private void FlushDelta(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                return;
            StringBuilder builder;
            if (!_pendingDeltas.TryGetValue(itemId, out builder))
                return;
            _pendingDeltas.Remove(itemId);
            var item = FindTimelineItem(itemId);
            if (item != null)
                item.Append(builder.ToString());
        }

        private void FlushAllDeltas()
        {
            foreach (var itemId in _pendingDeltas.Keys.ToList())
                FlushDelta(itemId);
        }

        private AiChatMessageVM FindTimelineItem(string itemId)
        {
            return string.IsNullOrWhiteSpace(itemId) ? null : Messages.FirstOrDefault(item => string.Equals(item.ItemId, itemId, StringComparison.Ordinal));
        }

        private AiTurnWorkGroupVM GetOrCreateWorkGroup(string turnId)
        {
            var key = string.IsNullOrWhiteSpace(turnId) ? (_activeTurnId ?? "current") : turnId;
            AiTurnWorkGroupVM group;
            if (_workGroups.TryGetValue(key, out group))
                return group;
            group = new AiTurnWorkGroupVM(key, DateTime.UtcNow);
            _workGroups[key] = group;
            Messages.Add(AiChatMessageVM.CreateWorkSummary(group));
            return group;
        }

        private void CompleteWorkGroup(string turnId, long? durationMs)
        {
            var key = string.IsNullOrWhiteSpace(turnId) ? (_activeTurnId ?? "current") : turnId;
            AiTurnWorkGroupVM group;
            if (!_workGroups.TryGetValue(key, out group))
                group = _currentWorkGroup;
            if (group != null)
            {
                RevealFinalAssistantMessage(Messages, group);
                group.Complete(durationMs);
            }
            if (ReferenceEquals(_currentWorkGroup, group))
                _currentWorkGroup = null;
        }

        private static void RevealFinalAssistantMessage(IEnumerable<AiChatMessageVM> entries, AiTurnWorkGroupVM group)
        {
            if (group == null)
                return;
            var finalMessage = (entries ?? Enumerable.Empty<AiChatMessageVM>())
                .LastOrDefault(item => item != null &&
                    item.Kind == CodexItemKind.AgentMessage &&
                    ReferenceEquals(item.WorkGroup, group));
            if (finalMessage != null)
                finalMessage.RevealAsFinalResponse();
        }

        private void BeginAcceptedTurn(string threadId, string turnId)
        {
            if (string.IsNullOrWhiteSpace(turnId))
                return;
            if (!string.IsNullOrWhiteSpace(threadId))
                _currentThreadId = threadId;
            _activeTurnId = turnId;
            SetTurnStartPending(false);
            IsBotWorking = true;
            _currentWorkGroup = GetOrCreateWorkGroup(turnId);
        }

        // Some app-server versions can deliver the turn RPC response before a streaming
        // notification reaches the WPF dispatcher. Reconcile the completed turn from its
        // durable history so the composer cannot remain on an endless waiting indicator.
        private async Task ReconcileAcceptedTurnAsync(string threadId, string turnId)
        {
            if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
                return;
            try
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    if (_disposed)
                        return;

                    var turns = await _backend.ListTurnsAsync(threadId, null, CancellationToken.None).ConfigureAwait(false);
                    if (!turns.Success || turns.Data == null)
                        continue;
                    var entries = turns.Data.Items
                        .Where(item => item != null && string.Equals(item.TurnId, turnId, StringComparison.Ordinal))
                        .ToList();
                    var duration = entries.Select(item => item.TurnDurationMs).FirstOrDefault(value => value.HasValue);
                    if (!duration.HasValue)
                        continue;

                    OnUi(() =>
                    {
                        if (!string.Equals(_activeTurnId, turnId, StringComparison.Ordinal))
                            return;
                        foreach (var entry in entries)
                        {
                            if (entry.Kind != CodexItemKind.UserMessage)
                            {
                                AddOrUpdateItem(entry, entry.Id);
                                if (entry.Kind == CodexItemKind.FileChange)
                                    RefreshOpenEditorsFromAgentChanges(entry);
                            }
                        }
                        RemoveWaitingIndicator();
                        FlushAllDeltas();
                        CompleteWorkGroup(turnId, duration);
                        RefreshOpenEditorsFromAgentChanges(null);
                        IsBotWorking = false;
                        _activeTurnId = null;
                        SetTurnStartPending(false);
                    });
                    return;
                }
            }
            catch
            {
                // The regular notification path remains authoritative; reconciliation is
                // best-effort and must not surface a second error for an active turn.
            }
        }

        private static void RefreshOpenEditorsFromAgentChanges(CodexThreadItem item)
        {
            var root = NormalizePath(MainWindowVM.CurrentProjectRoot);
            if (string.IsNullOrWhiteSpace(root))
                return;

            MainWindowVM.ReloadOpenProjectFilesFromDisk(
                root,
                item == null ? null : ExtractChangedPaths(item),
                true);
        }

        private static IEnumerable<string> ExtractChangedPaths(CodexThreadItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.RawJson))
                return Enumerable.Empty<string>();

            try
            {
                using (var document = JsonDocument.Parse(item.RawJson))
                {
                    JsonElement changes;
                    if (!document.RootElement.TryGetProperty("changes", out changes) ||
                        changes.ValueKind != JsonValueKind.Array)
                        return Enumerable.Empty<string>();

                    var paths = new List<string>();
                    foreach (var change in changes.EnumerateArray())
                    {
                        JsonElement path;
                        if (change.ValueKind == JsonValueKind.Object &&
                            change.TryGetProperty("path", out path) &&
                            path.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(path.GetString()))
                            paths.Add(path.GetString());
                    }
                    return paths;
                }
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private void HistorySearchTimer_Tick(object sender, EventArgs e)
        {
            _historySearchTimer.Stop();
            if (IsHistoryOpen)
                ReloadHistory();
        }

        private bool IsCapabilityAvailable(SlashCommandCapability capability)
        {
            var value = _backend.Capabilities;
            switch (capability)
            {
                case SlashCommandCapability.Models: return value.Models;
                case SlashCommandCapability.Reasoning: return value.Reasoning;
                case SlashCommandCapability.Speed: return value.Speed;
                case SlashCommandCapability.Goals: return value.Goals;
                case SlashCommandCapability.Compact: return value.Compact;
                case SlashCommandCapability.Status: return value.Status;
                default: return false;
            }
        }

        private void ApplyStatus(CodexStatusSnapshot status, string error)
        {
            _latestStatus = status;
            var usage = status == null ? null : status.TokenUsage;
            _contextUsedPercent = usage != null ? usage.UsedPercent : status != null && status.ContextUsedPercent.HasValue ? status.ContextUsedPercent.Value : 0;
            _statusTooltip = status == null ? BackendMessage(error) : FormatStatus(status);
            OnPropertyChanged("ContextTooltip");
            OnPropertyChanged("ContextUsedPercent");
            OnPropertyChanged("ContextArcGeometry");
        }

        private string FormatStatus(CodexStatusSnapshot status)
        {
            var lines = new List<string>();
            if (status.Auth != null && !string.IsNullOrWhiteSpace(status.Auth.Email))
                lines.Add(Localize("AiStatusAccount", "Account") + ": " + status.Auth.Email);
            if (status.Auth != null && !string.IsNullOrWhiteSpace(status.Auth.PlanType))
                lines.Add(Localize("AiStatusPlan", "Plan") + ": " + FormatPlan(status.Auth.PlanType));
            AddRateLimitLine(lines, status.RateLimits == null ? null : status.RateLimits.Primary);
            AddRateLimitLine(lines, status.RateLimits == null ? null : status.RateLimits.Secondary);
            var usage = status.TokenUsage;
            if (usage != null && usage.ModelContextWindow > 0)
                lines.Add(Localize("AiStatusContext", "Context") + ": " + FormatTokens(usage.TotalTokens) + " / " + FormatTokens(usage.ModelContextWindow));
            else if (status.ContextUsedPercent.HasValue)
                lines.Add(Localize("AiStatusContext", "Context") + ": " + status.ContextUsedPercent.Value + "%");
            if (lines.Count == 0 && !string.IsNullOrWhiteSpace(status.Message))
                lines.Add(status.Message);
            return string.Join("\n", lines);
        }

        private static string FormatPlan(string value)
        {
            var normalized = (value ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
            switch (normalized)
            {
                case "free": return "Free";
                case "go": return "Go";
                case "plus": return "Plus";
                case "prolite":
                case "pro5x": return "Pro 5x";
                case "pro":
                case "pro20x": return "Pro 20x";
                default: return string.IsNullOrWhiteSpace(value) ? "" : char.ToUpperInvariant(value[0]) + value.Substring(1);
            }
        }

        private void AddRateLimitLine(ICollection<string> lines, CodexRateLimitWindow window)
        {
            if (window == null)
                return;
            var label = FormatRateWindow(window.WindowDurationMinutes);
            lines.Add(label + " " + Localize("AiStatusLimit", "limit") + ": " + window.UsedPercent + "%");
        }

        private static string FormatRateWindow(long? minutes)
        {
            if (!minutes.HasValue || minutes.Value <= 0) return "Codex";
            if (minutes.Value % (7 * 24 * 60) == 0) return (minutes.Value / (7 * 24 * 60)) + "w";
            if (minutes.Value % (24 * 60) == 0) return (minutes.Value / (24 * 60)) + "d";
            if (minutes.Value % 60 == 0) return (minutes.Value / 60) + "h";
            return minutes.Value + "m";
        }

        private static string FormatTokens(long value)
        {
            if (value >= 1000000) return (value / 1000000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "m";
            if (value >= 1000) return (value / 1000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k";
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private void ShowBackendMessage(string message)
        {
            Messages.Add(AiChatMessageVM.CreateAssistant(BackendMessage(message)));
            IsHistoryOpen = false;
        }

        private void MessagesForHistoryStatus(string message)
        {
            if (HistoryItems.Count == 0)
                HistoryItems.Add(AiChatHistoryItemVM.Status(message));
        }

        private string BackendMessage(string message)
        {
            return string.IsNullOrWhiteSpace(message) ? Localize("AiBackendUnavailable", "Codex app-server is not connected yet.") : message;
        }

        private void SaveRobotSettings()
        {
            try { AiAppSettingsStore.SaveSettings(); }
            catch (Exception ex) { _statusTooltip = ex.Message; OnPropertyChanged("ContextTooltip"); }
        }

        private void PersistCodexDefaults()
        {
            if (_suppressSettingsUpdate)
                return;
            try
            {
                AiAppSettingsStore.Settings.CodexModel = _selectedModel ?? "";
                AiAppSettingsStore.Settings.CodexReasoningEffort = _selectedReasoning ?? "";
                AiAppSettingsStore.Settings.CodexServiceTier = _preferredSpeedId ?? "";
                AiAppSettingsStore.SaveSettings();
            }
            catch (Exception ex)
            {
                _statusTooltip = ex.Message;
                OnPropertyChanged("ContextTooltip");
            }
        }

        private void NotifySelectorState()
        {
            OnPropertyChanged("SelectorsEnabled");
            OnPropertyChanged("ModelSelectorEnabled");
            OnPropertyChanged("ReasoningSelectorEnabled");
            OnPropertyChanged("SpeedSelectorEnabled");
        }

        private void Backend_StateChanged(object sender, EventArgs e) { OnUi(RefreshStatusOnly); }

        private void MainWindowVM_StaticPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "CurrentWindowTitle")
                OnUi(() =>
                {
                    var reopenHistory = IsHistoryOpen;
                    StartNewChat();
                    if (reopenHistory) OpenHistory();
                    NotifyProjectState();
                    RefreshBackendState();
                });
        }

        private void NotifyProjectState()
        {
            OnPropertyChanged("HasActiveProject");
            OnPropertyChanged("CanSend");
            OnPropertyChanged("SendButtonEnabled");
            OnPropertyChanged("ChatContentVisibility");
            OnPropertyChanged("ProjectRequiredVisibility");
            OnPropertyChanged("ProjectRequiredText");
            OnPropertyChanged("ChatInputTooltip");
            NotifySelectorState();
        }

        private void OnUi(Action action)
        {
            if (action == null || _disposed)
                return;
            var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }

        private static CancellationTokenSource ReplaceCancellation(ref CancellationTokenSource source)
        {
            var replacement = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref source, replacement);
            CancelSafely(previous);
            return replacement;
        }

        private void CancelTurn()
        {
            CancelSafely(Interlocked.CompareExchange(ref _turnCancellation, null, null));
        }

        private static void CancelSafely(CancellationTokenSource source)
        {
            if (source == null)
                return;
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private static void CancelAndDisposeSafely(CancellationTokenSource source)
        {
            if (source == null)
                return;
            CancelSafely(source);
            try { source.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return ""; }
        }

        private static string NullIfBlank(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static Geometry CreateContextArcGeometry(int percent)
        {
            if (percent <= 0)
                return Geometry.Empty;
            if (percent >= 100)
            {
                var full = new EllipseGeometry(new Point(9, 9), 7, 7);
                full.Freeze();
                return full;
            }

            var angle = Math.PI * 2 * percent / 100d;
            var end = new Point(9 + 7 * Math.Sin(angle), 9 - 7 * Math.Cos(angle));
            var figure = new PathFigure { StartPoint = new Point(9, 2), IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(7, 7), 0, percent > 50, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry(new[] { figure });
            geometry.Freeze();
            return geometry;
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }

        internal static string Localize(string key, string fallback)
        {
            string value;
            return MainWindowVM.GetLocalization != null && MainWindowVM.GetLocalization.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _backend.StateChanged -= Backend_StateChanged;
            _backend.EventReceived -= Backend_EventReceived;
            MainWindowVM.StaticPropertyChanged -= MainWindowVM_StaticPropertyChanged;
            _deltaFlushTimer.Stop();
            _historySearchTimer.Stop();
            CancelAndDisposeSafely(Interlocked.Exchange(ref _turnCancellation, null));
            CancelAndDisposeSafely(Interlocked.Exchange(ref _historyCancellation, null));
        }
    }

    internal sealed class AiSlashCommandItemVM : BaseViewModel
    {
        private bool _isSelected;

        public AiSlashCommandItemVM(SlashCommandDefinition command, string title, string currentStatus, Geometry iconGeometry, bool isAvailable, string unavailableReason)
        {
            Command = command;
            Title = title;
            CurrentStatus = currentStatus ?? "";
            IconGeometry = iconGeometry;
            IsAvailable = isAvailable;
            UnavailableReason = unavailableReason;
        }

        public SlashCommandDefinition Command { get; private set; }
        public string Title { get; private set; }
        public string CurrentStatus { get; private set; }
        public Geometry IconGeometry { get; private set; }
        public bool IsAvailable { get; private set; }
        public string UnavailableReason { get; private set; }
        public double Opacity { get { return IsAvailable ? 1 : 0.55; } }
        public bool IsSelected { get { return _isSelected; } set { _isSelected = value; OnPropertyChanged("IsSelected"); } }
    }

    internal sealed class AiChatMessageVM : BaseViewModel
    {
        private string _markdown;
        private string _title;
        private string _details;
        private string _status;
        private FlowDocument _document;
        private FlowDocument _detailsDocument;
        private bool _isWaiting;
        private bool _isWorkSummary;
        private bool _isProcessNarrative;
        private AiTurnWorkGroupVM _workGroup;
        private Geometry _iconGeometry;

        private AiChatMessageVM(string markdown, bool isUser, string itemId, CodexItemKind kind)
        {
            _markdown = markdown ?? "";
            IsUser = isUser;
            ItemId = itemId;
            Kind = kind;
            _document = AiMarkdownRenderer.CreateDocument(_markdown, isUser);
            _detailsDocument = AiMarkdownRenderer.CreateDocument("", false);
            _iconGeometry = IconFor(kind, null);
        }

        public string ItemId { get; private set; }
        public CodexItemKind Kind { get; private set; }
        public string Markdown { get { return _markdown; } }
        public string Title { get { return _title; } }
        public string Details { get { return _details; } }
        public string Status { get { return _status; } }
        public bool IsUser { get; private set; }
        public bool IsCard { get { return Kind != CodexItemKind.UserMessage && Kind != CodexItemKind.AgentMessage; } }
        public FlowDocument Document { get { return _document; } }
        public FlowDocument DetailsDocument { get { return _detailsDocument; } }
        public Geometry IconGeometry { get { return _iconGeometry; } }
        public AiTurnWorkGroupVM WorkGroup { get { return _workGroup; } }
        public Visibility UserVisibility { get { return IsUser && !_isWaiting && !_isWorkSummary ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility AssistantVisibility
        {
            get
            {
                if (IsUser || IsCard || _isWaiting || _isWorkSummary)
                    return Visibility.Collapsed;
                return _isProcessNarrative && _workGroup != null
                    ? _workGroup.ProcessVisibility
                    : Visibility.Visible;
            }
        }
        public Visibility WaitingVisibility { get { return _isWaiting ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility WorkSummaryVisibility { get { return _isWorkSummary && _workGroup != null ? _workGroup.SummaryVisibility : Visibility.Collapsed; } }
        public Visibility CompactionVisibility { get { return !_isWaiting && !_isWorkSummary && Kind == CodexItemKind.ContextCompaction ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility CardVisibility
        {
            get
            {
                if (_isWaiting || _isWorkSummary || !IsProcessKind(Kind)) return Visibility.Collapsed;
                return _workGroup == null ? Visibility.Visible : _workGroup.ProcessVisibility;
            }
        }

        public void AssignWorkGroup(AiTurnWorkGroupVM group, bool hideAsProcessNarrative = false)
        {
            if (ReferenceEquals(_workGroup, group))
            {
                if (_isProcessNarrative == hideAsProcessNarrative)
                    return;
                _isProcessNarrative = hideAsProcessNarrative;
                OnPropertyChanged("AssistantVisibility");
                return;
            }
            if (_workGroup != null) _workGroup.PropertyChanged -= WorkGroup_PropertyChanged;
            _workGroup = group;
            _isProcessNarrative = hideAsProcessNarrative;
            if (_workGroup != null) _workGroup.PropertyChanged += WorkGroup_PropertyChanged;
            OnPropertyChanged("WorkGroup");
            OnPropertyChanged("CardVisibility");
            OnPropertyChanged("WorkSummaryVisibility");
            OnPropertyChanged("AssistantVisibility");
        }

        public void RevealAsFinalResponse()
        {
            if (_isProcessNarrative && _workGroup != null)
                _workGroup.RemoveNarrativeItem();
            AssignWorkGroup(null, false);
        }

        private void WorkGroup_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged("CardVisibility");
            OnPropertyChanged("WorkSummaryVisibility");
            OnPropertyChanged("AssistantVisibility");
        }

        public void Append(string delta)
        {
            if (string.IsNullOrEmpty(delta))
                return;
            _markdown += delta;
            _document = AiMarkdownRenderer.CreateDocument(_markdown, IsUser);
            OnPropertyChanged("Markdown");
            OnPropertyChanged("Document");
        }

        public void Update(CodexThreadItem item)
        {
            if (item == null)
                return;
            Kind = item.Kind;
            _title = ItemTitle(item);
            _details = BuildDetails(item);
            _status = BuildStatus(item);
            _detailsDocument = AiMarkdownRenderer.CreateDocument(_details, false);
            _iconGeometry = IconFor(item.Kind, item.Title);
            if (!string.IsNullOrWhiteSpace(item.Text) && !IsCard)
            {
                _markdown = item.Text;
                _document = AiMarkdownRenderer.CreateDocument(_markdown, IsUser);
            }
            OnPropertyChanged("Kind");
            OnPropertyChanged("Title");
            OnPropertyChanged("Details");
            OnPropertyChanged("DetailsDocument");
            OnPropertyChanged("IconGeometry");
            OnPropertyChanged("Status");
            OnPropertyChanged("Document");
            OnPropertyChanged("CardVisibility");
            OnPropertyChanged("AssistantVisibility");
            OnPropertyChanged("CompactionVisibility");
        }

        public static AiChatMessageVM Create(string markdown, bool isUser) { return new AiChatMessageVM(markdown, isUser, null, isUser ? CodexItemKind.UserMessage : CodexItemKind.AgentMessage); }
        public static AiChatMessageVM CreateUser(string markdown) { return Create(markdown, true); }
        public static AiChatMessageVM CreateAssistant(string markdown) { return Create(markdown, false); }
        public static AiChatMessageVM CreateStreaming(string itemId) { return new AiChatMessageVM("", false, itemId, CodexItemKind.AgentMessage); }
        public static AiChatMessageVM CreateWaiting()
        {
            var result = new AiChatMessageVM("", false, "waiting:" + Guid.NewGuid().ToString("N"), CodexItemKind.Unknown);
            result._isWaiting = true;
            return result;
        }
        public static AiChatMessageVM CreateWorkSummary(AiTurnWorkGroupVM group)
        {
            var result = new AiChatMessageVM("", false, "work:" + (group == null ? Guid.NewGuid().ToString("N") : group.TurnId), CodexItemKind.Unknown);
            result._isWorkSummary = true;
            result.AssignWorkGroup(group);
            return result;
        }
        public static AiChatMessageVM CreateError(string message)
        {
            var result = new AiChatMessageVM("", false, null, CodexItemKind.Unknown);
            result._title = AiChatPanelVM.Localize("AiTimelineError", "Error");
            result._details = message ?? "";
            result.Kind = CodexItemKind.CommandExecution;
            result._detailsDocument = AiMarkdownRenderer.CreateDocument(result._details, false);
            result._iconGeometry = AiIconGeometry.SystemTool;
            return result;
        }
        public static AiChatMessageVM CreateItem(CodexThreadItem item) { return CreateItem(item, item == null ? null : item.Id); }
        public static AiChatMessageVM CreateItem(CodexThreadItem item, string itemId)
        {
            var isUser = item != null && item.Kind == CodexItemKind.UserMessage;
            var result = new AiChatMessageVM(item == null ? "" : item.Text, isUser, itemId, item == null ? CodexItemKind.Unknown : item.Kind);
            result.Update(item);
            return result;
        }

        public static bool IsProcessKind(CodexItemKind kind)
        {
            return kind != CodexItemKind.Unknown && kind != CodexItemKind.UserMessage && kind != CodexItemKind.AgentMessage && kind != CodexItemKind.ContextCompaction;
        }

        private static string ItemTitle(CodexThreadItem item)
        {
            var kind = item == null ? CodexItemKind.Unknown : item.Kind;
            var rawTitle = item == null ? null : item.Title;
            var normalized = (rawTitle ?? "").Trim().ToLowerInvariant();
            if (normalized.EndsWith("docs_search") || normalized == "docs_search") return AiChatPanelVM.Localize("AiToolDocsSearch", "Docs search");
            if (normalized.EndsWith("docs_read") || normalized == "docs_read") return AiChatPanelVM.Localize("AiToolDocsRead", "Docs read");
            if (normalized.EndsWith("workspace_context") || normalized.EndsWith("robot_context")) return AiChatPanelVM.Localize("AiToolContextGet", "Context get");
            switch (kind)
            {
                case CodexItemKind.Reasoning: return AiChatPanelVM.Localize("AiTimelineReasoning", "Reasoning");
                case CodexItemKind.Plan: return AiChatPanelVM.Localize("AiTimelinePlan", "Plan");
                case CodexItemKind.CommandExecution: return AiChatPanelVM.Localize("AiTimelineCommand", "Command");
                case CodexItemKind.FileChange: return AiChatPanelVM.Localize("AiTimelineFileChange", "File changes");
                case CodexItemKind.McpToolCall: return AiChatPanelVM.Localize("AiTimelineMcp", "MCP tool");
                case CodexItemKind.DynamicToolCall: return AiChatPanelVM.Localize("AiTimelineTool", "CLEV3R tool");
                case CodexItemKind.ContextCompaction: return AiChatPanelVM.Localize("AiTimelineCompaction", "Context compaction");
                default: return Humanize(rawTitle, kind.ToString());
            }
        }

        private static string BuildDetails(CodexThreadItem item)
        {
            if (item == null) return "";
            if (item.Kind == CodexItemKind.Reasoning || item.Kind == CodexItemKind.Plan)
                return Limit(item.Text, 4000);
            if (item.Kind == CodexItemKind.ContextCompaction)
                return "";

            try
            {
                using (var document = JsonDocument.Parse(item.RawJson ?? "{}"))
                {
                    var root = document.RootElement;
                    var lines = new List<string>();
                    if (item.Kind == CodexItemKind.CommandExecution)
                    {
                        AddJsonLine(lines, AiChatPanelVM.Localize("AiDetailCommand", "Command"), root, "command");
                        AddJsonLine(lines, AiChatPanelVM.Localize("AiDetailFolder", "Folder"), root, "cwd");
                        AddJsonLine(lines, AiChatPanelVM.Localize("AiDetailExitCode", "Exit code"), root, "exitCode");
                        AddJsonLine(lines, AiChatPanelVM.Localize("AiDetailDuration", "Duration"), root, "durationMs", " ms");
                        AddJsonLine(lines, AiChatPanelVM.Localize("AiDetailOutput", "Output"), root, "aggregatedOutput");
                    }
                    else if (item.Kind == CodexItemKind.FileChange)
                    {
                        JsonElement changes;
                        if (root.TryGetProperty("changes", out changes) && changes.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var change in changes.EnumerateArray().Take(20))
                            {
                                var path = JsonString(change, "path");
                                var changeKind = JsonString(change, "kind");
                                if (!string.IsNullOrWhiteSpace(path)) lines.Add("• " + path + (string.IsNullOrWhiteSpace(changeKind) ? "" : " — " + changeKind));
                            }
                        }
                    }
                    else if (item.Kind == CodexItemKind.DynamicToolCall || item.Kind == CodexItemKind.McpToolCall)
                    {
                        AddJsonLine(lines, AiChatPanelVM.Localize("AiDetailServer", "Server"), root, "server");
                        AddJsonLine(lines, AiChatPanelVM.Localize("AiDetailTool", "Tool"), root, "tool");
                        var request = FindToolArgument(root, "query", "id", "path", "entryPath", "localPath", "destinationPath", "buildId", "name");
                        if (!string.IsNullOrWhiteSpace(request)) lines.Add(AiChatPanelVM.Localize("AiDetailRequest", "Request") + ": " + Limit(request, 500));
                        var topic = ExtractToolDataTitle(root);
                        if (!string.IsNullOrWhiteSpace(topic)) lines.Add(AiChatPanelVM.Localize("AiDetailTopic", "Topic") + ": " + Limit(topic, 500));
                        AddJsonLine(lines, AiChatPanelVM.Localize("AiDetailStatus", "Status"), root, "status");
                        var resultMessage = JsonString(root, "resultMessage") ??
                            FindNestedString(root, "result", "message") ??
                            ExtractToolResultMessage(root) ??
                            FindNestedString(root, "result", "code");
                        if (!string.IsNullOrWhiteSpace(resultMessage)) lines.Add(AiChatPanelVM.Localize("AiDetailResult", "Result") + ": " + Limit(resultMessage, 1000));
                        var error = JsonString(root, "error");
                        if (!string.IsNullOrWhiteSpace(error)) lines.Add(AiChatPanelVM.Localize("AiTimelineError", "Error") + ": " + Limit(error, 1000));
                    }
                    if (lines.Count == 0 && !string.IsNullOrWhiteSpace(item.Text)) lines.Add(Limit(item.Text, 3000));
                    return string.Join("\n", lines);
                }
            }
            catch
            {
                return Limit(item.Text, 3000);
            }
        }

        private static string BuildStatus(CodexThreadItem item)
        {
            if (item == null)
                return "";
            try
            {
                using (var document = JsonDocument.Parse(item.RawJson ?? "{}"))
                {
                    var root = document.RootElement;
                    if (item.Kind == CodexItemKind.CommandExecution)
                    {
                        var command = JsonString(root, "command");
                        return string.IsNullOrWhiteSpace(command) ? item.Status ?? "" : Limit(command, 140);
                    }
                    if (item.Kind != CodexItemKind.DynamicToolCall && item.Kind != CodexItemKind.McpToolCall)
                        return item.Status ?? "";

                    var tool = JsonString(root, "tool") ?? item.Title ?? "";
                    var normalized = tool.Trim().ToLowerInvariant();
                    var argument = FindToolArgument(root, "query", "id", "path", "entryPath", "localPath", "destinationPath", "buildId", "name");
                    if (normalized.EndsWith("docs_read"))
                    {
                        var topic = ExtractToolDataTitle(root);
                        return Limit(topic ?? argument ?? item.Status ?? "", 140);
                    }
                    if (normalized.EndsWith("docs_search"))
                        return Limit(argument ?? item.Status ?? "", 140);

                    var commandName = Humanize(tool, "");
                    if (string.IsNullOrWhiteSpace(commandName))
                        commandName = item.Status ?? "";
                    return string.IsNullOrWhiteSpace(argument)
                        ? Limit(commandName, 140)
                        : Limit(commandName + ": " + argument, 140);
                }
            }
            catch
            {
                return item.Status ?? "";
            }
        }

        private static string FindToolArgument(JsonElement root, params string[] names)
        {
            foreach (var name in names ?? new string[0])
            {
                var value = FindNestedString(root, "arguments", name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }

        private static string ExtractToolResultMessage(JsonElement root)
        {
            var envelope = FindToolResultEnvelope(root);
            if (envelope == null)
                return null;
            using (envelope)
                return JsonString(envelope.RootElement, "message");
        }

        private static string ExtractToolDataTitle(JsonElement root)
        {
            var envelope = FindToolResultEnvelope(root);
            if (envelope == null)
                return JsonString(root, "resultDataTitle");
            using (envelope)
            {
                var dataJson = JsonString(envelope.RootElement, "dataJson");
                if (string.IsNullOrWhiteSpace(dataJson))
                    return null;
                try
                {
                    using (var data = JsonDocument.Parse(dataJson))
                    {
                        if (data.RootElement.ValueKind == JsonValueKind.Object)
                            return JsonString(data.RootElement, "title");
                        if (data.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entry in data.RootElement.EnumerateArray())
                            {
                                var title = JsonString(entry, "title");
                                if (!string.IsNullOrWhiteSpace(title))
                                    return title;
                            }
                        }
                    }
                }
                catch { }
                return null;
            }
        }

        private static JsonDocument FindToolResultEnvelope(JsonElement root)
        {
            var text = FindContentItemText(root, 0);
            if (string.IsNullOrWhiteSpace(text))
                return null;
            try { return JsonDocument.Parse(text); }
            catch { return null; }
        }

        private static string FindContentItemText(JsonElement value, int depth)
        {
            if (depth > 6)
                return null;
            if (value.ValueKind == JsonValueKind.Object)
            {
                JsonElement items;
                if (value.TryGetProperty("contentItems", out items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var text = JsonString(item, "text");
                        if (!string.IsNullOrWhiteSpace(text))
                            return text;
                    }
                }
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Array && property.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    var text = FindContentItemText(property.Value, depth + 1);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    var text = FindContentItemText(item, depth + 1);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
            return null;
        }

        private static void AddJsonLine(ICollection<string> lines, string label, JsonElement root, string property, string suffix = "")
        {
            JsonElement value;
            if (!root.TryGetProperty(property, out value) || value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined) return;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            if (!string.IsNullOrWhiteSpace(text)) lines.Add(label + ": " + Limit(text, 2000) + suffix);
        }

        private static string FindNestedString(JsonElement root, string parentName, string childName)
        {
            JsonElement parent;
            if (!root.TryGetProperty(parentName, out parent)) return null;
            if (parent.ValueKind == JsonValueKind.String)
            {
                var text = parent.GetString();
                if (string.IsNullOrWhiteSpace(text)) return null;
                try
                {
                    using (var nested = JsonDocument.Parse(text)) return JsonString(nested.RootElement, childName);
                }
                catch { return childName == "query" ? text : null; }
            }
            return parent.ValueKind == JsonValueKind.Object ? JsonString(parent, childName) : null;
        }

        private static string JsonString(JsonElement root, string property)
        {
            JsonElement value;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Null ? null : value.GetRawText();
        }

        private static string Limit(string value, int max)
        {
            value = value ?? "";
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }

        private static string Humanize(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var unqualified = value.Trim();
            var separator = unqualified.LastIndexOf('.');
            if (separator >= 0 && separator + 1 < unqualified.Length)
                unqualified = unqualified.Substring(separator + 1);
            var text = Regex.Replace(unqualified.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ").Trim();
            return text.Length == 0 ? fallback : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        private static Geometry IconFor(CodexItemKind kind, string title)
        {
            var normalized = (title ?? "").ToLowerInvariant();
            if (kind == CodexItemKind.Reasoning) return AiIconGeometry.Reasoning;
            if (kind == CodexItemKind.FileChange) return AiIconGeometry.File;
            if (kind == CodexItemKind.McpToolCall) return AiIconGeometry.Web;
            if (normalized.Contains("docs_")) return AiIconGeometry.Documentation;
            if (normalized.Contains("context")) return AiIconGeometry.Context;
            return AiIconGeometry.SystemTool;
        }
    }

    internal sealed class AiTurnWorkGroupVM : BaseViewModel
    {
        private readonly DateTime _startedAtUtc;
        private bool _isCompleted;
        private bool _isExpanded;
        private int _processItemCount;
        private int _narrativeItemCount;
        private long _durationMs;

        public AiTurnWorkGroupVM(string turnId, DateTime startedAtUtc)
        {
            TurnId = turnId ?? "";
            _startedAtUtc = startedAtUtc;
        }

        public string TurnId { get; private set; }
        public bool IsCompleted { get { return _isCompleted; } }
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { _isExpanded = value; OnPropertyChanged("IsExpanded"); OnPropertyChanged("ProcessVisibility"); }
        }
        public string SummaryText { get { return AiChatPanelVM.Localize("AiWorked", "Worked") + " " + FormatDuration(_durationMs); } }
        public Visibility SummaryVisibility { get { return _isCompleted && (_processItemCount > 0 || _narrativeItemCount > 0) ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility ProcessVisibility
        {
            get
            {
                return !_isCompleted || IsExpanded
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        public void AddProcessItem()
        {
            _processItemCount++;
            OnPropertyChanged("SummaryVisibility");
        }

        public void AddNarrativeItem()
        {
            _narrativeItemCount++;
            OnPropertyChanged("SummaryVisibility");
        }

        public void RemoveNarrativeItem()
        {
            if (_narrativeItemCount > 0)
                _narrativeItemCount--;
            OnPropertyChanged("SummaryVisibility");
        }

        public void Complete(long? durationMs)
        {
            _durationMs = durationMs ?? Math.Max(0, (long)(DateTime.UtcNow - _startedAtUtc).TotalMilliseconds);
            _isCompleted = true;
            _isExpanded = false;
            OnPropertyChanged("SummaryText");
            OnPropertyChanged("SummaryVisibility");
            OnPropertyChanged("ProcessVisibility");
            OnPropertyChanged("IsExpanded");
        }

        private static string FormatDuration(long durationMs)
        {
            var totalSeconds = Math.Max(0, durationMs / 1000);
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return minutes > 0 ? minutes + "m " + seconds + "s" : seconds + "s";
        }
    }

    internal static class AiIconGeometry
    {
        public static readonly Geometry Reasoning = Create("M12,18 L12,5 M15,13 C13,12 12,10 12,9 C12,11 11,12 9,13 M17.6,6.5 C20,7 21,9 20.5,11 M18,18 C21,17 21,13 20,11 M6,18 C3,17 3,13 4,11 M6.4,6.5 C4,7 3,9 3.5,11");
        public static readonly Geometry Documentation = Create("M2.6,9.1 L11.2,5.2 C11.7,5 12.3,5 12.8,5.2 L21.4,9.1 C22.2,9.5 22.2,10.5 21.4,10.9 L12.8,14.8 C12.3,15 11.7,15 11.2,14.8 L2.6,10.9 C1.8,10.5 1.8,9.5 2.6,9.1 M22,10 L22,16 M6,12.5 L6,16 C6,20 18,20 18,16 L18,12.5");
        public static readonly Geometry Context = Create("M18,16 L22,12 18,8 M6,8 L2,12 6,16 M14.5,4 L9.5,20");
        public static readonly Geometry File = Create("M6,22 C5,22 4,21 4,20 L4,4 C4,3 5,2 6,2 L14,2 L20,8 L20,20 C20,21 19,22 18,22 Z M14,2 L14,7 C14,8 15,8 16,8 L20,8");
        public static readonly Geometry Web = Create("M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M2,12 L22,12 M12,2 C7,7 7,17 12,22 M12,2 C17,7 17,17 12,22");
        public static readonly Geometry SystemTool = Create("M10,5 L3,5 M14,3 L14,7 M21,5 L14,5 M8,10 L8,14 M3,12 L8,12 M12,12 L21,12 M12,19 L3,19 M16,17 L16,21 M21,19 L16,19");
        public static readonly Geometry Model = Create("M12,2 L16,6 12,10 8,6 Z M5,9 L9,13 5,17 1,13 Z M19,9 L23,13 19,17 15,13 Z M12,14 L16,18 12,22 8,18 Z");
        public static readonly Geometry Speed = Create("M3.3,19 A10,10 0 1 1 20.7,19 M12,14 L16,10");
        public static readonly Geometry Goal = Create("M12,13 L12,2 20,6 12,10 M20.5,10.2 A9,9 0 1 1 8,4.9 M8,10 A5,5 0 1 0 17,12");
        public static readonly Geometry Compact = Create("M11,21.7 C11.6,22 12.4,22 13,21.7 L20,17.7 C20.6,17.4 21,16.7 21,16 L21,8 C21,7.3 20.6,6.6 20,6.3 L13,2.3 C12.4,2 11.6,2 11,2.3 L4,6.3 C3.4,6.6 3,7.3 3,8 L3,16 C3,16.7 3.4,17.4 4,17.7 Z M12,22 L12,12 M3.3,7 L12,12 20.7,7 M7.5,4.3 L16.5,9.4");

        private static Geometry Create(string data)
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }
    }

    internal sealed class AiServiceTierChoiceVM
    {
        public AiServiceTierChoiceVM(string id, string displayName, string description)
        {
            Id = string.IsNullOrWhiteSpace(id) ? null : id;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? (Id ?? "Default") : displayName;
            Description = description ?? "";
        }

        public string Id { get; private set; }
        public string DisplayName { get; private set; }
        public string Description { get; private set; }
        public override string ToString() { return DisplayName; }
    }

    internal sealed class AiAttachmentVM
    {
        public AiAttachmentVM(string name, string kind, string source) { Name = name; Kind = kind; Source = source; }
        public string Name { get; private set; }
        public string Kind { get; private set; }
        public string Source { get; private set; }
    }

    internal sealed class AiChatHistoryItemVM : BaseViewModel
    {
        private string _editableTitle;
        private string _title;

        public AiChatHistoryItemVM(CodexThreadSummary thread)
        {
            Id = thread.Id;
            _title = string.IsNullOrWhiteSpace(thread.Name) ? AiChatPanelVM.Localize("AiUntitledChat", "Untitled chat") : thread.Name;
            _editableTitle = _title;
            Subtitle = thread.Preview ?? "";
            State = thread.State.ToString();
            ActionsVisibility = Visibility.Visible;
        }

        private AiChatHistoryItemVM(string status)
        {
            Id = "";
            _title = status ?? "";
            _editableTitle = _title;
            Subtitle = "";
            State = "";
            ActionsVisibility = Visibility.Collapsed;
        }

        public string Id { get; private set; }
        public string Title { get { return _title; } }
        public string Subtitle { get; private set; }
        public string State { get; private set; }
        public Visibility ActionsVisibility { get; private set; }
        public string EditableTitle { get { return _editableTitle; } set { _editableTitle = value ?? ""; OnPropertyChanged("EditableTitle"); } }
        public void CommitTitle() { _title = EditableTitle.Trim(); OnPropertyChanged("Title"); }
        public static AiChatHistoryItemVM Status(string message) { return new AiChatHistoryItemVM(message); }
    }

    internal static class AiMarkdownRenderer
    {
        private static readonly Regex OrderedListRegex = new Regex(@"^\d+[\.)]\s+(.+)$", RegexOptions.Compiled);

        public static FlowDocument CreateDocument(string markdown, bool compact)
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = compact ? 13 : 14,
                Foreground = BrushFrom("#414659"),
                LineHeight = compact ? 18 : 20,
                ColumnWidth = 999999
            };
            var inCode = false;
            Paragraph code = null;
            foreach (var raw in (markdown ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw ?? "";
                if (line.TrimStart().StartsWith("```"))
                {
                    inCode = !inCode;
                    if (inCode)
                    {
                        code = new Paragraph { Margin = new Thickness(0, 5, 0, 5), Padding = new Thickness(7, 5, 7, 5), Background = BrushFrom("#F1F3F7"), FontFamily = new FontFamily("JetBrains Mono, Consolas") };
                        document.Blocks.Add(code);
                    }
                    continue;
                }
                if (inCode)
                {
                    code.Inlines.Add(new Run(line));
                    code.Inlines.Add(new LineBreak());
                    continue;
                }
                document.Blocks.Add(CreateParagraph(line, compact));
            }
            return document;
        }

        private static Paragraph CreateParagraph(string line, bool compact)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
            var text = line.TrimStart();
            if (text.StartsWith("## ")) { paragraph.FontSize = compact ? 14 : 16; paragraph.FontWeight = FontWeights.Bold; text = text.Substring(3); }
            else if (text.StartsWith("# ")) { paragraph.FontSize = compact ? 15 : 17; paragraph.FontWeight = FontWeights.Bold; text = text.Substring(2); }
            else if (text.StartsWith("> ")) { paragraph.Background = BrushFrom("#F6F7FA"); paragraph.Padding = new Thickness(7, 4, 7, 4); text = text.Substring(2); }
            else if (text.StartsWith("- ") || text.StartsWith("* ")) text = "• " + text.Substring(2);
            else
            {
                var ordered = OrderedListRegex.Match(text);
                if (ordered.Success) text = line.Substring(0, line.IndexOf(ordered.Groups[1].Value, StringComparison.Ordinal)) + ordered.Groups[1].Value;
            }
            AddInlineMarkdown(paragraph.Inlines, text);
            return paragraph;
        }

        private static void AddInlineMarkdown(InlineCollection inlines, string text)
        {
            var index = 0;
            while (index < text.Length)
            {
                var linkEnd = 0;
                string linkLabel;
                string linkTarget;
                if (TryReadMarkdownLink(text, index, out linkLabel, out linkTarget, out linkEnd))
                {
                    AddLink(inlines, linkLabel, linkTarget);
                    index = linkEnd;
                    continue;
                }

                string marker = null;
                Action<Span> configure = null;
                if (text.Substring(index).StartsWith("**")) { marker = "**"; configure = span => span.FontWeight = FontWeights.Bold; }
                else if (text[index] == '`') { marker = "`"; configure = span => { span.FontFamily = new FontFamily("JetBrains Mono, Consolas"); span.Background = BrushFrom("#EEF1F6"); }; }
                else if (text[index] == '*' || text[index] == '_') { marker = text[index].ToString(); configure = span => span.FontStyle = FontStyles.Italic; }
                if (marker != null)
                {
                    var end = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
                    if (end > index)
                    {
                        var span = new Span(new Run(text.Substring(index + marker.Length, end - index - marker.Length)));
                        configure(span);
                        inlines.Add(span);
                        index = end + marker.Length;
                        continue;
                    }
                }
                var next = text.Length;
                foreach (var candidate in new[] { "**", "`", "*", "_" })
                {
                    var markerIndex = text.IndexOf(candidate, index + 1, StringComparison.Ordinal);
                    if (markerIndex >= 0 && markerIndex < next) next = markerIndex;
                }
                var linkIndex = FindNextMarkdownLinkStart(text, index + 1);
                if (linkIndex >= 0 && linkIndex < next) next = linkIndex;
                inlines.Add(new Run(text.Substring(index, next - index)));
                index = next;
            }
        }

        private static int FindNextMarkdownLinkStart(string text, int start)
        {
            var candidate = text.IndexOf('[', start);
            while (candidate >= 0)
            {
                string label;
                string target;
                int end;
                if (TryReadMarkdownLink(text, candidate, out label, out target, out end))
                    return candidate;
                candidate = text.IndexOf('[', candidate + 1);
            }
            return -1;
        }

        private static bool TryReadMarkdownLink(string text, int start, out string label, out string target, out int end)
        {
            label = null;
            target = null;
            end = start;
            if (string.IsNullOrEmpty(text) || start >= text.Length || text[start] != '[')
                return false;

            var closeLabel = text.IndexOf("](", start + 1, StringComparison.Ordinal);
            if (closeLabel <= start + 1)
                return false;
            var closeTarget = text.IndexOf(')', closeLabel + 2);
            if (closeTarget <= closeLabel + 2)
                return false;

            label = text.Substring(start + 1, closeLabel - start - 1);
            target = text.Substring(closeLabel + 2, closeTarget - closeLabel - 2).Trim();
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(target))
                return false;

            end = closeTarget + 1;
            return true;
        }

        private static void AddLink(InlineCollection inlines, string label, string target)
        {
            Uri uri;
            if (Path.IsPathRooted(target))
            {
                try { uri = new Uri(Path.GetFullPath(target)); }
                catch
                {
                    inlines.Add(new Run(label));
                    return;
                }
            }
            else if (!Uri.TryCreate(target, UriKind.Absolute, out uri) ||
                !(uri.IsFile || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                inlines.Add(new Run(label));
                return;
            }

            var link = new Hyperlink(new Run(label))
            {
                NavigateUri = uri,
                ToolTip = target,
                Foreground = BrushFrom("#0A66C2"),
                TextDecorations = TextDecorations.Underline
            };
            link.RequestNavigate += MarkdownLink_RequestNavigate;
            inlines.Add(link);
        }

        private static void MarkdownLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            e.Handled = true;
            if (e.Uri == null ||
                !(e.Uri.IsFile || string.Equals(e.Uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(e.Uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                return;
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.IsFile ? e.Uri.LocalPath : e.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private static Brush BrushFrom(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
    }
}
