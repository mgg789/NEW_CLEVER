using OpenAI;
using OpenAI.Chat;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEW_CLEVER.Model.Utils
{
    internal interface IAiTextGenerationService
    {
        Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken cancellationToken);
        Task<string> GenerateInlineCompletionAsync(AiInlineCompletionContext context, CancellationToken cancellationToken);
        Task<AiBugFixResponse> GenerateBugFixAsync(AiBugFixRequest request, CancellationToken cancellationToken);
    }

    internal sealed class AiInlineCompletionContext
    {
        internal string FilePath { get; set; }
        internal string FileType { get; set; }
        internal string Prefix { get; set; }
        internal string Suffix { get; set; }
        internal int CaretPosition { get; set; }
        internal string LinkedContext { get; set; }
        internal string LanguageReference { get; set; }
    }

    internal sealed class AiConfigurationException : InvalidOperationException
    {
        internal AiConfigurationException(string message) : base(message)
        {
        }
    }

    internal sealed class OpenAiTextGenerationService : IAiTextGenerationService
    {
        private static readonly Lazy<OpenAiTextGenerationService> LazyInstance =
            new Lazy<OpenAiTextGenerationService>(() => new OpenAiTextGenerationService());

        private readonly object _clientLock = new object();
        private ChatClient _client;
        private string _clientSignature;

        private OpenAiTextGenerationService()
        {
        }

        internal static OpenAiTextGenerationService Instance
        {
            get { return LazyInstance.Value; }
        }

        public async Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(diff))
                throw new ArgumentException("A Git diff is required.", "diff");

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(BuildCommitSystemPrompt(language)),
                new UserChatMessage("Generate a commit message for this saved working-tree diff:\n\n" + diff)
            };

            Exception lastError = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var text = await CompleteAsync(messages, 20, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    return CleanCommitMessage(text, language);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ClientResultException ex)
                {
                    lastError = ex;
                    if (attempt > 0 || (ex.Status != 429 && ex.Status < 500))
                        throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt > 0 || !IsTransientFailure(ex))
                        throw;
                }
            }

            throw lastError ?? new InvalidOperationException("The AI provider returned no result.");
        }

        public async Task<string> GenerateInlineCompletionAsync(AiInlineCompletionContext context, CancellationToken cancellationToken)
        {
            if (context == null)
                throw new ArgumentNullException("context");

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(BuildCompletionSystemPrompt(context.LanguageReference)),
                new UserChatMessage(BuildCompletionUserPrompt(context))
            };

            var text = await CompleteAsync(messages, 160, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            return CleanInlineCompletion(text, context.Prefix, context.Suffix);
        }

        /// <summary>
        /// Requests a text-only repair plan. The model receives source snapshots supplied by the
        /// caller and has no function/tool definitions, file system access, or direct editor access.
        /// Its untrusted JSON result must still pass <see cref="AiBugFixPlanValidator"/> before use.
        /// </summary>
        public async Task<AiBugFixResponse> GenerateBugFixAsync(AiBugFixRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
                throw new ArgumentNullException("request");
            if (request.Files == null || request.Files.Count == 0)
                throw new ArgumentException("At least one source snapshot is required.", "request");
            if (request.Diagnostics == null || request.Diagnostics.Count == 0)
                throw new ArgumentException("At least one diagnostic is required.", "request");

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(AiBugFixPromptBuilder.BuildSystemPrompt()),
                new UserChatMessage(AiBugFixPromptBuilder.BuildUserPrompt(request))
            };
            var rawResponse = await CompleteAsync(messages, 2200, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return AiBugFixResponseParser.Parse(rawResponse);
        }

        private async Task<string> CompleteAsync(IList<ChatMessage> messages, int maxTokens, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var client = GetClient();
            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = maxTokens,
                Temperature = 0.1f
            };

            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutSource.CancelAfter(timeout);
                ClientResult<ChatCompletion> result;
                try
                {
                    result = await client.CompleteChatAsync(messages, options, timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw;
                    throw new TimeoutException("The AI provider request timed out.", ex);
                }
                if (result == null || result.Value == null || result.Value.Content == null)
                    return "";

                var builder = new StringBuilder();
                foreach (var part in result.Value.Content)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                        builder.Append(part.Text);
                }
                return builder.ToString();
            }
        }

        private ChatClient GetClient()
        {
            var settings = AiAppSettingsStore.Settings;
            var url = (settings.EmbeddedUrl ?? "").Trim();
            var model = (settings.EmbeddedModel ?? "").Trim();
            var token = AiAppSettingsStore.EmbeddedToken;
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(token))
                throw new AiConfigurationException("AI provider URL, model and token are required.");

            Uri endpoint;
            if (!Uri.TryCreate(url, UriKind.Absolute, out endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
                throw new AiConfigurationException("The AI provider URL must be a valid HTTPS address.");

            var signature = url + "\n" + model + "\n" + token;
            lock (_clientLock)
            {
                if (_client != null && string.Equals(_clientSignature, signature, StringComparison.Ordinal))
                    return _client;

                var options = new OpenAIClientOptions
                {
                    Endpoint = endpoint,
                    NetworkTimeout = TimeSpan.FromSeconds(30),
                    RetryPolicy = new ClientRetryPolicy(0)
                };
                _client = new ChatClient(model, new ApiKeyCredential(token), options);
                _clientSignature = signature;
                return _client;
            }
        }

        private static bool IsTransientFailure(Exception exception)
        {
            if (exception is TimeoutException || exception is HttpRequestException || exception is IOException)
                return true;

            var clientError = exception as ClientResultException;
            return clientError != null && (clientError.Status == 429 || clientError.Status >= 500);
        }

        private static string BuildCommitSystemPrompt(string language)
        {
            var targetLanguage = "English";
            if (string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase))
                targetLanguage = "Russian";
            else if (string.Equals(language, "ua", StringComparison.OrdinalIgnoreCase))
                targetLanguage = "Ukrainian";

            return "You write short Git commit titles. Return exactly one imperative title in " + targetLanguage + "."
                + " Use 3 to 7 words and never exceed 50 characters."
                + " Summarize only the single main purpose or outcome of the change."
                + " Do not enumerate individual edits, files, functions, calls, or secondary details."
                + " When the diff contains several related edits, generalize them under their common intent."
                + " Do not use quotes, Markdown, explanations, lists, trailing punctuation, or Conventional Commits prefixes.";
        }

        private static string BuildCompletionSystemPrompt(string languageReference)
        {
            return "You are an inline Fill-in-the-Middle completion engine for NEW_CLEVER BP, a BASIC-like language for LEGO EV3 robots."
                + " Return only the exact source text to insert at the cursor. Never use Markdown fences, commentary, placeholders, or repeat the prefix/suffix."
                + " Preserve local indentation, naming, casing and style. Prefer a small high-confidence completion over unrelated code."
                + " The following compact reference is authoritative:\n\n" + (languageReference ?? "");
        }

        private static string BuildCompletionUserPrompt(AiInlineCompletionContext context)
        {
            var builder = new StringBuilder();
            builder.AppendLine("<file path=\"").Append(EscapeAttribute(context.FilePath)).Append("\" type=\"")
                .Append(EscapeAttribute(context.FileType)).AppendLine("\">");
            builder.AppendLine("<prefix>").Append(context.Prefix ?? "").AppendLine("</prefix>");
            builder.AppendLine("<suffix>").Append(context.Suffix ?? "").AppendLine("</suffix>");
            if (!string.IsNullOrWhiteSpace(context.LinkedContext))
                builder.AppendLine("<linked-context>").Append(context.LinkedContext).AppendLine("</linked-context>");
            builder.AppendLine("</file>");
            return builder.ToString();
        }

        private static string CleanCommitMessage(string text, string language)
        {
            var cleaned = StripCodeFence(text, false).Replace("\r", "").Trim();
            var line = cleaned.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            line = line.Trim().Trim('"', '\'', '`').TrimEnd('.', ';');
            line = TruncateCommitTitle(line, 50, GetCommitFallback(language));
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidOperationException("The AI provider returned an empty commit message.");
            return line;
        }

        private static string TruncateCommitTitle(string value, int maxLength, string fallback)
        {
            value = (value ?? "").Trim();
            if (value.Length <= maxLength)
                return value;

            var boundary = value.LastIndexOfAny(new[] { ' ', '\t' }, maxLength - 1);
            if (boundary >= Math.Min(24, maxLength / 2))
            {
                var result = value.Substring(0, boundary).TrimEnd();
                var danglingWords = new[] { "and", "or", "и", "или", "та", "або" };
                var lastSpace = result.LastIndexOf(' ');
                var lastWord = lastSpace >= 0 ? result.Substring(lastSpace + 1) : result;
                if (danglingWords.Contains(lastWord, StringComparer.OrdinalIgnoreCase))
                    result = lastSpace > 0 ? result.Substring(0, lastSpace).TrimEnd() : "";
                return string.IsNullOrWhiteSpace(result) ? fallback : result;
            }

            return fallback;
        }

        private static string GetCommitFallback(string language)
        {
            if (string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase))
                return "Обновить проект";
            if (string.Equals(language, "ua", StringComparison.OrdinalIgnoreCase))
                return "Оновити проєкт";
            return "Update project";
        }

        private static string CleanInlineCompletion(string text, string prefix, string suffix)
        {
            var cleaned = StripCodeFence(text, true).Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
            if (cleaned.StartsWith("<completion>", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned.Substring(12);
            if (cleaned.EndsWith("</completion>", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned.Substring(0, cleaned.Length - 13);

            var prefixTail = prefix ?? "";
            var maxPrefixOverlap = Math.Min(cleaned.Length, Math.Min(prefixTail.Length, 256));
            for (var length = maxPrefixOverlap; length > 0; length--)
            {
                if (prefixTail.EndsWith(cleaned.Substring(0, length), StringComparison.Ordinal))
                {
                    cleaned = cleaned.Substring(length);
                    break;
                }
            }

            var suffixHead = suffix ?? "";
            var maxSuffixOverlap = Math.Min(cleaned.Length, Math.Min(suffixHead.Length, 256));
            for (var length = maxSuffixOverlap; length > 0; length--)
            {
                if (suffixHead.StartsWith(cleaned.Substring(cleaned.Length - length), StringComparison.Ordinal))
                {
                    cleaned = cleaned.Substring(0, cleaned.Length - length);
                    break;
                }
            }

            if (cleaned.Length > 2048)
                cleaned = cleaned.Substring(0, 2048);
            return cleaned;
        }

        private static string StripCodeFence(string text, bool preserveLeadingWhitespace)
        {
            var original = text ?? "";
            var value = original.Trim();
            if (!value.StartsWith("```", StringComparison.Ordinal))
                return preserveLeadingWhitespace ? original.TrimEnd() : value;

            var firstLineEnd = value.IndexOf('\n');
            if (firstLineEnd >= 0)
                value = value.Substring(firstLineEnd + 1);
            if (value.EndsWith("```", StringComparison.Ordinal))
                value = value.Substring(0, value.Length - 3);
            return preserveLeadingWhitespace ? value.TrimEnd() : value.Trim();
        }

        private static string EscapeAttribute(string value)
        {
            return (value ?? "").Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
