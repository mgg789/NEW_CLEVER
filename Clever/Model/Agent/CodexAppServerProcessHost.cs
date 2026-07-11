using Clev3r.AgentCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Clever.Model.Agent
{
    /// <summary>Owns exactly one codex app-server process and its stdio transport.</summary>
    internal sealed class CodexAppServerProcessHost : IDisposable
    {
        private const int StderrLineLimit = 200;
        private const int StderrCharacterLimit = 32000;
        private readonly object _stderrSync = new object();
        private readonly Queue<string> _stderrLines = new Queue<string>();
        private Process _process;
        private CodexJsonRpcTransport _transport;
        private int _disposed;
        private int _expectedExit;

        public CodexJsonRpcTransport Transport { get { return _transport; } }

        public string StderrTail
        {
            get
            {
                lock (_stderrSync)
                    return string.Join(Environment.NewLine, _stderrLines.ToArray());
            }
        }

        public event Action<int> UnexpectedExit;

        public Task StartAsync(CodexRuntimeInfo runtime, CancellationToken cancellationToken)
        {
            if (runtime == null || string.IsNullOrWhiteSpace(runtime.ExecutablePath))
                throw new ArgumentException("A resolved Codex runtime is required.", "runtime");
            if (!File.Exists(runtime.ExecutablePath))
                throw new FileNotFoundException("The Codex runtime executable was not found.", runtime.ExecutablePath);
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException("CodexAppServerProcessHost");
            cancellationToken.ThrowIfCancellationRequested();

            var assemblyDirectory = Path.GetDirectoryName(typeof(CodexAppServerProcessHost).Assembly.Location);
            var launcher = Path.Combine(assemblyDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "Clev3r.AgentMcpBridge.exe");
            if (!File.Exists(launcher))
                throw new FileNotFoundException("The CLEV3R Codex stdio launcher was not found.", launcher);
            var startInfo = new ProcessStartInfo
            {
                FileName = launcher,
                Arguments = "--codex-stdio " + QuoteArgument(runtime.ExecutablePath),
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.EnvironmentVariables["RUST_LOG"] = "warn";
            startInfo.EnvironmentVariables["LOG_FORMAT"] = "json";
            RemoveSensitiveEnvironmentVariables(startInfo);
            // CODEX_HOME is intentionally inherited. CLEV3R shares the user's normal Codex
            // authentication, config and thread store with CLI and Desktop, but never passes
            // API keys from the parent process to the Codex child process.

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            process.ErrorDataReceived += OnErrorDataReceived;
            process.Exited += OnExited;

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Codex app-server did not start.");
                AppendDiagnostics("launcher started: " + runtime.ExecutablePath);
                _process = process;
                _transport = new CodexJsonRpcTransport(
                    process.StandardOutput.BaseStream,
                    process.StandardInput.BaseStream);
                process.BeginErrorReadLine();
                _transport.Start();
                return Task.CompletedTask;
            }
            catch
            {
                process.ErrorDataReceived -= OnErrorDataReceived;
                process.Exited -= OnExited;
                process.Dispose();
                throw;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs eventArgs)
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
                return;
            var line = RedactSecrets(eventArgs.Data);
            AppendDiagnostics("stderr: " + line);
            lock (_stderrSync)
            {
                _stderrLines.Enqueue(line);
                while (_stderrLines.Count > StderrLineLimit ||
                       _stderrLines.Sum(item => item.Length) > StderrCharacterLimit)
                {
                    if (_stderrLines.Count == 0)
                        break;
                    _stderrLines.Dequeue();
                }
            }
        }

        private void OnExited(object sender, EventArgs eventArgs)
        {
            if (Volatile.Read(ref _expectedExit) != 0 || Volatile.Read(ref _disposed) != 0)
                return;
            var process = sender as Process;
            var exitCode = -1;
            try { if (process != null) exitCode = process.ExitCode; } catch { }
            AppendDiagnostics("launcher exited: " + exitCode);
            var handler = UnexpectedExit;
            if (handler != null)
            {
                try { handler(exitCode); } catch { }
            }
        }

        private static string RedactSecrets(string line)
        {
            if (string.IsNullOrEmpty(line))
                return "";
            // stderr is diagnostic-only. Avoid retaining values from the common secret fields.
            var secretKeys = new[] { "apiKey", "access_token", "refresh_token", "authorization" };
            foreach (var key in secretKeys)
            {
                var index = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                    return line.Substring(0, Math.Min(line.Length, index + key.Length)) + ": [REDACTED]";
            }
            return line;
        }

        internal static void RemoveSensitiveEnvironmentVariables(ProcessStartInfo startInfo)
        {
            // CLEV3R uses the user's ChatGPT OAuth session through CODEX_HOME. API-key
            // environment variables are neither required nor safe to inherit into a process
            // which can receive model-controlled tool requests.
            foreach (var name in new[]
            {
                "OPENAI_API_KEY", "OPENAI_ORG_ID", "OPENAI_PROJECT_ID", "AZURE_OPENAI_API_KEY",
                "CLEV3R_CODEX_DIAGNOSTICS"
            })
                startInfo.EnvironmentVariables.Remove(name);
        }

        private static void AppendDiagnostics(string message)
        {
            var path = Environment.GetEnvironmentVariable("CLEV3R_CODEX_DIAGNOSTICS");
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.Exchange(ref _expectedExit, 1);

            var transport = _transport;
            _transport = null;
            if (transport != null)
                transport.Dispose();

            var process = _process;
            _process = null;
            if (process == null)
                return;

            process.ErrorDataReceived -= OnErrorDataReceived;
            process.Exited -= OnExited;
            try
            {
                if (!process.HasExited)
                {
                    try { process.StandardInput.Close(); } catch { }
                    if (!process.WaitForExit(1200))
                        TerminateProcessTree(process.Id);
                }
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }

        private static void TerminateProcessTree(int processId)
        {
            try
            {
                using (var taskKill = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/PID " + processId + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    if (taskKill != null)
                        taskKill.WaitForExit(3000);
                }
            }
            catch
            {
                try
                {
                    using (var process = Process.GetProcessById(processId))
                        process.Kill();
                }
                catch { }
            }
        }
    }
}
