using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace NEW_CLEVER.Model.Utils
{
    internal class GitCommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public bool GitNotFound { get; set; }
        public bool TimedOut { get; set; }

        public bool Success
        {
            get { return !GitNotFound && !TimedOut && ExitCode == 0; }
        }

        public string Message
        {
            get
            {
                if (GitNotFound)
                    return "Git executable was not found.";
                if (TimedOut)
                    return "Git command timed out.";
                if (!string.IsNullOrWhiteSpace(Error))
                    return Error.Trim();
                if (!string.IsNullOrWhiteSpace(Output))
                    return Output.Trim();

                return Success ? "" : "Git command failed.";
            }
        }
    }

    internal class GitCommandRunner
    {
        private const int DefaultTimeoutMs = 30000;

        internal bool IsGitAvailable()
        {
            return Run(null, 5000, "--version").Success;
        }

        internal GitCommandResult Run(string workingDirectory, params string[] args)
        {
            return Run(workingDirectory, DefaultTimeoutMs, args);
        }

        internal GitCommandResult Run(string workingDirectory, int timeoutMs, params string[] args)
        {
            return Run(workingDirectory, timeoutMs, null, null, args);
        }

        internal GitCommandResult Run(string workingDirectory, IDictionary<string, string> environmentVariables, params string[] args)
        {
            return Run(workingDirectory, DefaultTimeoutMs, null, environmentVariables, args);
        }

        internal GitCommandResult RunWithInput(string workingDirectory, string standardInput, IDictionary<string, string> environmentVariables, params string[] args)
        {
            return Run(workingDirectory, DefaultTimeoutMs, standardInput, environmentVariables, args);
        }

        internal GitCommandResult Run(string workingDirectory, int timeoutMs, string standardInput, IDictionary<string, string> environmentVariables, params string[] args)
        {
            return Run(workingDirectory, timeoutMs, standardInput, environmentVariables, CancellationToken.None, args);
        }

        internal GitCommandResult Run(string workingDirectory, CancellationToken cancellationToken, params string[] args)
        {
            return Run(workingDirectory, DefaultTimeoutMs, null, null, cancellationToken, args);
        }

        private GitCommandResult Run(string workingDirectory, int timeoutMs, string standardInput, IDictionary<string, string> environmentVariables, CancellationToken cancellationToken, params string[] args)
        {
            var result = new GitCommandResult
            {
                ExitCode = -1,
                Output = "",
                Error = ""
            };

            try
            {
                var output = new StringBuilder();
                var error = new StringBuilder();
                using (var process = new Process())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = BuildArguments(args),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = standardInput != null,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                        process.StartInfo.WorkingDirectory = workingDirectory;

                    if (environmentVariables != null)
                    {
                        foreach (var item in environmentVariables)
                        {
                            if (!string.IsNullOrEmpty(item.Key))
                                process.StartInfo.EnvironmentVariables[item.Key] = item.Value ?? "";
                        }
                    }

                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                            output.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                            error.AppendLine(e.Data);
                    };

                    process.Start();
                    if (standardInput != null)
                    {
                        process.StandardInput.Write(standardInput);
                        process.StandardInput.Close();
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var stopwatch = Stopwatch.StartNew();
                    while (!process.WaitForExit(Math.Min(100, Math.Max(1, timeoutMs - (int)stopwatch.ElapsedMilliseconds))))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try
                            {
                                process.Kill();
                            }
                            catch
                            {
                            }
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        if (stopwatch.ElapsedMilliseconds < timeoutMs)
                            continue;

                        result.TimedOut = true;
                        break;
                    }

                    if (result.TimedOut)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                    }
                    else
                    {
                        process.WaitForExit();
                        result.ExitCode = process.ExitCode;
                    }

                    result.Output = output.ToString();
                    result.Error = error.ToString();
                }
            }
            catch (Win32Exception)
            {
                result.GitNotFound = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        private static string BuildArguments(string[] args)
        {
            if (args == null || args.Length == 0)
                return "";

            var builder = new StringBuilder();
            foreach (var arg in args)
            {
                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(QuoteArgument(arg ?? ""));
            }

            return builder.ToString();
        }

        private static string QuoteArgument(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '"' }) < 0)
                return argument;

            var builder = new StringBuilder();
            builder.Append('"');
            var backslashes = 0;

            foreach (var c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }

                builder.Append(c);
            }

            if (backslashes > 0)
                builder.Append('\\', backslashes * 2);

            builder.Append('"');
            return builder.ToString();
        }
    }
}
