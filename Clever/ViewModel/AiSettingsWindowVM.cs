using Clever.Model.Utils;
using Clever.ViewModel.BaseVM;
using System.Windows;
using System.Windows.Input;

namespace Clever.ViewModel
{
    internal class AiSettingsWindowVM : BaseViewModel
    {
        private string _embeddedUrl;
        private string _embeddedModel;
        private string _embeddedToken;

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

        public bool AiEnabled
        {
            get { return AiAppSettingsStore.Settings.AiEnabled; }
            set
            {
                AiAppSettingsStore.Settings.AiEnabled = value;
                AiAppSettingsStore.SaveSettings();
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
            get { return CodexConnected ? "Отключить" : "Подключить"; }
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
                if (CodexConnected)
                    return "Email: " + settings.CodexEmail + "\n5h: " + settings.CodexLimit5h + "   7d: " + settings.CodexLimit7d;

                return "5h: " + settings.CodexLimit5h + "   7d: " + settings.CodexLimit7d;
            }
        }

        private void SaveEmbedded()
        {
            var settings = AiAppSettingsStore.Settings;
            settings.EmbeddedUrl = EmbeddedUrl ?? "";
            settings.EmbeddedModel = EmbeddedModel ?? "";
            AiAppSettingsStore.EmbeddedToken = EmbeddedToken ?? "";
            AiAppSettingsStore.SaveSettings();
        }

        private void ClearEmbedded()
        {
            AiAppSettingsStore.ClearEmbeddedSettings();
            EmbeddedUrl = "";
            EmbeddedModel = "";
            EmbeddedToken = "";
        }

        private void ToggleCodexConnection()
        {
            var settings = AiAppSettingsStore.Settings;
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

            AiAppSettingsStore.SaveSettings();
            OnPropertyChanged("CodexConnected");
            OnPropertyChanged("CodexButtonText");
            OnPropertyChanged("CodexStatusVisibility");
            OnPropertyChanged("CodexStatusText");
        }
    }
}
