using System;
using System.Collections.Generic;
using System.Linq;

namespace Clev3r.AgentCore
{
    public enum SlashCommandCapability
    {
        Models,
        Reasoning,
        Speed,
        Goals,
        Compact,
        Status
    }

    public sealed class SlashCommandDefinition
    {
        public string Name { get; set; }
        public string TitleKey { get; set; }
        public string DescriptionKey { get; set; }
        public SlashCommandCapability Capability { get; set; }
    }

    public sealed class SlashCommandMatch
    {
        public SlashCommandDefinition Command { get; set; }
        public bool IsAvailable { get; set; }
        public string UnavailableReason { get; set; }
    }

    public sealed class SlashCommandCatalog
    {
        private readonly IList<SlashCommandDefinition> _commands;

        public SlashCommandCatalog()
        {
            _commands = new List<SlashCommandDefinition>
            {
                New("model", "AiSlashModel", "AiSlashModelDescription", SlashCommandCapability.Models),
                New("reasoning", "AiSlashReasoning", "AiSlashReasoningDescription", SlashCommandCapability.Reasoning),
                New("speed", "AiSlashSpeed", "AiSlashSpeedDescription", SlashCommandCapability.Speed),
                New("goal", "AiSlashGoal", "AiSlashGoalDescription", SlashCommandCapability.Goals),
                New("compact", "AiSlashCompact", "AiSlashCompactDescription", SlashCommandCapability.Compact),
                New("status", "AiSlashStatus", "AiSlashStatusDescription", SlashCommandCapability.Status)
            };
        }

        public IList<SlashCommandDefinition> Commands
        {
            get { return _commands.ToList(); }
        }

        public IList<SlashCommandMatch> Filter(string composerText, CodexCapabilities capabilities)
        {
            string query;
            if (!TryGetQuery(composerText, out query))
                return new List<SlashCommandMatch>();

            return _commands
                .Where(item => item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .Select(item => new SlashCommandMatch
                {
                    Command = item,
                    IsAvailable = IsAvailable(item.Capability, capabilities),
                    UnavailableReason = IsAvailable(item.Capability, capabilities) ? "" : "Codex app-server capability is unavailable."
                })
                .ToList();
        }

        public bool TryParseExact(string composerText, out SlashCommandDefinition command, out string argument)
        {
            command = null;
            argument = "";
            if (string.IsNullOrWhiteSpace(composerText) || composerText[0] != '/')
                return false;

            var trimmed = composerText.Trim();
            var separator = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            var name = separator < 0 ? trimmed.Substring(1) : trimmed.Substring(1, separator - 1);
            command = _commands.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (command == null)
                return false;
            argument = separator < 0 ? "" : trimmed.Substring(separator).Trim();
            return true;
        }

        private static bool TryGetQuery(string text, out string query)
        {
            query = "";
            if (string.IsNullOrEmpty(text) || text[0] != '/')
                return false;
            var token = text.Substring(1);
            if (token.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0)
                return false;
            query = token;
            return true;
        }

        private static bool IsAvailable(SlashCommandCapability capability, CodexCapabilities capabilities)
        {
            capabilities = capabilities ?? CodexCapabilities.None();
            switch (capability)
            {
                case SlashCommandCapability.Models: return capabilities.Models;
                case SlashCommandCapability.Reasoning: return capabilities.Reasoning;
                case SlashCommandCapability.Speed: return capabilities.Speed;
                case SlashCommandCapability.Goals: return capabilities.Goals;
                case SlashCommandCapability.Compact: return capabilities.Compact;
                case SlashCommandCapability.Status: return capabilities.Status;
                default: return false;
            }
        }

        private static SlashCommandDefinition New(string name, string titleKey, string descriptionKey, SlashCommandCapability capability)
        {
            return new SlashCommandDefinition { Name = name, TitleKey = titleKey, DescriptionKey = descriptionKey, Capability = capability };
        }
    }
}
