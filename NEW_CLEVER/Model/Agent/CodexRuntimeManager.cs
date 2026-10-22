using NEW_CLEVER.AgentCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NEW_CLEVER.Model.Agent
{
    /// <summary>
    /// Resolves and updates the Codex runtime without consulting PATH. A custom runtime is
    /// authoritative; otherwise the active local update is preferred over the bundled copy.
    /// </summary>
    public sealed class CodexRuntimeManager : ICodexRuntimeManager, IDisposable
    {
        public const string MinimumVersion = "0.144.1";
        public const string WindowsAssetName = "codex-x86_64-pc-windows-msvc.exe.zip";

        private static readonly Uri DefaultReleaseApi =
            new Uri("https://api.github.com/repos/openai/codex/releases/latest", UriKind.Absolute);

        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly string _customExecutablePath;
        private readonly string _updatesRoot;
        private readonly string _bundledRoot;
        private readonly Uri _releaseApi;
        private readonly SemaphoreSlim _installGate = new SemaphoreSlim(1, 1);

        public CodexRuntimeManager(string customExecutablePath = null)
            : this(
                  CreateHttpClient(),
                  customExecutablePath,
                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLEV3R", "Codex", "runtimes"),
                  Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Codex", "runtime"),
                  DefaultReleaseApi,
                  true)
        {
        }

        /// <summary>Dependency-injection constructor used by deterministic updater tests.</summary>
        public CodexRuntimeManager(
            HttpClient http,
            string customExecutablePath,
            string updatesRoot,
            string bundledRoot,
            Uri releaseApi)
            : this(http, customExecutablePath, updatesRoot, bundledRoot, releaseApi, false)
        {
        }

        private CodexRuntimeManager(
            HttpClient http,
            string customExecutablePath,
            string updatesRoot,
            string bundledRoot,
            Uri releaseApi,
            bool ownsHttp)
        {
            if (http == null) throw new ArgumentNullException("http");
            if (string.IsNullOrWhiteSpace(updatesRoot)) throw new ArgumentException("An updates root is required.", "updatesRoot");
            if (string.IsNullOrWhiteSpace(bundledRoot)) throw new ArgumentException("A bundled root is required.", "bundledRoot");
            if (releaseApi == null) throw new ArgumentNullException("releaseApi");

            _http = http;
            _ownsHttp = ownsHttp;
            _customExecutablePath = NormalizeOptionalPath(customExecutablePath);
            _updatesRoot = Path.GetFullPath(updatesRoot);
            _bundledRoot = Path.GetFullPath(bundledRoot);
            _releaseApi = releaseApi;
        }

        public async Task<BackendResult<CodexRuntimeInfo>> ResolveAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(_customExecutablePath))
                return await InspectRuntimeAsync(_customExecutablePath, "custom", cancellationToken).ConfigureAwait(false);

            var active = ReadActiveRuntimePath();
            if (!string.IsNullOrWhiteSpace(active))
            {
                var inspected = await InspectRuntimeAsync(active, "updated", cancellationToken).ConfigureAwait(false);
                if (inspected.Success)
                    return inspected;
            }

            foreach (var candidate in EnumerateInstalledRuntimes())
            {
                var inspected = await InspectRuntimeAsync(candidate, "updated", cancellationToken).ConfigureAwait(false);
                if (inspected.Success)
                    return inspected;
            }

            var bundled = Path.Combine(_bundledRoot, "codex.exe");
            var bundledResult = await InspectRuntimeAsync(bundled, "bundled", cancellationToken).ConfigureAwait(false);
            if (bundledResult.Success)
                return bundledResult;

            return BackendResult<CodexRuntimeInfo>.Fail(
                "codex_runtime_missing",
                "A compatible Codex runtime was not found. Expected bundled runtime: " + bundled);
        }

        public async Task<BackendResult<CodexRuntimeUpdateInfo>> CheckForUpdateAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (var response = await _http.GetAsync(_releaseApi, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    using (var document = JsonDocument.Parse(json))
                    {
                        var root = document.RootElement;
                        var tag = GetString(root, "tag_name");
                        var latest = NormalizeVersion(tag);
                        if (string.IsNullOrWhiteSpace(latest))
                            return BackendResult<CodexRuntimeUpdateInfo>.Fail("invalid_release", "The official release has no valid version tag.");

                        JsonElement asset;
                        if (!TryFindAsset(root, WindowsAssetName, out asset))
                            return BackendResult<CodexRuntimeUpdateInfo>.Fail("runtime_asset_missing", "The official release has no Windows x64 Codex archive.");

                        var downloadUrl = GetString(asset, "browser_download_url");
                        if (!IsOfficialDownloadUrl(downloadUrl))
                            return BackendResult<CodexRuntimeUpdateInfo>.Fail("untrusted_download_url", "The release asset URL is not hosted by the official OpenAI GitHub repository.");

                        var sha = await ResolveOfficialSha256Async(root, asset, cancellationToken).ConfigureAwait(false);
                        if (!IsSha256(sha))
                            return BackendResult<CodexRuntimeUpdateInfo>.Fail("checksum_missing", "The official release does not publish a valid SHA-256 checksum for the Windows x64 archive.");

                        var current = await ResolveAsync(cancellationToken).ConfigureAwait(false);
                        var currentVersion = current.Success && current.Data != null ? current.Data.Version : null;
                        return BackendResult<CodexRuntimeUpdateInfo>.Ok(new CodexRuntimeUpdateInfo
                        {
                            CurrentVersion = currentVersion,
                            LatestVersion = latest,
                            UpdateAvailable = string.IsNullOrWhiteSpace(currentVersion) || CompareVersions(latest, currentVersion) > 0,
                            ReleaseNotes = GetString(root, "body") ?? "",
                            DownloadUrl = downloadUrl,
                            Sha256 = sha.ToLowerInvariant()
                        });
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return BackendResult<CodexRuntimeUpdateInfo>.Fail("update_check_failed", ex.Message);
            }
        }

        public async Task<BackendResult<CodexRuntimeInfo>> InstallUpdateAsync(CodexRuntimeUpdateInfo update, CancellationToken cancellationToken)
        {
            if (update == null)
                return BackendResult<CodexRuntimeInfo>.Fail("invalid_update", "Update metadata is required.");
            if (!IsOfficialDownloadUrl(update.DownloadUrl))
                return BackendResult<CodexRuntimeInfo>.Fail("untrusted_download_url", "Only official OpenAI Codex GitHub release assets can be installed.");
            if (!IsSha256(update.Sha256))
                return BackendResult<CodexRuntimeInfo>.Fail("invalid_checksum", "A valid SHA-256 checksum is required.");

            var version = NormalizeVersion(update.LatestVersion);
            if (string.IsNullOrWhiteSpace(version) || CompareVersions(version, MinimumVersion) < 0)
                return BackendResult<CodexRuntimeInfo>.Fail("incompatible_runtime", "Codex " + MinimumVersion + " or newer is required.");

            await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string staging = null;
            string archive = null;
            try
            {
                Directory.CreateDirectory(_updatesRoot);
                var previousRuntime = ReadActiveRuntimePath();
                if (string.IsNullOrWhiteSpace(previousRuntime))
                {
                    var bundledRuntime = Path.Combine(_bundledRoot, "codex.exe");
                    if (File.Exists(bundledRuntime)) previousRuntime = bundledRuntime;
                }
                staging = Path.Combine(_updatesRoot, ".install-" + Guid.NewGuid().ToString("N"));
                archive = Path.Combine(_updatesRoot, ".download-" + Guid.NewGuid().ToString("N") + ".zip");
                Directory.CreateDirectory(staging);

                await DownloadFileAsync(new Uri(update.DownloadUrl), archive, cancellationToken).ConfigureAwait(false);
                var actual = ComputeSha256(archive);
                if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    return BackendResult<CodexRuntimeInfo>.Fail("checksum_mismatch", "The downloaded Codex archive failed SHA-256 verification.");

                ExtractZipSafely(archive, staging);
                var executable = FindCodexExecutable(staging);
                if (executable == null)
                    return BackendResult<CodexRuntimeInfo>.Fail("runtime_executable_missing", "The verified archive does not contain codex.exe.");

                var canonicalExecutable = Path.Combine(staging, "codex.exe");
                if (!executable.Equals(canonicalExecutable, StringComparison.OrdinalIgnoreCase))
                    File.Move(executable, canonicalExecutable);

                var inspected = await InspectRuntimeAsync(canonicalExecutable, "updated", cancellationToken).ConfigureAwait(false);
                if (!inspected.Success || !string.Equals(inspected.Data.Version, version, StringComparison.OrdinalIgnoreCase))
                    return BackendResult<CodexRuntimeInfo>.Fail("runtime_verification_failed", "The extracted executable version does not match the selected release.");

                var finalDirectory = Path.Combine(_updatesRoot, version);
                if (Directory.Exists(finalDirectory))
                {
                    var existing = await InspectRuntimeAsync(Path.Combine(finalDirectory, "codex.exe"), "updated", cancellationToken).ConfigureAwait(false);
                    if (!existing.Success)
                        return BackendResult<CodexRuntimeInfo>.Fail("runtime_install_conflict", "The target runtime directory exists but is not usable.");
                    Directory.Delete(staging, true);
                    staging = null;
                }
                else
                {
                    Directory.Move(staging, finalDirectory);
                    staging = null;
                }

                WriteActiveRuntime(version, Path.Combine(finalDirectory, "codex.exe"), previousRuntime);
                return await InspectRuntimeAsync(Path.Combine(finalDirectory, "codex.exe"), "updated", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return BackendResult<CodexRuntimeInfo>.Fail("update_install_failed", ex.Message);
            }
            finally
            {
                TryDeleteFile(archive);
                TryDeleteDirectory(staging);
                _installGate.Release();
            }
        }

        /// <summary>Restores the previously active, still-installed runtime.</summary>
        public async Task<BackendResult<CodexRuntimeInfo>> RollbackAsync(CancellationToken cancellationToken)
        {
            var previousMarker = Path.Combine(_updatesRoot, "previous-runtime.json");
            var previous = ReadMarkerPath(previousMarker);
            if (string.IsNullOrWhiteSpace(previous))
                return BackendResult<CodexRuntimeInfo>.Fail("rollback_unavailable", "No previous Codex runtime is available.");

            var inspected = await InspectRuntimeAsync(previous, "updated", cancellationToken).ConfigureAwait(false);
            if (!inspected.Success)
                return BackendResult<CodexRuntimeInfo>.Fail("rollback_runtime_invalid", inspected.Message);

            WriteActiveRuntime(inspected.Data.Version, inspected.Data.ExecutablePath);
            return inspected;
        }

        private async Task<BackendResult<CodexRuntimeInfo>> InspectRuntimeAsync(string executable, string source, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                return BackendResult<CodexRuntimeInfo>.Fail("runtime_not_found", "Codex executable not found: " + (executable ?? ""));

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(executable),
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))
                };
                using (var process = Process.Start(start))
                using (var registration = cancellationToken.Register(delegate { TryKill(process); }))
                {
                    if (process == null)
                        return BackendResult<CodexRuntimeInfo>.Fail("runtime_start_failed", "Codex version process could not be started.");
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(15000))
                    {
                        TryKill(process);
                        return BackendResult<CodexRuntimeInfo>.Fail("runtime_version_timeout", "Codex did not report its version within 15 seconds.");
                    }
                    var output = (await stdoutTask.ConfigureAwait(false)) + " " + (await stderrTask.ConfigureAwait(false));
                    cancellationToken.ThrowIfCancellationRequested();
                    var version = NormalizeVersion(output);
                    if (string.IsNullOrWhiteSpace(version))
                        return BackendResult<CodexRuntimeInfo>.Fail("invalid_runtime_version", "Codex returned an unrecognized version: " + output.Trim());
                    var compatible = CompareVersions(version, MinimumVersion) >= 0;
                    var info = new CodexRuntimeInfo
                    {
                        ExecutablePath = Path.GetFullPath(executable),
                        Version = version,
                        Source = source,
                        Compatible = compatible
                    };
                    return compatible
                        ? BackendResult<CodexRuntimeInfo>.Ok(info)
                        : BackendResult<CodexRuntimeInfo>.Fail("incompatible_runtime", "Codex " + version + " is installed; " + MinimumVersion + " or newer is required.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return BackendResult<CodexRuntimeInfo>.Fail("runtime_inspection_failed", ex.Message);
            }
        }

        private async Task<string> ResolveOfficialSha256Async(JsonElement release, JsonElement asset, CancellationToken cancellationToken)
        {
            JsonElement sumsAsset;
            if (TryFindAsset(release, "codex-package_SHA256SUMS", out sumsAsset))
            {
                var sumsUrl = GetString(sumsAsset, "browser_download_url");
                if (IsOfficialDownloadUrl(sumsUrl))
                {
                    var sums = await _http.GetStringAsync(sumsUrl).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var line in sums.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && parts[1].TrimStart('*').Equals(WindowsAssetName, StringComparison.OrdinalIgnoreCase) && IsSha256(parts[0]))
                            return parts[0];
                    }
                }
            }

            var digest = GetString(asset, "digest");
            return digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest.Substring("sha256:".Length)
                : null;
        }

        private async Task DownloadFileAsync(Uri uri, string destination, CancellationToken cancellationToken)
        {
            using (var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await source.CopyToAsync(target, 81920, cancellationToken).ConfigureAwait(false);
            }
        }

        private string ReadActiveRuntimePath()
        {
            return ReadMarkerPath(Path.Combine(_updatesRoot, "current-runtime.json"));
        }

        private static string ReadMarkerPath(string marker)
        {
            try
            {
                if (!File.Exists(marker)) return null;
                using (var document = JsonDocument.Parse(File.ReadAllText(marker, Encoding.UTF8)))
                    return GetString(document.RootElement, "executablePath");
            }
            catch { return null; }
        }

        private IEnumerable<string> EnumerateInstalledRuntimes()
        {
            if (!Directory.Exists(_updatesRoot)) yield break;
            var candidates = new List<Tuple<string, string>>();
            foreach (var directory in Directory.GetDirectories(_updatesRoot))
            {
                var version = NormalizeVersion(Path.GetFileName(directory));
                var executable = Path.Combine(directory, "codex.exe");
                if (!string.IsNullOrWhiteSpace(version) && File.Exists(executable))
                    candidates.Add(Tuple.Create(version, executable));
            }
            foreach (var candidate in candidates.OrderByDescending(item => ParseVersion(item.Item1)))
                yield return candidate.Item2;
        }

        private void WriteActiveRuntime(string version, string executablePath, string previousFallback = null)
        {
            Directory.CreateDirectory(_updatesRoot);
            var current = Path.Combine(_updatesRoot, "current-runtime.json");
            var previous = Path.Combine(_updatesRoot, "previous-runtime.json");
            var temporary = Path.Combine(_updatesRoot, ".current-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { version = version, executablePath = Path.GetFullPath(executablePath) }), new UTF8Encoding(false));
            if (File.Exists(current))
            {
                File.Copy(current, previous, true);
                File.Replace(temporary, current, null);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(previousFallback) && File.Exists(previousFallback))
                    File.WriteAllText(previous, JsonSerializer.Serialize(new
                    {
                        version = "bundled",
                        executablePath = Path.GetFullPath(previousFallback)
                    }), new UTF8Encoding(false));
                File.Move(temporary, current);
            }
        }

        private static void ExtractZipSafely(string archive, string destination)
        {
            var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var entry in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The Codex archive contains an unsafe path.");
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, false);
                }
            }
        }

        private static string FindCodexExecutable(string directory)
        {
            return Directory.GetFiles(directory, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => Path.GetFileName(path).Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
                ?? Directory.GetFiles(directory, "codex-*-windows-msvc.exe", SearchOption.AllDirectories).FirstOrDefault();
        }

        public static string NormalizeVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var text = value.Trim();
            var marker = text.IndexOf("rust-v", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0) text = text.Substring(marker + 6);
            for (var i = 0; i < text.Length; i++)
            {
                if (!char.IsDigit(text[i])) continue;
                var end = i;
                while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
                var candidate = text.Substring(i, end - i).TrimEnd('.');
                Version parsed;
                if (Version.TryParse(candidate, out parsed) && candidate.Count(ch => ch == '.') >= 2)
                    return parsed.Major + "." + parsed.Minor + "." + parsed.Build;
            }
            return null;
        }

        public static int CompareVersions(string left, string right)
        {
            return ParseVersion(left).CompareTo(ParseVersion(right));
        }

        private static Version ParseVersion(string value)
        {
            Version parsed;
            return Version.TryParse(NormalizeVersion(value), out parsed) ? parsed : new Version(0, 0, 0);
        }

        public static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static bool TryFindAsset(JsonElement release, string name, out JsonElement found)
        {
            JsonElement assets;
            if (release.TryGetProperty("assets", out assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (string.Equals(GetString(asset, "name"), name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = asset;
                        return true;
                    }
                }
            }
            found = default(JsonElement);
            return false;
        }

        private static string GetString(JsonElement element, string property)
        {
            JsonElement value;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static bool IsOfficialDownloadUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/openai/codex/releases/download/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSha256(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);
        }

        private static string NormalizeOptionalPath(string path)
        {
            try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())); }
            catch { return path; }
        }

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NEW_CLEVER-Codex-Integration/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.Timeout = TimeSpan.FromMinutes(5);
            return client;
        }

        private static void TryKill(Process process)
        {
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        public void Dispose()
        {
            _installGate.Dispose();
            if (_ownsHttp) _http.Dispose();
        }
    }
}
