using System;
using System.IO;
using System.Linq;
using System.Text;

namespace NEW_CLEVER.Model.Utils
{
    internal static class NEW_CLEVERGitIgnoreService
    {
        private const string Header = "# NEW_CLEVER EV3 build output";
        private const string BuildFolderRule = "~*/";
        private const string AttachmentsHeader = "# NEW_CLEVER Codex pinned attachments";
        private const string AttachmentsFolderRule = ".clev3r/attachments/";

        internal static bool EnsureForCompiledFile(string sourceFilePath)
        {
            try
            {
                var repositoryRoot = GitRepositoryService.FindRepositoryRoot(sourceFilePath);
                if (string.IsNullOrWhiteSpace(repositoryRoot))
                    return false;

                return EnsureInRepository(repositoryRoot);
            }
            catch
            {
                return false;
            }
        }

        internal static bool EnsureInRepository(string repositoryRoot)
        {
            return EnsureRule(repositoryRoot, Header, BuildFolderRule, false);
        }

        /// <summary>
        /// Ensures that snapshots imported for a Codex attachment stay outside source control.
        /// Unlike the EV3 helper, an existing rule is a successful result because callers need
        /// to know that it is safe to place a file in the folder.
        /// </summary>
        internal static bool EnsureAttachmentsFolderIgnored(string projectRoot)
        {
            return EnsureRule(projectRoot, AttachmentsHeader, AttachmentsFolderRule, true);
        }

        private static bool EnsureRule(string root, string header, string rule, bool existingRuleIsSuccess)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return false;

            var gitIgnorePath = Path.Combine(root, ".gitignore");
            var text = File.Exists(gitIgnorePath)
                ? File.ReadAllText(gitIgnorePath, Encoding.UTF8)
                : "";

            var hasRule = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Any(line => line.Trim() == rule);
            if (hasRule)
                return existingRuleIsSuccess;

            var builder = new StringBuilder(text);
            if (builder.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal))
                builder.AppendLine();
            if (builder.Length > 0)
                builder.AppendLine();
            builder.AppendLine(header);
            builder.AppendLine(rule);

            File.WriteAllText(gitIgnorePath, builder.ToString(), new UTF8Encoding(false));
            return true;
        }
    }
}
