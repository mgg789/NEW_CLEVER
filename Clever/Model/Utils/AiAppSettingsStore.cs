using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace Clever.Model.Utils
{
    public static class AiAppSettingsStore
    {
        private const string ProtectedPrefix = "dpapi:";
        private static readonly string BaseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CLEV3R");
        private static readonly string SettingsPath = Path.Combine(BaseDirectory, "ai_settings.xml");
        private static readonly string ChatsPath = Path.Combine(BaseDirectory, "ai_chats.xml");
        private static AiAppSettingsData _settings;

        public static event EventHandler SettingsChanged;

        static AiAppSettingsStore()
        {
            _settings = LoadXml(SettingsPath, new AiAppSettingsData());
        }

        public static AiAppSettingsData Settings
        {
            get { return _settings; }
        }

        public static string EmbeddedToken
        {
            get { return Unprotect(_settings.EmbeddedTokenProtected); }
            set { _settings.EmbeddedTokenProtected = Protect(value); }
        }

        public static void SaveSettings()
        {
            SaveXml(SettingsPath, _settings);
            var handler = SettingsChanged;
            if (handler != null)
                handler(null, EventArgs.Empty);
        }

        public static void ClearEmbeddedSettings()
        {
            _settings.EmbeddedUrl = "";
            _settings.EmbeddedModel = "";
            _settings.EmbeddedTokenProtected = "";
            SaveSettings();
        }

        public static List<AiChatSessionData> LoadChatSessions()
        {
            var data = LoadXml(ChatsPath, new AiChatHistoryData());
            return data.Sessions ?? new List<AiChatSessionData>();
        }

        public static void SaveChatSession(AiChatSessionData session)
        {
            if (session == null)
                return;

            var sessions = LoadChatSessions();
            var existing = sessions.FirstOrDefault(item => item.Id == session.Id);
            if (existing != null)
                sessions.Remove(existing);
            sessions.Insert(0, session);
            SaveChatSessions(sessions);
        }

        public static void SaveChatSessions(IList<AiChatSessionData> sessions)
        {
            SaveXml(ChatsPath, new AiChatHistoryData { Sessions = sessions == null ? new List<AiChatSessionData>() : sessions.ToList() });
        }

        public static void ClearChatSessions()
        {
            SaveChatSessions(new List<AiChatSessionData>());
        }

        private static T LoadXml<T>(string path, T fallback)
        {
            try
            {
                if (!File.Exists(path))
                    return fallback;

                using (var stream = File.OpenRead(path))
                {
                    var serializer = new XmlSerializer(typeof(T));
                    var value = serializer.Deserialize(stream);
                    return value is T ? (T)value : fallback;
                }
            }
            catch
            {
                return fallback;
            }
        }

        private static void SaveXml<T>(string path, T value)
        {
            Directory.CreateDirectory(BaseDirectory);
            using (var stream = File.Create(path))
            {
                var serializer = new XmlSerializer(typeof(T));
                serializer.Serialize(stream, value);
            }
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            try
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return ProtectedPrefix + Convert.ToBase64String(protectedBytes);
            }
            catch
            {
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            }
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            try
            {
                if (value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
                {
                    var protectedBytes = Convert.FromBase64String(value.Substring(ProtectedPrefix.Length));
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
                }

                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return "";
            }
        }
    }

    public class AiAppSettingsData
    {
        public AiAppSettingsData()
        {
            AiEnabled = true;
            EmbeddedUrl = "";
            EmbeddedModel = "";
            EmbeddedTokenProtected = "";
            CodexConnected = false;
            CodexEmail = "";
            CodexLimit5h = "0%";
            CodexLimit7d = "0%";
            WheelDiameter = "56";
            WheelBaseWidth = "120";
            RobotDescription = "";
        }

        public bool AiEnabled { get; set; }
        public string EmbeddedUrl { get; set; }
        public string EmbeddedModel { get; set; }
        public string EmbeddedTokenProtected { get; set; }
        public bool CodexConnected { get; set; }
        public string CodexEmail { get; set; }
        public string CodexLimit5h { get; set; }
        public string CodexLimit7d { get; set; }
        public string WheelDiameter { get; set; }
        public string WheelBaseWidth { get; set; }
        public string RobotDescription { get; set; }
    }

    public class AiChatHistoryData
    {
        public AiChatHistoryData()
        {
            Sessions = new List<AiChatSessionData>();
        }

        public List<AiChatSessionData> Sessions { get; set; }
    }

    public class AiChatSessionData
    {
        public AiChatSessionData()
        {
            Id = Guid.NewGuid().ToString("N");
            Title = "";
            Subtitle = "";
            Messages = new List<AiChatMessageData>();
        }

        public string Id { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public List<AiChatMessageData> Messages { get; set; }
    }

    public class AiChatMessageData
    {
        public string Markdown { get; set; }
        public bool IsUser { get; set; }
    }
}
