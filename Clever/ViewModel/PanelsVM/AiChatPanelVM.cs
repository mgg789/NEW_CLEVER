using Clever.Model.Utils;
using Clever.ViewModel.BaseVM;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Clever.ViewModel.PanelsVM
{
    internal class AiChatPanelVM : BaseViewModel
    {
        private string _inputText;
        private bool _isBotWorking;
        private bool _isHistoryOpen;
        private bool _isSettingsOpen;
        private string _selectedModel;
        private string _selectedReasoning;
        private string _wheelDiameter;
        private string _wheelBaseWidth;
        private string _robotDescription;
        private string _currentSessionId;

        public AiChatPanelVM()
        {
            Models = new ObservableCollection<string> { "5.4-Mini", "5.4", "Local" };
            ReasoningModes = new ObservableCollection<string> { "Fast", "Balanced", "Deep" };
            SelectedModel = Models[0];
            SelectedReasoning = ReasoningModes[1];

            _wheelDiameter = AiAppSettingsStore.Settings.WheelDiameter;
            _wheelBaseWidth = AiAppSettingsStore.Settings.WheelBaseWidth;
            _robotDescription = AiAppSettingsStore.Settings.RobotDescription;
            if (string.IsNullOrWhiteSpace(_robotDescription))
                _robotDescription = "EV3 robot with two drive motors and a front color sensor.";

            Messages = new ObservableCollection<AiChatMessageVM>();
            Attachments = new ObservableCollection<AiAttachmentVM>();
            HistoryItems = new ObservableCollection<AiChatHistoryItemVM>();

            NewChatCommand = new RelayCommand(param => StartNewChat(), true);
            HistoryCommand = new RelayCommand(param => IsHistoryOpen = true, true);
            OpenHistoryItemCommand = new RelayCommand(OpenHistoryItem, true);
            ClearHistoryCommand = new RelayCommand(param => ClearHistory(), true);
            ToggleSettingsCommand = new RelayCommand(param => IsSettingsOpen = !IsSettingsOpen, true);
            AttachFileCommand = new RelayCommand(param => PickFiles(), true);
            RemoveAttachmentCommand = new RelayCommand(RemoveAttachment, true);
            SendOrStopCommand = new RelayCommand(param => SendOrStop(), true);

            ReloadHistoryItems();
            SeedDemoMessages();
        }

        public ObservableCollection<AiChatMessageVM> Messages { get; private set; }
        public ObservableCollection<AiAttachmentVM> Attachments { get; private set; }
        public ObservableCollection<AiChatHistoryItemVM> HistoryItems { get; private set; }
        public ObservableCollection<string> Models { get; private set; }
        public ObservableCollection<string> ReasoningModes { get; private set; }

        public ICommand NewChatCommand { get; private set; }
        public ICommand HistoryCommand { get; private set; }
        public ICommand OpenHistoryItemCommand { get; private set; }
        public ICommand ClearHistoryCommand { get; private set; }
        public ICommand ToggleSettingsCommand { get; private set; }
        public ICommand AttachFileCommand { get; private set; }
        public ICommand RemoveAttachmentCommand { get; private set; }
        public ICommand SendOrStopCommand { get; private set; }

        public string InputText
        {
            get { return _inputText; }
            set
            {
                _inputText = value;
                OnPropertyChanged("InputText");
                OnPropertyChanged("CanSend");
                OnPropertyChanged("SendButtonEnabled");
            }
        }

        public bool IsBotWorking
        {
            get { return _isBotWorking; }
            set
            {
                _isBotWorking = value;
                OnPropertyChanged("IsBotWorking");
                OnPropertyChanged("SelectorsEnabled");
                OnPropertyChanged("SendButtonEnabled");
                OnPropertyChanged("SendIconVisibility");
                OnPropertyChanged("StopIconVisibility");
            }
        }

        public bool IsHistoryOpen
        {
            get { return _isHistoryOpen; }
            set
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
            set
            {
                _isSettingsOpen = value;
                OnPropertyChanged("IsSettingsOpen");
            }
        }

        public string SelectedModel
        {
            get { return _selectedModel; }
            set
            {
                _selectedModel = value;
                OnPropertyChanged("SelectedModel");
            }
        }

        public string SelectedReasoning
        {
            get { return _selectedReasoning; }
            set
            {
                _selectedReasoning = value;
                OnPropertyChanged("SelectedReasoning");
            }
        }

        public string WheelDiameter
        {
            get { return _wheelDiameter; }
            set
            {
                _wheelDiameter = value;
                AiAppSettingsStore.Settings.WheelDiameter = value ?? "";
                AiAppSettingsStore.SaveSettings();
                OnPropertyChanged("WheelDiameter");
            }
        }

        public string WheelBaseWidth
        {
            get { return _wheelBaseWidth; }
            set
            {
                _wheelBaseWidth = value;
                AiAppSettingsStore.Settings.WheelBaseWidth = value ?? "";
                AiAppSettingsStore.SaveSettings();
                OnPropertyChanged("WheelBaseWidth");
            }
        }

        public string RobotDescription
        {
            get { return _robotDescription; }
            set
            {
                _robotDescription = value;
                AiAppSettingsStore.Settings.RobotDescription = value ?? "";
                AiAppSettingsStore.SaveSettings();
                OnPropertyChanged("RobotDescription");
                OnPropertyChanged("RobotDescriptionCounter");
            }
        }

        public string RobotDescriptionCounter
        {
            get { return (RobotDescription == null ? 0 : RobotDescription.Length) + "/800"; }
        }

        public bool SelectorsEnabled
        {
            get { return !IsBotWorking; }
        }

        public bool CanSend
        {
            get { return !string.IsNullOrWhiteSpace(InputText); }
        }

        public bool SendButtonEnabled
        {
            get { return IsBotWorking || CanSend; }
        }

        public Visibility SendIconVisibility
        {
            get { return IsBotWorking ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility StopIconVisibility
        {
            get { return IsBotWorking ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility ConversationVisibility
        {
            get { return IsHistoryOpen ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility HistoryVisibility
        {
            get { return IsHistoryOpen ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility ComposerVisibility
        {
            get { return IsHistoryOpen ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility AttachmentVisibility
        {
            get { return Attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string ContextTooltip
        {
            get { return "Context: 62%\n5h limit: 18%\n7d limit: 7%"; }
        }

        public void AttachDroppedFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var name = Path.GetFileName(path);
            Attachments.Add(new AiAttachmentVM(name, "FILE", path));
            OnPropertyChanged("AttachmentVisibility");
        }

        public void AttachEditorTab(string tabName)
        {
            if (string.IsNullOrWhiteSpace(tabName))
                return;

            Attachments.Add(new AiAttachmentVM(tabName, "TAB", "Open editor tab"));
            OnPropertyChanged("AttachmentVisibility");
        }

        private void SeedDemoMessages()
        {
            _currentSessionId = Guid.NewGuid().ToString("N");
            Messages.Clear();
            Messages.Add(AiChatMessageVM.CreateUser("Help me quickly check the program before running it on the robot."));
            Messages.Add(AiChatMessageVM.CreateAssistant(
@"# Check plan
I can inspect the program before uploading it to EV3:

- **Syntax mistakes** and unclosed blocks.
- _Suspicious speed values_ and turns.
- Variable checks such as `wheelDiameter`.

```bp
start
  motor B on 30
  motor C on 30
end
```

> Tip: before class, save a clean commit and experiment in a separate branch."));
        }

        private void StartNewChat()
        {
            IsHistoryOpen = false;
            IsBotWorking = false;
            Attachments.Clear();
            InputText = "";
            SeedDemoMessages();
            OnPropertyChanged("AttachmentVisibility");
        }

        private void OpenHistoryItem(object parameter)
        {
            var item = parameter as AiChatHistoryItemVM;
            if (item == null)
                return;

            var session = AiAppSettingsStore.LoadChatSessions().FirstOrDefault(candidate => candidate.Id == item.Id);
            if (session == null)
                return;

            _currentSessionId = session.Id;
            IsHistoryOpen = false;
            Messages.Clear();
            foreach (var message in session.Messages)
                Messages.Add(AiChatMessageVM.Create(message.Markdown, message.IsUser));
        }

        private void ClearHistory()
        {
            var result = MessageBox.Show("Clear AI chat history?", "AI chat", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            AiAppSettingsStore.ClearChatSessions();
            ReloadHistoryItems();
            StartNewChat();
        }

        private void PickFiles()
        {
            var dialog = new OpenFileDialog { Multiselect = true };
            if (dialog.ShowDialog() != true)
                return;

            foreach (var fileName in dialog.FileNames)
                AttachDroppedFile(fileName);
        }

        private void RemoveAttachment(object parameter)
        {
            var attachment = parameter as AiAttachmentVM;
            if (attachment == null)
                return;

            Attachments.Remove(attachment);
            OnPropertyChanged("AttachmentVisibility");
        }

        private void SendOrStop()
        {
            if (IsBotWorking)
            {
                IsBotWorking = false;
                return;
            }

            if (!CanSend)
                return;

            Messages.Add(AiChatMessageVM.CreateUser(InputText.Trim()));
            InputText = "";
            Attachments.Clear();
            OnPropertyChanged("AttachmentVisibility");
            SaveCurrentSession();
            ReloadHistoryItems();
            IsBotWorking = true;
        }

        private void SaveCurrentSession()
        {
            var userMessages = Messages.Where(item => item.IsUser).ToList();
            var title = userMessages.Count == 0 ? "New chat" : TrimForTitle(userMessages[userMessages.Count - 1].Markdown, 34);
            var subtitle = Messages.Count == 0 ? "" : TrimForTitle(Messages[Messages.Count - 1].Markdown, 54);
            var session = new AiChatSessionData
            {
                Id = _currentSessionId,
                Title = title,
                Subtitle = subtitle,
                Messages = Messages.Select(item => new AiChatMessageData { Markdown = item.Markdown, IsUser = item.IsUser }).ToList()
            };

            AiAppSettingsStore.SaveChatSession(session);
        }

        private void ReloadHistoryItems()
        {
            HistoryItems.Clear();
            foreach (var session in AiAppSettingsStore.LoadChatSessions())
                HistoryItems.Add(new AiChatHistoryItemVM(session.Id, session.Title, session.Subtitle));
        }

        private static string TrimForTitle(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "New chat";

            var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return normalized.Length <= maxLength ? normalized : normalized.Substring(0, maxLength - 3) + "...";
        }
    }

    internal class AiChatMessageVM
    {
        private AiChatMessageVM(string markdown, bool isUser)
        {
            Markdown = markdown ?? "";
            IsUser = isUser;
            Document = AiMarkdownRenderer.CreateDocument(Markdown, isUser);
        }

        public string Markdown { get; private set; }
        public bool IsUser { get; private set; }
        public FlowDocument Document { get; private set; }

        public Visibility UserVisibility
        {
            get { return IsUser ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility AssistantVisibility
        {
            get { return IsUser ? Visibility.Collapsed : Visibility.Visible; }
        }

        public static AiChatMessageVM Create(string markdown, bool isUser)
        {
            return new AiChatMessageVM(markdown, isUser);
        }

        public static AiChatMessageVM CreateUser(string markdown)
        {
            return new AiChatMessageVM(markdown, true);
        }

        public static AiChatMessageVM CreateAssistant(string markdown)
        {
            return new AiChatMessageVM(markdown, false);
        }
    }

    internal class AiAttachmentVM
    {
        public AiAttachmentVM(string name, string kind, string source)
        {
            Name = name;
            Kind = kind;
            Source = source;
        }

        public string Name { get; private set; }
        public string Kind { get; private set; }
        public string Source { get; private set; }
    }

    internal class AiChatHistoryItemVM
    {
        public AiChatHistoryItemVM(string id, string title, string subtitle)
        {
            Id = id;
            Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title;
            Subtitle = subtitle ?? "";
        }

        public string Id { get; private set; }
        public string Title { get; private set; }
        public string Subtitle { get; private set; }
    }

    internal static class AiMarkdownRenderer
    {
        private static readonly Regex LinkRegex = new Regex(@"^\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);
        private static readonly Regex OrderedListRegex = new Regex(@"^\d+[\.)]\s+(.+)$", RegexOptions.Compiled);

        private enum AiListKind
        {
            None,
            Unordered,
            Ordered
        }

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

            var text = (markdown ?? "").Replace("\r\n", "\n");
            var lines = text.Split('\n');
            var inCode = false;
            Paragraph codeParagraph = null;
            System.Windows.Documents.List currentList = null;
            var currentListKind = AiListKind.None;

            foreach (var rawLine in lines)
            {
                var line = rawLine ?? "";
                if (line.TrimStart().StartsWith("```"))
                {
                    currentList = null;
                    currentListKind = AiListKind.None;

                    if (!inCode)
                    {
                        inCode = true;
                        codeParagraph = new Paragraph
                        {
                            Margin = new Thickness(0, 5, 0, 5),
                            Padding = new Thickness(7, 5, 7, 5),
                            Background = BrushFrom("#F1F3F7"),
                            FontFamily = new FontFamily("JetBrains Mono, Consolas")
                        };
                        document.Blocks.Add(codeParagraph);
                    }
                    else
                    {
                        inCode = false;
                        codeParagraph = null;
                    }

                    continue;
                }

                if (inCode)
                {
                    codeParagraph.Inlines.Add(new Run(line));
                    codeParagraph.Inlines.Add(new LineBreak());
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    currentList = null;
                    currentListKind = AiListKind.None;
                    document.Blocks.Add(new Paragraph { Margin = new Thickness(0, 2, 0, 2) });
                    continue;
                }

                ListItem listItem;
                AiListKind listKind;
                if (TryCreateListItem(line, out listItem, out listKind))
                {
                    if (currentList == null || currentListKind != listKind)
                    {
                        currentListKind = listKind;
                        currentList = new System.Windows.Documents.List
                        {
                            MarkerStyle = listKind == AiListKind.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                            Margin = new Thickness(13, 2, 0, 2),
                            Padding = new Thickness(10, 0, 0, 0)
                        };
                        document.Blocks.Add(currentList);
                    }

                    currentList.ListItems.Add(listItem);
                    continue;
                }

                currentList = null;
                currentListKind = AiListKind.None;
                document.Blocks.Add(CreateParagraph(line, compact));
            }

            return document;
        }

        private static bool TryCreateListItem(string line, out ListItem item, out AiListKind kind)
        {
            item = null;
            kind = AiListKind.None;

            var trimmed = line.TrimStart();
            var content = "";
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
            {
                kind = AiListKind.Unordered;
                content = trimmed.Substring(2);
            }
            else
            {
                var ordered = OrderedListRegex.Match(trimmed);
                if (!ordered.Success)
                    return false;

                kind = AiListKind.Ordered;
                content = ordered.Groups[1].Value;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            AddInlineMarkdown(paragraph.Inlines, content);
            item = new ListItem(paragraph) { Margin = new Thickness(0) };
            return true;
        }

        private static Paragraph CreateParagraph(string line, bool compact)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("# "))
            {
                paragraph.FontSize = compact ? 15 : 17;
                paragraph.FontWeight = FontWeights.Bold;
                AddInlineMarkdown(paragraph.Inlines, trimmed.Substring(2));
                return paragraph;
            }

            if (trimmed.StartsWith("## "))
            {
                paragraph.FontSize = compact ? 14 : 16;
                paragraph.FontWeight = FontWeights.Bold;
                AddInlineMarkdown(paragraph.Inlines, trimmed.Substring(3));
                return paragraph;
            }

            if (trimmed.StartsWith("> "))
            {
                paragraph.Background = BrushFrom("#F6F7FA");
                paragraph.Padding = new Thickness(7, 4, 7, 4);
                paragraph.Foreground = BrushFrom("#60687A");
                AddInlineMarkdown(paragraph.Inlines, trimmed.Substring(2));
                return paragraph;
            }

            AddInlineMarkdown(paragraph.Inlines, line);
            return paragraph;
        }

        private static void AddInlineMarkdown(InlineCollection inlines, string text)
        {
            var index = 0;
            while (index < text.Length)
            {
                var link = LinkRegex.Match(text.Substring(index));
                if (link.Success)
                {
                    var run = new Run(link.Groups[1].Value)
                    {
                        Foreground = BrushFrom("#0A86FF"),
                        TextDecorations = TextDecorations.Underline
                    };
                    inlines.Add(run);
                    index += link.Length;
                    continue;
                }

                if (TryAddDelimited(inlines, text, ref index, "`", span =>
                {
                    span.FontFamily = new FontFamily("JetBrains Mono, Consolas");
                    span.Background = BrushFrom("#EEF1F6");
                }))
                    continue;

                if (TryAddDelimited(inlines, text, ref index, "**", span => span.FontWeight = FontWeights.Bold))
                    continue;

                if (TryAddDelimited(inlines, text, ref index, "*", span => span.FontStyle = FontStyles.Italic))
                    continue;

                if (TryAddDelimited(inlines, text, ref index, "_", span => span.FontStyle = FontStyles.Italic))
                    continue;

                var next = FindNextMarker(text, index + 1);
                inlines.Add(new Run(text.Substring(index, next - index)));
                index = next;
            }
        }

        private static bool TryAddDelimited(InlineCollection inlines, string text, ref int index, string marker, Action<Span> configure)
        {
            if (!text.Substring(index).StartsWith(marker))
                return false;

            var end = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
            if (end < 0)
                return false;

            var span = new Span(new Run(text.Substring(index + marker.Length, end - index - marker.Length)));
            configure(span);
            inlines.Add(span);
            index = end + marker.Length;
            return true;
        }

        private static int FindNextMarker(string text, int start)
        {
            var next = text.Length;
            foreach (var marker in new[] { "`", "**", "*", "_", "[" })
            {
                var markerIndex = text.IndexOf(marker, start, StringComparison.Ordinal);
                if (markerIndex >= 0 && markerIndex < next)
                    next = markerIndex;
            }

            return next;
        }

        private static Brush BrushFrom(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
    }
}
