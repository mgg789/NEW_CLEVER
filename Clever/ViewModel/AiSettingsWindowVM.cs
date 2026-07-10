using Clever.Model.Utils;
using Clever.ViewModel.BaseVM;
using System;
using System.Windows;
using System.Windows.Input;

namespace Clever.ViewModel
{
    internal class AiSettingsWindowVM : BaseViewModel
    {
        private string _embeddedUrl;
        private string _embeddedModel;
        private string _embeddedToken;
        private string _statusText;
        private bool _statusIsError;

        public AiSettingsWindowVM()
        {
            var settings = AiAppSettingsStore.Settings;
            _embeddedUrl = settings.EmbeddedUrl;
            _embeddedModel = settings.EmbeddedModel;
            _embeddedToken = AiAppSettingsStore.EmbeddedToken;

            SaveEmbeddedCommand = new RelayCommand(param => SaveEmbedded(), true);
            ClearEmbeddedCommand = new RelayCommand(param => ClearEmbedded(), true);
            ToggleCodexConnectionCommand = new RelayCommand(param => ToggleCodexConnection(), true);
        }

        public ICommand SaveEmbeddedCommand { get; private set; }
        public ICommand ClearEmbeddedCommand { get; private set; }
        public ICommand ToggleCodexConnectionCommand { get; private set; }

        public string WindowTitle { get { return Localize("AiSettingsTitle", "AI settings"); } }
        public string EnableAiText { get { return Localize("AiSettingsEnableFunctions", "Enable AI functions"); } }
        public string EmbeddedModelText { get { return Localize("AiSettingsEmbeddedModel", "OpenAI-compatible model"); } }
        public string UrlText { get { return Localize("AiSettingsUrl", "URL"); } }
        public string ModelText { get { return Localize("AiSettingsModel", "Model"); } }
        public string TokenText { get { return Localize("AiSettingsToken", "Token"); } }
        public string SaveText { get { return Localize("AiSettingsSave", "Save"); } }
        public string ClearText { get { return Localize("AiSettingsClear", "Clear"); } }
        public string CodexText { get { return Localize("AiSettingsCodex", "Codex"); } }

        public bool AiEnabled
        {
            get { return AiAppSettingsStore.Settings.AiEnabled; }
            set
            {
                var settings = AiAppSettingsStore.Settings;
                if (settings.AiEnabled == value)
                    return;

                var previousValue = settings.AiEnabled;
                settings.AiEnabled = value;
                try
                {
                    AiAppSettingsStore.SaveSettings();
                }
                catch (Exception ex)
                {
                    settings.AiEnabled = previousValue;
                    SetStatus("AiSettingsSaveFailed", "Could not save AI settings: {0}", true, ex.Message);
                }
                OnPropertyChanged("AiEnabled");
            }
        }

        public string EmbeddedUrl
        {
            get { return _embeddedUrl; }
            set
            {
                _embeddedUrl = value;
                OnPropertyChanged("EmbeddedUrl");
            }
        }

        public string EmbeddedModel
        {
            get { return _embeddedModel; }
            set
            {
                _embeddedModel = value;
                OnPropertyChanged("EmbeddedModel");
            }
        }

        public string EmbeddedToken
        {
            get { return _embeddedToken; }
            set
            {
                _embeddedToken = value;
                OnPropertyChanged("EmbeddedToken");
            }
        }

        public bool CodexConnected
        {
            get { return AiAppSettingsStore.Settings.CodexConnected; }
        }

        public string CodexButtonText
        {
            get
            {
                return CodexConnected
                    ? Localize("AiSettingsDisconnect", "Disconnect")
                    : Localize("AiSettingsConnect", "Connect");
            }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set
            {
                _statusText = value;
                OnPropertyChanged("StatusText");
            }
        }

        public bool StatusIsError
        {
            get { return _statusIsError; }
            private set
            {
                _statusIsError = value;
                OnPropertyChanged("StatusIsError");
            }
        }

        public Visibility CodexStatusVisibility
        {
            get { return Visibility.Visible; }
        }

        public string CodexStatusText
        {
            get
            {
                var settings = AiAppSettingsStore.Settings;
                var emailLabel = Localize("AiSettingsCodexEmail", "Email");
                var hoursLabel = Localize("AiSettingsCodexLimit5h", "5h");
                var daysLabel = Localize("AiSettingsCodexLimit7d", "7d");
                if (CodexConnected)
                    return emailLabel + ": " + settings.CodexEmail + "\n" + hoursLabel + ": " + settings.CodexLimit5h + "   " + daysLabel + ": " + settings.CodexLimit7d;

                return hoursLabel + ": " + settings.CodexLimit5h + "   " + daysLabel + ": " + settings.CodexLimit7d;
            }
        }

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

        private void ToggleCodexConnection()
        {
            var settings = AiAppSettingsStore.Settings;
            var previousConnected = settings.CodexConnected;
            var previousEmail = settings.CodexEmail;
            var previousLimit5h = settings.CodexLimit5h;
            var previousLimit7d = settings.CodexLimit7d;
            settings.CodexConnected = !settings.CodexConnected;
            if (settings.CodexConnected)
            {
                settings.CodexEmail = string.IsNullOrWhiteSpace(settings.CodexEmail) ? "student@example.com" : settings.CodexEmail;
                settings.CodexLimit5h = "18%";
                settings.CodexLimit7d = "7%";
            }
            else
            {
                settings.CodexEmail = "";
                settings.CodexLimit5h = "0%";
                settings.CodexLimit7d = "0%";
            }

            try
            {
                AiAppSettingsStore.SaveSettings();
            }
            catch (Exception ex)
            {
                settings.CodexConnected = previousConnected;
                settings.CodexEmail = previousEmail;
                settings.CodexLimit5h = previousLimit5h;
                settings.CodexLimit7d = previousLimit7d;
                SetStatus("AiSettingsSaveFailed", "Could not save AI settings: {0}", true, ex.Message);
            }
            OnPropertyChanged("CodexConnected");
            OnPropertyChanged("CodexButtonText");
            OnPropertyChanged("CodexStatusVisibility");
            OnPropertyChanged("CodexStatusText");
        }

        private void SetStatus(string key, string fallback, bool isError, params object[] args)
        {
            var message = Localize(key, fallback);
            if (args != null && args.Length > 0)
                message = string.Format(message, args);

            StatusIsError = isError;
            StatusText = message;
        }

        private static string Localize(string key, string fallback)
        {
            string value;
            return MainWindowVM.GetLocalization != null
                && MainWindowVM.GetLocalization.TryGetValue(key, out value)
                && !string.IsNullOrWhiteSpace(value)
                    ? value
                    : fallback;
        }
    }
}
