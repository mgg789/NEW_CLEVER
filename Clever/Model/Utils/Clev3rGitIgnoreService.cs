using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Clever.Model.Utils
{
    internal static class Clev3rGitIgnoreService
    {
        private const string Header = "# CLEV3R EV3 build output";
        private const string BuildFolderRule = "~*/";

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
            if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
                return false;

            var gitIgnorePath = Path.Combine(repositoryRoot, ".gitignore");
            var text = File.Exists(gitIgnorePath)
                ? File.ReadAllText(gitIgnorePath, Encoding.UTF8)
                : "";

            var hasRule = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Any(line => line.Trim() == BuildFolderRule);
            if (hasRule)
                return false;

            var builder = new StringBuilder(text);
            if (builder.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal))
                builder.AppendLine();
            if (builder.Length > 0)
                builder.AppendLine();
            builder.AppendLine(Header);
            builder.AppendLine(BuildFolderRule);

            File.WriteAllText(gitIgnorePath, builder.ToString(), new UTF8Encoding(false));
            return true;
        }
    }
}
