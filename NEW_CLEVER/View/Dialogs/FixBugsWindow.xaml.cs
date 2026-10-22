using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace NEW_CLEVER.View.Dialogs
{
    /// <summary>
    /// A stable, display-only snapshot of one active IDE problem.
    /// </summary>
    public sealed class FixBugsDialogItem
    {
        public FixBugsDialogItem(string kind, string file, string range, string message, bool isFixable)
            : this(kind, file, range, message, isFixable, null)
        {
        }

        public FixBugsDialogItem(string kind, string file, string range, string message, bool isFixable, string resolutionReason)
        {
            Kind = kind ?? string.Empty;
            File = file ?? string.Empty;
            Range = range ?? string.Empty;
            Message = message ?? string.Empty;
            IsFixable = isFixable;
            ResolutionReason = resolutionReason ?? string.Empty;
        }

        public string Kind { get; private set; }

        public string File { get; private set; }

        public string Range { get; private set; }

        public string Message { get; private set; }

        public bool IsFixable { get; private set; }

        /// <summary>
        /// Reason shown in the final report if this problem could not be fixed.
        /// </summary>
        public string ResolutionReason { get; private set; }

        public string Location
        {
            get
            {
                if (string.IsNullOrWhiteSpace(File))
                    return Range;

                if (string.IsNullOrWhiteSpace(Range))
                    return File;

                return File + ", " + Range;
            }
        }

        public string FixabilityMark
        {
            get { return IsFixable ? "AI" : "!"; }
        }

        public string FixabilityText
        {
            get { return IsFixable ? "Можно исправить" : "Не исправляется AI"; }
        }

        public string ReportTitle
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Location))
                    return Kind;

                return Kind + " — " + Location;
            }
        }

        public string ReportMessage
        {
            get
            {
                return string.IsNullOrWhiteSpace(ResolutionReason) ? Message : ResolutionReason;
            }
        }
    }

    /// <summary>
    /// Shows the active code problems before a safe Fix bugs run and its final report afterwards.
    /// This window owns no diagnostics or AI workflow; the caller supplies immutable snapshots.
    /// </summary>
    public partial class FixBugsWindow : Window
    {
        private readonly List<FixBugsDialogItem> _problems;

        public FixBugsWindow(IEnumerable<FixBugsDialogItem> problems)
        {
            if (problems == null)
                throw new ArgumentNullException(nameof(problems));

            InitializeComponent();

            _problems = new List<FixBugsDialogItem>();
            foreach (var problem in problems)
            {
                if (problem != null)
                    _problems.Add(problem);
            }

            ProblemsList.ItemsSource = _problems;
        }

        /// <summary>
        /// Snapshot displayed when the window was opened. It is never modified by the dialog.
        /// </summary>
        public IReadOnlyList<FixBugsDialogItem> Problems
        {
            get { return _problems.AsReadOnly(); }
        }

        /// <summary>
        /// Raised after the user explicitly requests the caller to start its Fix all workflow.
        /// </summary>
        public event EventHandler FixAllRequested;

        /// <summary>Prevents duplicate requests while the caller is validating and applying a repair run.</summary>
        public void BeginProgress()
        {
            FixAllButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            Title = "Fix bugs — исправление";
        }

        /// <summary>
        /// Replaces the confirmation view with the final run result.
        /// </summary>
        public void ShowReport(int fixedCount, TimeSpan elapsed, IEnumerable<FixBugsDialogItem> unresolved)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ShowReport(fixedCount, elapsed, unresolved));
                return;
            }

            var unresolvedItems = new List<FixBugsDialogItem>();
            if (unresolved != null)
            {
                foreach (var problem in unresolved)
                {
                    if (problem != null)
                        unresolvedItems.Add(problem);
                }
            }

            ProblemsPanel.Visibility = Visibility.Collapsed;
            ReportPanel.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
            FixAllButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;

            ReportSummaryText.Text = string.Format(
                CultureInfo.CurrentCulture,
                "Исправлено ошибок: {0}. Время работы: {1:0.0} сек.",
                fixedCount,
                Math.Max(0, elapsed.TotalSeconds));
            UnresolvedTitleText.Text = unresolvedItems.Count == 0
                ? "Неисправленных проблем нет."
                : string.Format(CultureInfo.CurrentCulture, "Не исправлено: {0}", unresolvedItems.Count);
            UnresolvedList.ItemsSource = unresolvedItems;
        }

        private void FixAllButton_Click(object sender, RoutedEventArgs e)
        {
            var handler = FixAllRequested;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
