using Clev3r.AgentCore;
using Clever.Model.Agent;
using Clever.Model.Utils;
using Clever.ViewModel.BaseVM;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
                OnPropertyChanged("SelectedSpeed");
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
            IsHistoryOpen = false;
            IsBotWorking = false;
            Messages.Clear();
            Attachments.Clear();
            InputText = "";
            OnPropertyChanged("AttachmentVisibility");
            OnPropertyChanged("LoadMoreTurnsVisibility");
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
            ReplaceCancellation(ref _historyCancellation);
            _nextHistoryCursor = null;
            OnPropertyChanged("LoadMoreVisibility");
            await LoadHistoryPageAsync(null, true, version, _historyCancellation.Token);
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
                if (result.Data.Items.Count > 0)
                    foreach (var threadItem in result.Data.Items)
                        Messages.Add(AiChatMessageVM.CreateItem(threadItem));
                else
                    foreach (var message in result.Data.Messages)
                        Messages.Add(AiChatMessageVM.Create(message.Markdown, message.IsUser));
                _nextTurnsCursor = result.Data.NextTurnsCursor;
                OnPropertyChanged("LoadMoreTurnsVisibility");
                RefreshBackendState();
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
                    foreach (var threadItem in result.Data.Items)
                        older.Add(AiChatMessageVM.CreateItem(threadItem));
                else
                    foreach (var message in result.Data.Messages)
                        older.Add(AiChatMessageVM.Create(message.Markdown, message.IsUser));
                for (var i = older.Count - 1; i >= 0; i--)
                    Messages.Insert(0, older[i]);
                _nextTurnsCursor = result.Data.NextTurnsCursor;
                OnPropertyChanged("LoadMoreTurnsVisibility");
            });
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

        private async void SendAsync()
        {
            if (!CanSend || _turnStartPending)
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

            ReplaceCancellation(ref _turnCancellation);
            SetTurnStartPending(true);
            try
            {
                if (_services.WorkspaceContext == null)
                {
                    ShowBackendMessage(Localize("AiFoundationUnavailable", "The CLEV3R agent context is not initialized."));
                    return;
                }

                var context = await _services.WorkspaceContext.SaveAllAndCaptureAsync(_turnCancellation.Token);
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
                    }, _turnCancellation.Token);
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
                var turn = await _backend.StartTurnAsync(request, _turnCancellation.Token);
                if (!turn.Success || turn.Data == null)
                    ShowBackendMessage(BackendMessage(turn.Message));
                else
                {
                    _currentThreadId = turn.Data.ThreadId ?? _currentThreadId;
                    _activeTurnId = turn.Data.TurnId;
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
                    "/" + match.Command.Name,
                    Localize(match.Command.DescriptionKey, match.Command.Name),
                    match.IsAvailable,
                    match.IsAvailable ? "" : Localize("AiCapabilityUnavailableShort", "Unavailable until a compatible Codex app-server is connected.")));
            }
            _selectedSlashIndex = 0;
            UpdateSlashSelection();
            IsSlashOpen = SlashItems.Count > 0;
        }

        private void UpdateSlashSelection()
        {
            for (var i = 0; i < SlashItems.Count; i++)
                SlashItems[i].IsSelected = i == _selectedSlashIndex;
        }

        private async void RefreshBackendState()
        {
            await LoadModelsAsync();
            var result = await _backend.GetStatusAsync(_currentThreadId, CancellationToken.None);
            OnUi(() =>
            {
                if (result.Success && result.Data != null && !string.IsNullOrWhiteSpace(_currentThreadId))
                    ApplyBackendSettings(result.Data.Settings);
                _statusTooltip = result.Success && result.Data != null ? FormatStatus(result.Data) : BackendMessage(result.Message);
                OnPropertyChanged("ContextTooltip");
                NotifySelectorState();
                RefreshSlashItems();
            });
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
                        SelectedModel = null;
                        SelectedReasoning = null;
                        SelectedSpeed = null;
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
                        SelectedModel = Models.FirstOrDefault();
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
            var selectedTierId = SelectedSpeed == null ? null : SelectedSpeed.Id;
            _suppressSettingsUpdate = true;
            try
            {
                ReasoningModes.Clear();
                SpeedTiers.Clear();
                if (model != null)
                {
                    foreach (var value in model.SupportedReasoningEfforts) ReasoningModes.Add(value);
                    SpeedTiers.Add(new AiServiceTierChoiceVM(null, Localize("AiSpeedDefault", "Default"), ""));
                    foreach (var value in model.ServiceTiers.Where(item => !item.IsDefault && !string.IsNullOrWhiteSpace(item.Id)))
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
                if (!string.IsNullOrWhiteSpace(settings.Model))
                {
                    if (!Models.Any(item => string.Equals(item, settings.Model, StringComparison.OrdinalIgnoreCase)))
                        Models.Add(settings.Model);
                    SelectedModel = settings.Model;
                }
                RefreshModelDependentOptions();

                if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort))
                {
                    if (!ReasoningModes.Any(item => string.Equals(item, settings.ReasoningEffort, StringComparison.OrdinalIgnoreCase)))
                        ReasoningModes.Add(settings.ReasoningEffort);
                    SelectedReasoning = settings.ReasoningEffort;
                }

                var selectedTier = SpeedTiers.FirstOrDefault(item =>
                    string.Equals(item.Id, settings.ServiceTier, StringComparison.OrdinalIgnoreCase));
                if (selectedTier == null && !string.IsNullOrWhiteSpace(settings.ServiceTier))
                {
                    selectedTier = new AiServiceTierChoiceVM(settings.ServiceTier, settings.ServiceTier, "");
                    SpeedTiers.Add(selectedTier);
                }
                SelectedSpeed = selectedTier ?? SpeedTiers.FirstOrDefault(item => item.Id == null);
            }
            finally { _suppressSettingsUpdate = previousSuppression; }
            NotifySelectorState();
        }

        private async void RequestSettingsUpdate()
        {
            if (!_suppressSettingsUpdate && !string.IsNullOrWhiteSpace(_currentThreadId))
                await UpdateSettingsAsync();
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
                _statusTooltip = result.Success && result.Data != null ? FormatStatus(result.Data) : BackendMessage(result.Message);
                OnPropertyChanged("ContextTooltip");
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
                case CodexBackendEventKind.AccountUpdated:
                case CodexBackendEventKind.RateLimitsUpdated:
                case CodexBackendEventKind.TokenUsageUpdated:
                    RefreshBackendState();
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
                    }
                    break;
                case CodexBackendEventKind.TurnCompleted:
                    if (belongsToCurrentThread && (string.IsNullOrWhiteSpace(_activeTurnId) || string.Equals(e.TurnId, _activeTurnId, StringComparison.Ordinal)))
                    {
                        FlushAllDeltas();
                        IsBotWorking = false;
                        _activeTurnId = null;
                    }
                    break;
                case CodexBackendEventKind.ItemStarted:
                    if (belongsToCurrentThread && (e.Item == null || e.Item.Kind != CodexItemKind.UserMessage))
                        AddOrUpdateItem(e.Item, e.ItemId);
                    break;
                case CodexBackendEventKind.ItemDelta:
                    if (belongsToCurrentThread) QueueDelta(e.ItemId, e.Delta, e.Item);
                    break;
                case CodexBackendEventKind.ItemCompleted:
                    if (belongsToCurrentThread && (e.Item == null || e.Item.Kind != CodexItemKind.UserMessage))
                    {
                        FlushDelta(e.ItemId);
                        AddOrUpdateItem(e.Item, e.ItemId);
                    }
                    break;
                case CodexBackendEventKind.Error:
                    if (belongsToCurrentThread)
                        Messages.Add(AiChatMessageVM.CreateError(BackendMessage(e.Message)));
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
                Messages.Add(existing);
            }
            else
                existing.Update(item);
        }

        private void QueueDelta(string itemId, string delta, CodexThreadItem item)
        {
            itemId = !string.IsNullOrWhiteSpace(itemId) ? itemId : item == null ? null : item.Id;
            if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrEmpty(delta))
                return;
            if (FindTimelineItem(itemId) == null)
                Messages.Add(item == null ? AiChatMessageVM.CreateStreaming(itemId) : AiChatMessageVM.CreateItem(item, itemId));
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

        private string FormatStatus(CodexStatusSnapshot status)
        {
            var lines = new List<string>
            {
                Localize("AiStatusProject", "Project") + ": " + (MainWindowVM.CurrentProjectRoot ?? ""),
                Localize("AiStatusConnection", "Connection") + ": " + status.ConnectionState
            };
            var version = status.Runtime == null ? status.Version : status.Runtime.Version;
            if (!string.IsNullOrWhiteSpace(version)) lines.Add(Localize("AiStatusVersion", "Version") + ": " + version);
            if (status.Auth != null && !string.IsNullOrWhiteSpace(status.Auth.Email)) lines.Add(Localize("AiStatusAccount", "Account") + ": " + status.Auth.Email);
            if (status.Auth != null && !string.IsNullOrWhiteSpace(status.Auth.PlanType)) lines.Add(Localize("AiStatusPlan", "Plan") + ": " + status.Auth.PlanType);
            if (status.Settings != null && !string.IsNullOrWhiteSpace(status.Settings.Model)) lines.Add(ModelText + ": " + status.Settings.Model);
            if (status.Settings != null && !string.IsNullOrWhiteSpace(status.Settings.ReasoningEffort)) lines.Add(ReasoningText + ": " + status.Settings.ReasoningEffort);
            if (status.Settings != null) lines.Add(SpeedText + ": " + (string.IsNullOrWhiteSpace(status.Settings.ServiceTier) ? Localize("AiSpeedDefault", "Default") : status.Settings.ServiceTier));
            if (status.Goal != null && !string.IsNullOrWhiteSpace(status.Goal.Objective)) lines.Add(Localize("AiGoal", "Goal") + ": " + status.Goal.Objective);
            if (status.ContextUsedPercent.HasValue) lines.Add(Localize("AiStatusContext", "Context") + ": " + status.ContextUsedPercent.Value + "%");
            if (status.RateLimits != null && status.RateLimits.Primary != null) lines.Add(Localize("AiStatusPrimaryLimit", "Primary limit used") + ": " + status.RateLimits.Primary.UsedPercent + "%");
            if (_services.Ev3 != null && _services.Ev3.State != null) lines.Add("EV3: " + (_services.Ev3.State.Connected ? Localize("AiEv3Connected", "connected") : Localize("AiEv3Disconnected", "disconnected")));
            if (!string.IsNullOrWhiteSpace(status.Message)) lines.Add(status.Message);
            return string.Join("\n", lines);
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

        private void NotifySelectorState()
        {
            OnPropertyChanged("SelectorsEnabled");
            OnPropertyChanged("ModelSelectorEnabled");
            OnPropertyChanged("ReasoningSelectorEnabled");
            OnPropertyChanged("SpeedSelectorEnabled");
        }

        private void Backend_StateChanged(object sender, EventArgs e) { OnUi(RefreshBackendState); }

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

        private static void ReplaceCancellation(ref CancellationTokenSource source)
        {
            if (source != null) { source.Cancel(); source.Dispose(); }
            source = new CancellationTokenSource();
        }

        private void CancelTurn()
        {
            if (_turnCancellation != null) _turnCancellation.Cancel();
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return ""; }
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
            if (_turnCancellation != null) { _turnCancellation.Cancel(); _turnCancellation.Dispose(); }
            if (_historyCancellation != null) { _historyCancellation.Cancel(); _historyCancellation.Dispose(); }
        }
    }

    internal sealed class AiSlashCommandItemVM : BaseViewModel
    {
        private bool _isSelected;

        public AiSlashCommandItemVM(SlashCommandDefinition command, string title, string description, bool isAvailable, string unavailableReason)
        {
            Command = command;
            Title = title;
            Description = description;
            IsAvailable = isAvailable;
            UnavailableReason = unavailableReason;
        }

        public SlashCommandDefinition Command { get; private set; }
        public string Title { get; private set; }
        public string Description { get; private set; }
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

        private AiChatMessageVM(string markdown, bool isUser, string itemId, CodexItemKind kind)
        {
            _markdown = markdown ?? "";
            IsUser = isUser;
            ItemId = itemId;
            Kind = kind;
            _document = AiMarkdownRenderer.CreateDocument(_markdown, isUser);
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
        public Visibility UserVisibility { get { return IsUser ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility AssistantVisibility { get { return !IsUser && !IsCard ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility CardVisibility { get { return IsCard ? Visibility.Visible : Visibility.Collapsed; } }

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
            _title = string.IsNullOrWhiteSpace(item.Title) ? ItemTitle(item.Kind) : item.Title;
            _details = string.IsNullOrWhiteSpace(item.Details) ? item.Text : item.Details;
            _status = item.Status ?? "";
            if (!string.IsNullOrWhiteSpace(item.Text) && !IsCard)
            {
                _markdown = item.Text;
                _document = AiMarkdownRenderer.CreateDocument(_markdown, IsUser);
            }
            OnPropertyChanged("Kind");
            OnPropertyChanged("Title");
            OnPropertyChanged("Details");
            OnPropertyChanged("Status");
            OnPropertyChanged("Document");
            OnPropertyChanged("CardVisibility");
            OnPropertyChanged("AssistantVisibility");
        }

        public static AiChatMessageVM Create(string markdown, bool isUser) { return new AiChatMessageVM(markdown, isUser, null, isUser ? CodexItemKind.UserMessage : CodexItemKind.AgentMessage); }
        public static AiChatMessageVM CreateUser(string markdown) { return Create(markdown, true); }
        public static AiChatMessageVM CreateAssistant(string markdown) { return Create(markdown, false); }
        public static AiChatMessageVM CreateStreaming(string itemId) { return new AiChatMessageVM("", false, itemId, CodexItemKind.AgentMessage); }
        public static AiChatMessageVM CreateError(string message)
        {
            var result = new AiChatMessageVM("", false, null, CodexItemKind.Unknown);
            result._title = AiChatPanelVM.Localize("AiTimelineError", "Error");
            result._details = message ?? "";
            result.Kind = CodexItemKind.CommandExecution;
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

        private static string ItemTitle(CodexItemKind kind)
        {
            switch (kind)
            {
                case CodexItemKind.Reasoning: return AiChatPanelVM.Localize("AiTimelineReasoning", "Reasoning");
                case CodexItemKind.Plan: return AiChatPanelVM.Localize("AiTimelinePlan", "Plan");
                case CodexItemKind.CommandExecution: return AiChatPanelVM.Localize("AiTimelineCommand", "Command");
                case CodexItemKind.FileChange: return AiChatPanelVM.Localize("AiTimelineFileChange", "File changes");
                case CodexItemKind.McpToolCall: return AiChatPanelVM.Localize("AiTimelineMcp", "MCP tool");
                case CodexItemKind.DynamicToolCall: return AiChatPanelVM.Localize("AiTimelineTool", "CLEV3R tool");
                case CodexItemKind.ContextCompaction: return AiChatPanelVM.Localize("AiTimelineCompaction", "Context compaction");
                default: return kind.ToString();
            }
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
                inlines.Add(new Run(text.Substring(index, next - index)));
                index = next;
            }
        }

        private static Brush BrushFrom(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
    }
}
