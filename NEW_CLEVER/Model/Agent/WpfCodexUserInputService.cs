using NEW_CLEVER.AgentCore;
using NEW_CLEVER.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NEW_CLEVER.Model.Agent
{
    /// <summary>Shows app-server request_user_input questions without blocking the transport reader.</summary>
    public sealed class WpfCodexUserInputService
    {
        public Task<CodexUserInputResponse> HandleAsync(CodexUserInputRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var application = Application.Current;
            if (application == null || application.Dispatcher == null || application.Dispatcher.HasShutdownStarted)
                return Task.FromResult(new CodexUserInputResponse());
            if (application.Dispatcher.CheckAccess())
                return Task.FromResult(Show(request, cancellationToken));
            return application.Dispatcher.InvokeAsync(() => Show(request, cancellationToken), DispatcherPriority.Normal, cancellationToken).Task;
        }

        private static CodexUserInputResponse Show(CodexUserInputRequest request, CancellationToken cancellationToken)
        {
            request = request ?? new CodexUserInputRequest();
            var response = new CodexUserInputResponse();
            if (request.Questions == null || request.Questions.Count == 0)
                return response;

            var window = new Window
            {
                Title = Localize("AiUserInputTitle", "Codex needs your input"),
                Width = 520,
                MinWidth = 420,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 720,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                ShowInTaskbar = false,
                Owner = Application.Current.MainWindow,
                Background = Brushes.White
            };

            var root = new DockPanel { Margin = new Thickness(18) };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var cancel = new Button
            {
                Content = Localize("AiUserInputCancel", "Cancel"),
                MinWidth = 90,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            var submit = new Button
            {
                Content = Localize("AiUserInputSubmit", "Continue"),
                MinWidth = 100,
                Padding = new Thickness(12, 5, 12, 5),
                IsDefault = true
            };
            cancel.Click += (sender, args) => window.DialogResult = false;
            submit.Click += (sender, args) => window.DialogResult = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(submit);
            root.Children.Add(buttons);

            var questionPanel = new StackPanel();
            var readers = new List<Tuple<string, Func<string>>>();
            foreach (var question in request.Questions.Take(3))
            {
                questionPanel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(question.Header) ? question.Id : question.Header,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(65, 70, 89)),
                    Margin = new Thickness(0, readers.Count == 0 ? 0 : 16, 0, 4)
                });
                questionPanel.Children.Add(new TextBlock
                {
                    Text = question.Question ?? "",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(89, 97, 116)),
                    Margin = new Thickness(0, 0, 0, 7)
                });

                if (question.Options != null && question.Options.Count > 0)
                {
                    var combo = new ComboBox
                    {
                        IsEditable = true,
                        MinHeight = 30,
                        Padding = new Thickness(6, 3, 6, 3),
                        ItemsSource = question.Options.Select(option => new OptionChoice(option)).ToList(),
                        SelectedIndex = 0
                    };
                    questionPanel.Children.Add(combo);
                    readers.Add(Tuple.Create(question.Id, new Func<string>(() =>
                        combo.SelectedItem is OptionChoice choice ? choice.Label : combo.Text ?? "")));
                }
                else if (question.IsSecret)
                {
                    var password = new PasswordBox { MinHeight = 30, Padding = new Thickness(6, 3, 6, 3) };
                    questionPanel.Children.Add(password);
                    readers.Add(Tuple.Create(question.Id, new Func<string>(() => password.Password ?? "")));
                }
                else
                {
                    var text = new TextBox
                    {
                        MinHeight = 30,
                        Padding = new Thickness(6, 3, 6, 3),
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = true,
                        MaxHeight = 110,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    };
                    questionPanel.Children.Add(text);
                    readers.Add(Tuple.Create(question.Id, new Func<string>(() => text.Text ?? "")));
                }
            }
            root.Children.Add(new ScrollViewer
            {
                Content = questionPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
            window.Content = root;

            DispatcherTimer timer = null;
            if (request.AutoResolutionMs.HasValue && request.AutoResolutionMs.Value > 0)
            {
                timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(Math.Min(request.AutoResolutionMs.Value, 24L * 60 * 60 * 1000))
                };
                timer.Tick += (sender, args) =>
                {
                    timer.Stop();
                    if (window.IsVisible) window.DialogResult = false;
                };
                timer.Start();
            }

            var cancellationRegistration = cancellationToken.Register(() =>
            {
                var dispatcher = window.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted) return;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (window.IsVisible) window.DialogResult = false;
                }), DispatcherPriority.Send);
            });
            bool accepted;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                accepted = window.ShowDialog() == true;
            }
            finally
            {
                cancellationRegistration.Dispose();
                if (timer != null) timer.Stop();
            }
            if (!accepted) return response;
            foreach (var reader in readers)
                response.Answers[reader.Item1] = new[] { reader.Item2() };
            return response;
        }

        private static string Localize(string key, string fallback)
        {
            string value;
            return MainWindowVM.GetLocalization != null && MainWindowVM.GetLocalization.TryGetValue(key, out value)
                ? value
                : fallback;
        }

        private sealed class OptionChoice
        {
            public OptionChoice(CodexUserInputOption option)
            {
                Label = option == null ? "" : option.Label ?? "";
                Description = option == null ? "" : option.Description ?? "";
            }

            public string Label { get; private set; }
            public string Description { get; private set; }
            public override string ToString() { return string.IsNullOrWhiteSpace(Description) ? Label : Label + " — " + Description; }
        }
    }
}
