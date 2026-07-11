using Clever.Model.Agent;
using System;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Clev3r.AgentCore.Tests
{
    public sealed class CodexRuntimeManagerTests
    {
        [Theory]
        [InlineData("codex-cli 0.144.1", "0.144.1")]
        [InlineData("rust-v0.145.0", "0.145.0")]
        [InlineData("0.144.0-alpha.4", "0.144.0")]
        [InlineData("unknown", null)]
        public void NormalizesSupportedVersionShapes(string value, string expected)
        {
            Assert.Equal(expected, CodexRuntimeManager.NormalizeVersion(value));
        }

        [Fact]
        public void ComparesSemanticVersions()
        {
            Assert.True(CodexRuntimeManager.CompareVersions("0.144.1", "0.115.0") > 0);
            Assert.Equal(0, CodexRuntimeManager.CompareVersions("rust-v0.144.1", "0.144.1"));
        }

        [Fact]
        public void CodexChildProcessDoesNotInheritOpenAiSecrets()
        {
            var startInfo = new ProcessStartInfo();
            startInfo.EnvironmentVariables["OPENAI_API_KEY"] = "sk-test-secret";
            startInfo.EnvironmentVariables["AZURE_OPENAI_API_KEY"] = "azure-secret";
            startInfo.EnvironmentVariables["OPENAI_ORG_ID"] = "org-private";
            startInfo.EnvironmentVariables["CODEX_HOME"] = "C:\\CodexHome";

            CodexAppServerProcessHost.RemoveSensitiveEnvironmentVariables(startInfo);

            Assert.False(startInfo.EnvironmentVariables.ContainsKey("OPENAI_API_KEY"));
            Assert.False(startInfo.EnvironmentVariables.ContainsKey("AZURE_OPENAI_API_KEY"));
            Assert.False(startInfo.EnvironmentVariables.ContainsKey("OPENAI_ORG_ID"));
            Assert.Equal("C:\\CodexHome", startInfo.EnvironmentVariables["CODEX_HOME"]);
        }

        [Fact]
        public async Task ReadsOfficialReleaseDigestWithoutDownloadingRuntime()
        {
            const string digest = "976844ab2d3d77187b70a10c93f0952d6a58b2f67219734fc37785e4334b4c4a";
            var json = "{\"tag_name\":\"rust-v0.144.1\",\"body\":\"notes\",\"assets\":[{" +
                       "\"name\":\"codex-x86_64-pc-windows-msvc.exe.zip\"," +
                       "\"browser_download_url\":\"https://github.com/openai/codex/releases/download/rust-v0.144.1/codex-x86_64-pc-windows-msvc.exe.zip\"," +
                       "\"digest\":\"sha256:" + digest + "\"}]}";
            var root = Path.Combine(Path.GetTempPath(), "clev3r-runtime-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using (var http = new HttpClient(new StaticHandler(json)))
                using (var manager = new CodexRuntimeManager(http, null, Path.Combine(root, "updates"), Path.Combine(root, "bundle"), new Uri("https://api.github.com/repos/openai/codex/releases/latest")))
                {
                    var result = await manager.CheckForUpdateAsync(CancellationToken.None);
                    Assert.True(result.Success, result.Message);
                    Assert.True(result.Data.UpdateAvailable);
                    Assert.Equal("0.144.1", result.Data.LatestVersion);
                    Assert.Equal(digest, result.Data.Sha256);
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void FirstActivationRecordsBundledRuntimeForRollback()
        {
            var root = Path.Combine(Path.GetTempPath(), "clev3r-runtime-rollback-" + Guid.NewGuid().ToString("N"));
            var updates = Path.Combine(root, "updates");
            var bundle = Path.Combine(root, "bundle");
            Directory.CreateDirectory(bundle);
            var bundledExecutable = Path.Combine(bundle, "codex.exe");
            var updatedExecutable = Path.Combine(root, "updated", "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(updatedExecutable));
            File.WriteAllBytes(bundledExecutable, new byte[] { 1 });
            File.WriteAllBytes(updatedExecutable, new byte[] { 2 });
            try
            {
                using (var http = new HttpClient(new StaticHandler("{}")))
                using (var manager = new CodexRuntimeManager(http, null, updates, bundle, new Uri("https://api.github.com/repos/openai/codex/releases/latest")))
                {
                    var write = typeof(CodexRuntimeManager).GetMethod("WriteActiveRuntime", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(write);
                    write.Invoke(manager, new object[] { "0.145.0", updatedExecutable, bundledExecutable });
                    using (var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(updates, "previous-runtime.json"))))
                        Assert.Equal(Path.GetFullPath(bundledExecutable), marker.RootElement.GetProperty("executablePath").GetString());
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private sealed class StaticHandler : HttpMessageHandler
        {
            private readonly string _json;
            public StaticHandler(string json) { _json = json; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_json, Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
