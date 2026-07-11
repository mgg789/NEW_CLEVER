using Clever.Model.Utils;
using ScintillaNET;
using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Clever.Model.Bplus
{
    internal sealed class AiInlineCompletionController : IDisposable
    {
        private const int DebounceMilliseconds = 350;
        private const int PrefixLimit = 12000;
        private const int SuffixLimit = 6000;
        private const int GhostAnnotationStyle = 31;
        private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(5);

        private readonly TextEditor _editor;
        private readonly Scintilla _textArea;
        private readonly IAiTextGenerationService _service;
        private readonly BpAiReferenceProvider _referenceProvider;
        private readonly System.Windows.Forms.Timer _debounceTimer;

        private CancellationTokenSource _requestCancellation;
        private string _ghostText = "";
        private int _ghostPosition = -1;
        private int _documentRevision;
        private int _requestVersion;
        private int _lastCaretPosition;
        private int? _expectedCaretPosition;
        private DateTime _cooldownUntilUtc = DateTime.MinValue;
        private bool _attached;
        private bool _disposed;
        private bool _keyboardEditPending;
        private bool _explicitUserEdit;
        private bool _accepting;

        internal AiInlineCompletionController(
            TextEditor editor,
            Scintilla textArea,
            IAiTextGenerationService service,
            BpAiReferenceProvider referenceProvider)
        {
            _editor = editor ?? throw new ArgumentNullException("editor");
            _textArea = textArea ?? throw new ArgumentNullException("textArea");
            _service = service ?? throw new ArgumentNullException("service");
            _referenceProvider = referenceProvider ?? throw new ArgumentNullException("referenceProvider");
            _lastCaretPosition = textArea.CurrentPosition;

            _debounceTimer = new System.Windows.Forms.Timer { Interval = DebounceMilliseconds };
            _debounceTimer.Tick += DebounceTimer_Tick;
            ConfigureGhostReservationStyle();
        }

        internal event EventHandler StateChanged;

        internal bool HasSuggestion
        {
            get { return !string.IsNullOrEmpty(_ghostText); }
        }

        internal bool IsEnabled
        {
            get
            {
                var settings = AiAppSettingsStore.Settings;
                return settings != null && settings.AiEnabled && settings.AiCodeCompletionsEnabled;
            }
        }

        internal void Attach()
        {
            if (_attached || _disposed)
                return;

            _textArea.Insert += TextArea_Insert;
            _textArea.Delete += TextArea_Delete;
            _textArea.UpdateUI += TextArea_UpdateUI;
            _textArea.Painted += TextArea_Painted;
            _textArea.KeyDown += TextArea_KeyDown;
            _textArea.MouseDown += TextArea_MouseDown;
            _textArea.LostFocus += TextArea_LostFocus;
            _textArea.Resize += TextArea_Resize;
            _textArea.ZoomChanged += TextArea_ZoomChanged;
            AiAppSettingsStore.SettingsChanged += AiAppSettingsStore_SettingsChanged;
            ClearGhostAnnotation();
            _lastCaretPosition = _textArea.CurrentPosition;
            _attached = true;
        }

        internal void Detach()
        {
            if (!_attached)
                return;

            _textArea.Insert -= TextArea_Insert;
            _textArea.Delete -= TextArea_Delete;
            _textArea.UpdateUI -= TextArea_UpdateUI;
            _textArea.Painted -= TextArea_Painted;
            _textArea.KeyDown -= TextArea_KeyDown;
            _textArea.MouseDown -= TextArea_MouseDown;
            _textArea.LostFocus -= TextArea_LostFocus;
            _textArea.Resize -= TextArea_Resize;
            _textArea.ZoomChanged -= TextArea_ZoomChanged;
            AiAppSettingsStore.SettingsChanged -= AiAppSettingsStore_SettingsChanged;
            _attached = false;
            Clear();
        }

        internal void RefreshSettings()
        {
            Clear();
        }

        internal void OnDocumentIdentityChanged()
        {
            Clear();
        }

        internal void RunUserEdit(Action action)
        {
            if (action == null)
                return;

            _explicitUserEdit = true;
            try
            {
                action();
            }
            finally
            {
                _explicitUserEdit = false;
            }
        }

        internal void Clear()
        {
            InvalidatePendingRequest(true);
            SetGhost("", -1);
        }

        private void TextArea_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Clear();
                return;
            }

            if (HasSuggestion && !_textArea.AutoCActive &&
                (e.KeyCode == Keys.Tab || (e.KeyCode == Keys.Enter && e.Shift)))
            {
                AcceptSuggestion();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (IsCaretMovementKey(e))
            {
                Clear();
                return;
            }

            if (IsPotentialEditKey(e))
            {
                _keyboardEditPending = true;
                BeginInvokeSafely(() => _keyboardEditPending = false);
            }
        }

        private void TextArea_Insert(object sender, ModificationEventArgs e)
        {
            var isUserEdit = !_accepting && (_keyboardEditPending || _explicitUserEdit);
            _documentRevision++;
            InvalidatePendingRequest(true);

            var insertedText = e.Text ?? "";
            _expectedCaretPosition = e.Position + insertedText.Length;

            if (_accepting)
                return;

            if (!isUserEdit)
            {
                SetGhost("", -1);
                return;
            }

            if (HasSuggestion && e.Position == _ghostPosition &&
                _ghostText.StartsWith(insertedText, StringComparison.Ordinal))
            {
                SetGhost(_ghostText.Substring(insertedText.Length), e.Position + insertedText.Length);
            }
            else
            {
                SetGhost("", -1);
            }

            ScheduleRequest();
        }

        private void TextArea_Delete(object sender, ModificationEventArgs e)
        {
            var isUserEdit = !_accepting && (_keyboardEditPending || _explicitUserEdit);
            _documentRevision++;
            _expectedCaretPosition = e.Position;
            InvalidatePendingRequest(true);
            SetGhost("", -1);

            if (isUserEdit)
                ScheduleRequest();
        }

        private void TextArea_UpdateUI(object sender, UpdateUIEventArgs e)
        {
            if (_textArea.AutoCActive && HasSuggestion)
                SetGhost("", -1);

            var currentPosition = _textArea.CurrentPosition;
            if ((e.Change & UpdateChange.Selection) != 0 && currentPosition != _lastCaretPosition)
            {
                if (_expectedCaretPosition.HasValue && currentPosition == _expectedCaretPosition.Value)
                {
                    _expectedCaretPosition = null;
                }
                else
                {
                    Clear();
                }
            }

            _lastCaretPosition = currentPosition;
            if (HasSuggestion && (e.Change & (UpdateChange.VScroll | UpdateChange.HScroll)) != 0)
                InvalidateEditor();
        }

        private void TextArea_Painted(object sender, EventArgs e)
        {
            DrawGhostText();
        }

        private void TextArea_MouseDown(object sender, MouseEventArgs e)
        {
            Clear();
        }

        private void TextArea_LostFocus(object sender, EventArgs e)
        {
            Clear();
        }

        private void TextArea_Resize(object sender, EventArgs e)
        {
            InvalidateEditor();
        }

        private void TextArea_ZoomChanged(object sender, EventArgs e)
        {
            InvalidateEditor();
        }

        private void AiAppSettingsStore_SettingsChanged(object sender, EventArgs e)
        {
            BeginInvokeSafely(RefreshSettings);
        }

        private void DebounceTimer_Tick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            RequestCompletionAsync();
        }

        private void ScheduleRequest()
        {
            _requestVersion++;
            CancelActiveRequest();
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private async void RequestCompletionAsync()
        {
            if (!CanRequestCompletion())
                return;

            var caretPosition = _textArea.CurrentPosition;
            var revision = _documentRevision;
            var version = _requestVersion;
            var cancellation = new CancellationTokenSource();
            _requestCancellation = cancellation;
            try
            {
                var prefixStart = Math.Max(0, caretPosition - PrefixLimit);
                var prefix = _textArea.GetTextRange(prefixStart, caretPosition - prefixStart);
                var suffixLength = Math.Min(SuffixLimit, _textArea.TextLength - caretPosition);
                var suffix = _textArea.GetTextRange(caretPosition, Math.Max(0, suffixLength));
                var documentIdentity = GetDocumentIdentity();
                var context = new AiInlineCompletionContext
                {
                    FilePath = documentIdentity,
                    FileType = Path.GetExtension(documentIdentity).ToLowerInvariant(),
                    Prefix = prefix,
                    Suffix = suffix,
                    CaretPosition = caretPosition,
                    LanguageReference = _referenceProvider.BuildLanguageReference(prefix, suffix),
                    LinkedContext = _referenceProvider.BuildLinkedContext(_editor, prefix, suffix)
                };
                var completion = await _service.GenerateInlineCompletionAsync(context, cancellation.Token);
                if (cancellation.IsCancellationRequested || version != _requestVersion ||
                    revision != _documentRevision || caretPosition != _textArea.CurrentPosition ||
                    !CanShowCompletion())
                    return;

                completion = NormalizeCompletion(completion);
                if (!string.IsNullOrEmpty(completion))
                    SetGhost(completion, caretPosition);
                else
                    SetGhost("", -1);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                if (!cancellation.IsCancellationRequested && version == _requestVersion)
                {
                    _cooldownUntilUtc = DateTime.UtcNow.Add(FailureCooldown);
                    SetGhost("", -1);
                }
            }
            finally
            {
                if (ReferenceEquals(_requestCancellation, cancellation))
                    _requestCancellation = null;
                cancellation.Dispose();
            }
        }

        private bool CanRequestCompletion()
        {
            return IsEnabled && DateTime.UtcNow >= _cooldownUntilUtc && CanShowCompletion();
        }

        private bool CanShowCompletion()
        {
            return !_disposed && _attached && IsSupportedDocument() && !_textArea.IsDisposed &&
                _textArea.ContainsFocus && !_textArea.ReadOnly && !_textArea.AutoCActive &&
                _textArea.SelectionStart == _textArea.SelectionEnd && IsCaretAtSafeInlinePosition();
        }

        private bool IsCaretAtSafeInlinePosition()
        {
            var position = _textArea.CurrentPosition;
            var lineNumber = _textArea.LineFromPosition(position);
            if (lineNumber < 0 || lineNumber >= _textArea.Lines.Count)
                return false;

            var line = _textArea.Lines[lineNumber];
            var length = Math.Max(0, line.EndPosition - position);
            if (length == 0)
                return true;

            var remainder = _textArea.GetTextRange(position, length).TrimEnd('\r', '\n');
            return string.IsNullOrWhiteSpace(remainder);
        }

        private bool IsSupportedDocument()
        {
            var extension = Path.GetExtension(GetDocumentIdentity());
            return string.Equals(extension, ".bp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".bpi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".bpm", StringComparison.OrdinalIgnoreCase);
        }

        private string GetDocumentIdentity()
        {
            return !string.IsNullOrWhiteSpace(_editor.Filepath) ? _editor.Filepath : _editor.ProgramName;
        }

        private void AcceptSuggestion()
        {
            if (!HasSuggestion || _textArea.AutoCActive || _textArea.ReadOnly ||
                _textArea.CurrentPosition != _ghostPosition)
                return;

            var insertion = NormalizeForDocument(_ghostText);
            var position = _ghostPosition;
            Clear();
            _accepting = true;
            _textArea.BeginUndoAction();
            try
            {
                _textArea.InsertText(position, insertion);
                _textArea.GotoPosition(position + insertion.Length);
            }
            finally
            {
                _textArea.EndUndoAction();
                _accepting = false;
                _lastCaretPosition = _textArea.CurrentPosition;
                _expectedCaretPosition = null;
            }
        }

        private string NormalizeForDocument(string value)
        {
            var normalized = NormalizeCompletion(value);
            switch (_textArea.EolMode)
            {
                case Eol.CrLf:
                    return normalized.Replace("\n", "\r\n");
                case Eol.Cr:
                    return normalized.Replace("\n", "\r");
                default:
                    return normalized;
            }
        }

        private static string NormalizeCompletion(string value)
        {
            return (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private void DrawGhostText()
        {
            if (!HasSuggestion || _ghostPosition < 0 || _ghostPosition > _textArea.TextLength ||
                _textArea.AutoCActive || !_textArea.ContainsFocus)
                return;

            var x = _textArea.PointXFromPosition(_ghostPosition);
            var y = _textArea.PointYFromPosition(_ghostPosition);
            if (y >= _textArea.ClientSize.Height || x >= _textArea.ClientSize.Width)
                return;

            var lineNumber = Math.Max(0, _textArea.LineFromPosition(_ghostPosition));
            var lineHeight = lineNumber < _textArea.Lines.Count
                ? Math.Max(1, _textArea.Lines[lineNumber].Height)
                : Math.Max(1, _textArea.Font.Height);
            var lineStartX = lineNumber < _textArea.Lines.Count
                ? _textArea.PointXFromPosition(_textArea.Lines[lineNumber].Position)
                : x;
            var style = _textArea.Styles[Style.Default];
            var fontName = string.IsNullOrWhiteSpace(style.Font) ? _textArea.Font.Name : style.Font;
            var fontSize = Math.Max(1.0f, style.SizeF + _textArea.Zoom);
            var lines = _ghostText.Split('\n');

            using (var graphics = _textArea.CreateGraphics())
            using (var font = new Font(fontName, fontSize, FontStyle.Italic, GraphicsUnit.Point))
            using (var brush = new SolidBrush(Color.FromArgb(185, 112, 118, 128)))
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                graphics.SetClip(_textArea.ClientRectangle);
                format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;

                for (var index = 0; index < lines.Length; index++)
                {
                    var drawX = index == 0 ? x : lineStartX;
                    var drawY = y + (index * lineHeight);
                    if (drawY >= _textArea.ClientSize.Height)
                        break;
                    graphics.DrawString(lines[index], font, brush, drawX, drawY, format);
                }
            }
        }

        private void SetGhost(string text, int position)
        {
            text = text ?? "";
            if (text.Length == 0)
                position = -1;

            var changed = !string.Equals(_ghostText, text, StringComparison.Ordinal) || _ghostPosition != position;
            ClearGhostAnnotation();
            _ghostText = text;
            _ghostPosition = position;
            ApplyGhostAnnotation();
            if (!changed)
                return;

            InvalidateEditor();
            var handler = StateChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private void ConfigureGhostReservationStyle()
        {
            // The annotation is only a virtual spacer. The ghost itself is drawn in
            // DrawGhostText, while this hidden style reserves the required display rows.
            _textArea.Styles[GhostAnnotationStyle].Visible = false;
            _textArea.AnnotationVisible = Annotation.Standard;
        }

        private void ApplyGhostAnnotation()
        {
            if (!HasSuggestion || _ghostPosition < 0 || _ghostPosition > _textArea.TextLength)
                return;

            var lines = _ghostText.Split('\n');
            if (lines.Length < 2)
                return;

            var lineNumber = _textArea.LineFromPosition(_ghostPosition);
            if (lineNumber < 0 || lineNumber >= _textArea.Lines.Count)
                return;

            ConfigureGhostReservationStyle();
            var line = _textArea.Lines[lineNumber];
            var reservedRows = new string[lines.Length - 1];
            for (var index = 0; index < reservedRows.Length; index++)
                reservedRows[index] = " ";
            line.AnnotationText = string.Join("\n", reservedRows);
            line.AnnotationStyle = GhostAnnotationStyle;
        }

        private void ClearGhostAnnotation()
        {
            // Line indexes move whenever the user inserts or removes a newline.
            // Keeping a cached line number therefore left the old annotation on a
            // different line and created a non-editable blank gap. Inline ghosts
            // are the only annotations in this editor, so Scintilla can clear the
            // reservation atomically without depending on a stale line index.
            _textArea.AnnotationClearAll();
        }

        private void InvalidatePendingRequest(bool stopDebounce)
        {
            _requestVersion++;
            if (stopDebounce)
                _debounceTimer.Stop();
            CancelActiveRequest();
        }

        private void CancelActiveRequest()
        {
            var cancellation = _requestCancellation;
            if (cancellation != null && !cancellation.IsCancellationRequested)
                cancellation.Cancel();
        }

        private void InvalidateEditor()
        {
            if (!_textArea.IsDisposed)
                _textArea.Invalidate();
        }

        private void BeginInvokeSafely(Action action)
        {
            if (_disposed || _textArea.IsDisposed || action == null)
                return;

            if (_textArea.IsHandleCreated)
            {
                try
                {
                    _textArea.BeginInvoke(action);
                }
                catch (InvalidOperationException)
                {
                }
            }
            else
                action();
        }

        private static bool IsCaretMovementKey(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.Home:
                case Keys.End:
                case Keys.PageUp:
                case Keys.PageDown:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsPotentialEditKey(KeyEventArgs e)
        {
            if (e.Control)
                return e.KeyCode == Keys.V || e.KeyCode == Keys.X;
            if (e.Alt)
                return false;

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete || e.KeyCode == Keys.Enter ||
                e.KeyCode == Keys.Tab || e.KeyCode == Keys.Space)
                return true;
            if (e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
                return true;
            if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)
                return true;
            if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.Divide)
                return true;
            return e.KeyCode >= Keys.OemSemicolon && e.KeyCode <= Keys.OemBackslash;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Detach();
            _disposed = true;
            _debounceTimer.Tick -= DebounceTimer_Tick;
            _debounceTimer.Dispose();
            CancelActiveRequest();
        }
    }
}
