using NEW_CLEVER.AgentCore;
using NEW_CLEVER.Model.Agent;
using NEW_CLEVER.Model.Utils;
using NEW_CLEVER.ViewModel.BaseVM;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace NEW_CLEVER.ViewModel
{
    internal sealed class AiSettingsWindowVM : BaseViewModel, IDisposable
    {
        private readonly ICodexSessionBackend _backend;
        private string _embeddedUrl;
        private string _embeddedModel;
        private string _embeddedToken;
        private string _statusText;
        private bool _statusIsError;
        private string _codexStatusText;
        private string _runtimePath;
        private string _runtimeVersion;
        private string _authorizationUrl;
        private string _loginId;
        private string _updateText;
        private CodexRuntimeUpdateInfo _pendingUpdate;
        private bool _codexBusy;
        private bool _isLoggedIn;
        private bool _codexRuntimeMissing;
        private bool _disposed;

        public AiSettingsWindowVM()
        {
            var settings = AiAppSettingsStore.Settings;
            _embeddedUrl = settings.EmbeddedUrl;
            _embeddedModel = settings.EmbeddedModel;
            _embeddedToken = AiAppSettingsStore.EmbeddedToken;
            _backend = NEW_CLEVERAgentServices.Current.SessionBackend;
            _codexStatusText = Localize("AiStatusLoading", "Codex status is loading.");

            SaveEmbeddedCommand = new RelayCommand(param => SaveEmbedded(), true);
            ClearEmbeddedCommand = new RelayCommand(param => ClearEmbedded(), true);
            RefreshCodexCommand = new RelayCommand(param => RefreshCodexStatus(), true);
            RestartCodexCommand = new RelayCommand(param => RestartCodex(), true);
            CheckUpdateCommand = new RelayCommand(param => CheckForUpdate(), true);
            InstallUpdateCommand = new RelayCommand(param => InstallUpdate(), true);
            LoginCommand = new RelayCommand(param => StartLogin(), true);
            CancelLoginCommand = new RelayCommand(param => CancelLogin(), true);
            LogoutCommand = new RelayCommand(param => Logout(), true);
            OpenAuthorizationCommand = new RelayCommand(param => OpenAuthorizationUrl(), true);
            CopyAuthorizationCommand = new RelayCommand(param => CopyAuthorizationUrl(), true);
            _backend.StateChanged += Backend_StateChanged;
            _backend.EventReceived += Backend_EventReceived;
            StartCodex();
        }

        public ICommand SaveEmbeddedCommand { get; private set; }
        public ICommand ClearEmbeddedCommand { get; private set; }
        public ICommand RefreshCodexCommand { get; private set; }
        public ICommand RestartCodexCommand { get; private set; }
        public ICommand CheckUpdateCommand { get; private set; }
        public ICommand InstallUpdateCommand { get; private set; }
        public ICommand LoginCommand { get; private set; }
        public ICommand CancelLoginCommand { get; private set; }
        public ICommand LogoutCommand { get; private set; }
        public ICommand OpenAuthorizationCommand { get; private set; }
        public ICommand CopyAuthorizationCommand { get; private set; }

        public string WindowTitle { get { return Localize("AiSettingsTitle", "AI settings"); } }
        public string EnableAiText { get { return Localize("AiSettingsEnableFunctions", "Enable AI functions"); } }
        public string EmbeddedModelText { get { return Localize("AiSettingsEmbeddedModel", "OpenAI-compatible model"); } }
        public string UrlText { get { return Localize("AiSettingsUrl", "URL"); } }
        public string ModelText { get { return Localize("AiSettingsModel", "Model"); } }
        public string TokenText { get { return Localize("AiSettingsToken", "Token"); } }
        public string SaveText { get { return Localize("AiSettingsSave", "Save"); } }
        public string ClearText { get { return Localize("AiSettingsClear", "Clear"); } }
        public string CodexText { get { return Localize("AiSettingsCodex", "Codex app-server"); } }
        public string CodexButtonText { get { return Localize("AiReload", "Reload"); } }
        public string RuntimePathText { get { return Localize("AiRuntimePath", "Runtime path"); } }
        public string RuntimeVersionText { get { return Localize("AiRuntimeVersion", "Runtime version"); } }
        public string RestartText { get { return Localize("AiRestart", "Restart"); } }
        public string CheckUpdateText { get { return _codexRuntimeMissing ? Localize("AiFindCodex", "Install Codex") : Localize("AiCheckUpdate", "Check for updates"); } }
        public string InstallUpdateText { get { return _codexRuntimeMissing ? Localize("AiInstallCodex", "Install Codex") : Localize("AiInstallUpdate", "Install update"); } }
        public string LoginText { get { return Localize("AiLoginChatGpt", "Sign in with ChatGPT"); } }
        public string CancelLoginText { get { return Localize("AiCancelLogin", "Cancel sign-in"); } }
        public string LogoutText { get { return Localize("AiLogout", "Sign out"); } }
        public string OpenLinkText { get { return Localize("AiOpenLoginLink", "Open sign-in page"); } }
        public string CopyLinkText { get { return Localize("AiCopyLoginLink", "Copy link"); } }
        public string AccountText { get { return Localize("AiStatusAccount", "Account"); } }

        public bool AiEnabled
        {
            get { return AiAppSettingsStore.Settings.AiEnabled; }
            set
            {
                var settings = AiAppSettingsStore.Settings;
                if (settings.AiEnabled == value)
                    return;
                var previous = settings.AiEnabled;
                settings.AiEnabled = value;
                try { AiAppSettingsStore.SaveSettings(); }
                catch (Exception ex)
                {
                    settings.AiEnabled = previous;
                    SetStatus("AiSettingsSaveFailed", "Could not save AI settings: {0}", true, ex.Message);
                }
                OnPropertyChanged("AiEnabled");
            }
        }

        public string EmbeddedUrl { get { return _embeddedUrl; } set { _embeddedUrl = value; OnPropertyChanged("EmbeddedUrl"); } }
        public string EmbeddedModel { get { return _embeddedModel; } set { _embeddedModel = value; OnPropertyChanged("EmbeddedModel"); } }
        public string EmbeddedToken { get { return _embeddedToken; } set { _embeddedToken = value; OnPropertyChanged("EmbeddedToken"); } }
        public string StatusText { get { return _statusText; } private set { _statusText = value; OnPropertyChanged("StatusText"); } }
        public bool StatusIsError { get { return _statusIsError; } private set { _statusIsError = value; OnPropertyChanged("StatusIsError"); } }
        public Visibility CodexStatusVisibility { get { return Visibility.Visible; } }
        public string CodexStatusText { get { return _codexStatusText; } private set { _codexStatusText = value; OnPropertyChanged("CodexStatusText"); } }
        public string RuntimePath { get { return _runtimePath; } private set { _runtimePath = value ?? ""; OnPropertyChanged("RuntimePath"); } }
        public string RuntimeVersion { get { return _runtimeVersion; } private set { _runtimeVersion = value ?? ""; OnPropertyChanged("RuntimeVersion"); } }
        public string AuthorizationUrl { get { return _authorizationUrl; } private set { _authorizationUrl = value ?? ""; OnPropertyChanged("AuthorizationUrl"); OnPropertyChanged("LoginPendingVisibility"); } }
        public string UpdateText { get { return _updateText; } private set { _updateText = value ?? ""; OnPropertyChanged("UpdateText"); OnPropertyChanged("UpdateVisibility"); } }
        public bool CodexBusy { get { return _codexBusy; } private set { _codexBusy = value; OnPropertyChanged("CodexBusy"); OnPropertyChanged("CodexActionsEnabled"); } }
        public bool CodexActionsEnabled { get { return !CodexBusy; } }
        public Visibility LoginPendingVisibility { get { return string.IsNullOrWhiteSpace(AuthorizationUrl) ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility LoginVisibility { get { return !_isLoggedIn && string.IsNullOrWhiteSpace(AuthorizationUrl) ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility LogoutVisibility { get { return _isLoggedIn ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility UpdateVisibility { get { return _pendingUpdate != null && _pendingUpdate.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed; } }

        private void SaveEmbedded()
        {
            Uri endpoint;
            if (!Uri.TryCreate((EmbeddedUrl ?? "").Trim(), UriKind.Absolute, out endpoint)
                || endpoint.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrWhiteSpace(EmbeddedModel)
                || string.IsNullOrWhiteSpace(EmbeddedToken))
            {
                SetStatus("AiSettingsValidationFailed", "Enter a valid HTTPS URL, model and token.", true);
                return;
            }

            var settings = AiAppSettingsStore.Settings;
            var previousUrl = settings.EmbeddedUrl;
            var previousModel = settings.EmbeddedModel;
            var previousToken = settings.EmbeddedTokenProtected;
            try
            {
                settings.EmbeddedUrl = EmbeddedUrl ?? "";
                settings.EmbeddedModel = EmbeddedModel ?? "";
                AiAppSettingsStore.EmbeddedToken = EmbeddedToken ?? "";
                AiAppSettingsStore.SaveSettings();
                SetStatus("AiSettingsSaved", "AI settings saved.", false);
            }
            catch (Exception ex)
            {
                settings.EmbeddedUrl = previousUrl;
                settings.EmbeddedModel = previousModel;
                settings.EmbeddedTokenProtected = previousToken;
                SetStatus("AiSettingsSaveFailed", "Could not save AI settings: {0}", true, ex.Message);
            }
        }

        private void ClearEmbedded()
        {
            var settings = AiAppSettingsStore.Settings;
            var previousUrl = settings.EmbeddedUrl;
            var previousModel = settings.EmbeddedModel;
            var previousToken = settings.EmbeddedTokenProtected;
            try
            {
                AiAppSettingsStore.ClearEmbeddedSettings();
                EmbeddedUrl = "";
                EmbeddedModel = "";
                EmbeddedToken = "";
                SetStatus("AiSettingsCleared", "AI provider settings cleared.", false);
            }
            catch (Exception ex)
            {
                settings.EmbeddedUrl = previousUrl;
                settings.EmbeddedModel = previousModel;
                settings.EmbeddedTokenProtected = previousToken;
                SetStatus("AiSettingsSaveFailed", "Could not save AI settings: {0}", true, ex.Message);
            }
        }

        private async void StartCodex()
        {
            await RunStatusOperation(() => _backend.StartAsync(CancellationToken.None));
        }

        private async void RestartCodex()
        {
            await RunStatusOperation(() => _backend.RestartAsync(CancellationToken.None));
        }

        private async void RefreshCodexStatus()
        {
            BackendResult<CodexStatusSnapshot> result;
            try { result = await _backend.GetStatusAsync(null, CancellationToken.None); }
            catch (Exception ex) { result = BackendResult<CodexStatusSnapshot>.Fail("status_error", ex.Message); }

            ApplyStatusResult(result);
        }

        private async System.Threading.Tasks.Task RunStatusOperation(Func<System.Threading.Tasks.Task<BackendResult<CodexStatusSnapshot>>> operation)
        {
            CodexBusy = true;
            BackendResult<CodexStatusSnapshot> result;
            try { result = await operation(); }
            catch (Exception ex) { result = BackendResult<CodexStatusSnapshot>.Fail("codex_operation_failed", ex.Message); }
            finally { OnUi(() => CodexBusy = false); }
            ApplyStatusResult(result);
        }

        private void ApplyStatusResult(BackendResult<CodexStatusSnapshot> result)
        {
            OnUi(() =>
            {
                if (!result.Success || result.Data == null)
                {
                    _codexRuntimeMissing = string.Equals(result.Code, "codex_runtime_missing", StringComparison.OrdinalIgnoreCase);
                    OnPropertyChanged("CheckUpdateText");
                    OnPropertyChanged("InstallUpdateText");
                    CodexStatusText = string.IsNullOrWhiteSpace(result.Message)
                        ? Localize("AiBackendUnavailable", "Codex app-server is not connected yet.")
                        : result.Message;
                    return;
                }
                _codexRuntimeMissing = false;
                OnPropertyChanged("CheckUpdateText");
                OnPropertyChanged("InstallUpdateText");
                ApplyStatus(result.Data);
            });
        }

        private void ApplyStatus(CodexStatusSnapshot status)
        {
            CodexStatusText = FormatStatus(status);
            if (status.Runtime != null)
            {
                RuntimePath = status.Runtime.ExecutablePath;
                RuntimeVersion = status.Runtime.Version;
            }
            else
                RuntimeVersion = status.Version;
            _isLoggedIn = status.Auth != null && status.Auth.LoggedIn;
            OnPropertyChanged("LoginVisibility");
            OnPropertyChanged("LogoutVisibility");
        }

        private async void CheckForUpdate()
        {
            CodexBusy = true;
            BackendResult<CodexRuntimeUpdateInfo> result;
            try { result = await _backend.CheckForUpdateAsync(CancellationToken.None); }
            catch (Exception ex) { result = BackendResult<CodexRuntimeUpdateInfo>.Fail("update_check_failed", ex.Message); }
            finally { OnUi(() => CodexBusy = false); }

            OnUi(() =>
            {
                _pendingUpdate = result.Success ? result.Data : null;
                if (!result.Success)
                {
                    UpdateText = BackendMessage(result.Message);
                    return;
                }
                UpdateText = result.Data != null && result.Data.UpdateAvailable
                    ? string.Format(Localize("AiUpdateAvailable", "Codex {0} is available."), result.Data.LatestVersion)
                    : Localize("AiNoUpdate", "Codex is up to date.");
                OnPropertyChanged("UpdateVisibility");
            });
        }

        private async void InstallUpdate()
        {
            var update = _pendingUpdate;
            if (update == null || !update.UpdateAvailable)
                return;
            var prompt = string.Format(Localize("AiInstallUpdateConfirm", "Install Codex {0}? NEW_CLEVER will restart the app-server after installation."), update.LatestVersion);
            if (MessageBox.Show(prompt, Localize("AiInstallUpdate", "Install update"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            CodexBusy = true;
            BackendResult<CodexRuntimeInfo> result;
            try { result = await _backend.InstallUpdateAsync(update, CancellationToken.None); }
            catch (Exception ex) { result = BackendResult<CodexRuntimeInfo>.Fail("update_install_failed", ex.Message); }
            finally { OnUi(() => CodexBusy = false); }
            OnUi(() =>
            {
                if (!result.Success) { UpdateText = BackendMessage(result.Message); return; }
                _pendingUpdate = null;
                UpdateText = Localize("AiUpdateInstalled", "Codex update installed.");
                OnPropertyChanged("UpdateVisibility");
                RestartCodex();
            });
        }

        private async void StartLogin()
        {
            CodexBusy = true;
            BackendResult<CodexLoginStartResult> result;
            try { result = await _backend.StartChatGptLoginAsync(CancellationToken.None); }
            catch (Exception ex) { result = BackendResult<CodexLoginStartResult>.Fail("login_failed", ex.Message); }
            finally { OnUi(() => CodexBusy = false); }
            OnUi(() =>
            {
                if (!result.Success || result.Data == null)
                {
                    CodexStatusText = BackendMessage(result.Message);
                    return;
                }
                _loginId = result.Data.LoginId;
                AuthorizationUrl = !string.IsNullOrWhiteSpace(result.Data.AuthorizationUrl) ? result.Data.AuthorizationUrl : result.Data.VerificationUrl;
                OnPropertyChanged("LoginVisibility");
                OpenAuthorizationUrl();
            });
        }

        private async void CancelLogin()
        {
            var loginId = _loginId;
            if (string.IsNullOrWhiteSpace(loginId))
                return;
            try
            {
                var result = await _backend.CancelLoginAsync(loginId, CancellationToken.None);
                OnUi(() =>
                {
                    if (!result.Success) CodexStatusText = BackendMessage(result.Message);
                    else ClearLoginState();
                });
            }
            catch (Exception ex) { OnUi(() => CodexStatusText = BackendMessage(ex.Message)); }
        }

        private async void Logout()
        {
            if (MessageBox.Show(Localize("AiLogoutConfirm", "Sign out of ChatGPT in Codex?"), LogoutText, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            try
            {
                var result = await _backend.LogoutAsync(CancellationToken.None);
                OnUi(() =>
                {
                    if (!result.Success) CodexStatusText = BackendMessage(result.Message);
                    else
                    {
                        _isLoggedIn = false;
                        ClearLoginState();
                        RefreshCodexStatus();
                    }
                });
            }
            catch (Exception ex) { OnUi(() => CodexStatusText = BackendMessage(ex.Message)); }
        }

        private void OpenAuthorizationUrl()
        {
            if (string.IsNullOrWhiteSpace(AuthorizationUrl))
                return;
            try { Process.Start(new ProcessStartInfo(AuthorizationUrl) { UseShellExecute = true }); }
            catch
            {
                CopyAuthorizationUrl();
                CodexStatusText = Localize("AiLoginLinkCopiedFallback", "The browser could not be opened. The sign-in link was copied.");
            }
        }

        private void CopyAuthorizationUrl()
        {
            if (string.IsNullOrWhiteSpace(AuthorizationUrl))
                return;
            try { Clipboard.SetText(AuthorizationUrl); }
            catch (Exception ex) { CodexStatusText = ex.Message; }
        }

        private void ClearLoginState()
        {
            _loginId = null;
            AuthorizationUrl = "";
            OnPropertyChanged("LoginVisibility");
        }

        private string BackendMessage(string message)
        {
            return string.IsNullOrWhiteSpace(message) ? Localize("AiBackendUnavailable", "Codex app-server is not connected yet.") : message;
        }

        private string FormatStatus(CodexStatusSnapshot status)
        {
            var lines = new List<string>
            {
                Localize("AiStatusConnection", "Connection") + ": " + status.ConnectionState
            };
            var version = status.Runtime == null ? status.Version : status.Runtime.Version;
            if (!string.IsNullOrWhiteSpace(version)) lines.Add(Localize("AiStatusVersion", "Version") + ": " + version);
            var account = status.Auth == null ? status.Account : status.Auth.Email;
            var plan = status.Auth == null ? status.PlanType : status.Auth.PlanType;
            if (!string.IsNullOrWhiteSpace(account)) lines.Add(Localize("AiStatusAccount", "Account") + ": " + account);
            if (!string.IsNullOrWhiteSpace(plan)) lines.Add(Localize("AiStatusPlan", "Plan") + ": " + plan);
            if (status.ContextUsedPercent.HasValue) lines.Add(Localize("AiStatusContext", "Context") + ": " + status.ContextUsedPercent.Value + "%");
            var primary = status.RateLimits != null && status.RateLimits.Primary != null ? (int?)status.RateLimits.Primary.UsedPercent : status.PrimaryRateLimitUsedPercent;
            var secondary = status.RateLimits != null && status.RateLimits.Secondary != null ? (int?)status.RateLimits.Secondary.UsedPercent : status.SecondaryRateLimitUsedPercent;
            if (primary.HasValue) lines.Add(Localize("AiStatusPrimaryLimit", "Primary limit used") + ": " + primary.Value + "%");
            if (secondary.HasValue) lines.Add(Localize("AiStatusSecondaryLimit", "Secondary limit used") + ": " + secondary.Value + "%");
            if (!string.IsNullOrWhiteSpace(status.Message)) lines.Add(status.Message);
            return string.Join("\n", lines);
        }

        private void Backend_StateChanged(object sender, EventArgs e) { OnUi(RefreshCodexStatus); }

        private void Backend_EventReceived(object sender, CodexBackendEventArgs e)
        {
            if (e == null)
                return;
            OnUi(() =>
            {
                if (e.Kind == CodexBackendEventKind.AccountUpdated && e.Account != null)
                {
                    _isLoggedIn = e.Account.LoggedIn;
                    if (_isLoggedIn) ClearLoginState();
                    OnPropertyChanged("LoginVisibility");
                    OnPropertyChanged("LogoutVisibility");
                }
                if (e.Kind == CodexBackendEventKind.ConnectionChanged || e.Kind == CodexBackendEventKind.RateLimitsUpdated || e.Kind == CodexBackendEventKind.AccountUpdated)
                    RefreshCodexStatus();
                else if (e.Kind == CodexBackendEventKind.Error && !string.IsNullOrWhiteSpace(e.Message))
                    CodexStatusText = e.Message;
            });
        }

        private void OnUi(Action action)
        {
            if (_disposed || action == null)
                return;
            var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
            Action safeAction = () =>
            {
                if (_disposed)
                    return;
                try { action(); }
                catch (Exception ex)
                {
                    Debug.WriteLine("Unhandled Codex settings UI callback: " + ex);
                    CodexStatusText = BackendMessage(ex.Message);
                }
            };
            try
            {
                if (dispatcher == null || dispatcher.CheckAccess()) safeAction();
                else dispatcher.BeginInvoke(safeAction);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Unable to schedule Codex settings UI callback: " + ex);
            }
        }

        private void SetStatus(string key, string fallback, bool isError, params object[] args)
        {
            var message = Localize(key, fallback);
            if (args != null && args.Length > 0) message = string.Format(message, args);
            StatusIsError = isError;
            StatusText = message;
        }

        private static string Localize(string key, string fallback)
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
        }
    }
}
