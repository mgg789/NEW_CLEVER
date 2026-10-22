using NEW_CLEVER.AgentCore;
using NEW_CLEVER.Brick.Utils;
using NEW_CLEVER.CommonData;
using NEW_CLEVER.Model.Diagnostics;
using NEW_CLEVER.Model.Utils;
using NEW_CLEVER.ViewModel;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NEW_CLEVER.Model.Agent
{
    internal sealed class BpBuildService : IBpBuildService
    {
        private static readonly Regex DiagnosticPattern = new Regex(
            @"file:\s*(?<file>.*?)\s+line:\s*(?<line>\d+)\s*\|\s*code:\s*(?<code>\d+)\s*===>\s*(?<message>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly SemaphoreSlim _buildGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, BpBuildResult> _builds =
            new ConcurrentDictionary<string, BpBuildResult>(StringComparer.OrdinalIgnoreCase);

        public async Task<OperationResult<BpBuildResult>> CompileAsync(string entryPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(entryPath))
                return OperationResult<BpBuildResult>.Fail("build_target_missing", "A Basic Plus entry file is required.");

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(entryPath);
            }
            catch (Exception ex)
            {
                return OperationResult<BpBuildResult>.Fail("build_target_invalid", ex.Message);
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".bp", StringComparison.OrdinalIgnoreCase))
                return OperationResult<BpBuildResult>.Fail("build_target_invalid", "Only .bp entry files can be compiled.");
            if (!File.Exists(fullPath))
                return OperationResult<BpBuildResult>.Fail("build_target_not_found", "The Basic Plus entry file does not exist: " + fullPath);
            if (!MainWindowVM.IsPathInsideCurrentProject(fullPath))
                return OperationResult<BpBuildResult>.Fail("build_target_outside_project", "The build target must be inside the active project.");

            await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => CompileCore(fullPath, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _buildGate.Release();
            }
        }

        public OperationResult<BpBuildResult> GetBuild(string buildId)
        {
            BpBuildResult result;
            if (string.IsNullOrWhiteSpace(buildId) || !_builds.TryGetValue(buildId, out result))
                return OperationResult<BpBuildResult>.Fail("build_not_found", "The requested build is not available in this IDE session.");
            if (string.IsNullOrWhiteSpace(result.ArtifactPath) || !File.Exists(result.ArtifactPath))
                return OperationResult<BpBuildResult>.Fail("build_artifact_missing", "The build artifact is no longer available.");
            return OperationResult<BpBuildResult>.Ok(result);
        }

        private OperationResult<BpBuildResult> CompileCore(string entryPath, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new BpBuildResult
            {
                BuildId = Guid.NewGuid().ToString("N"),
                EntryPath = entryPath,
                TargetFileName = Path.GetFileNameWithoutExtension(entryPath) + ".rbf"
            };

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = File.ReadAllLines(entryPath);
                if (source.Length == 0)
                    return OperationResult<BpBuildResult>.Fail("build_source_empty", "The Basic Plus entry file is empty.");

                var language = Configurations.Get.Language;
                var builder = new Interpreter.Builder(language);
                var runtimePath = GetRuntimePath();
                var entryDirectory = Path.GetDirectoryName(entryPath) ?? "";
                var entryName = Path.GetFileName(entryPath);
                var errors = new List<string>();

                using (var interpreted = new MemoryStream())
                {
                    builder.StartInterpreter(source, EnsureTrailingSeparator(entryDirectory), entryName, runtimePath, ".bp", ".bpi", ".bpm", "~", language, errors, interpreted);
                    if (errors.Count > 0)
                    {
                        CompilerProblemReporter.Replace(IdeProblemSource.Preprocessor, entryPath, errors);
                        result.Diagnostics = ToDiagnostics(errors, "preprocessor", entryPath);
                        result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                        return OperationResult<BpBuildResult>.Ok(result, "Basic Plus preprocessing failed.");
                    }
                    CompilerProblemReporter.Replace(IdeProblemSource.Preprocessor, entryPath, new string[0]);

                    CaptureManifest(builder, result);
                    cancellationToken.ThrowIfCancellationRequested();

                    using (var compiled = new MemoryStream())
                    {
                        errors.Clear();
                        builder.StartCompiler(interpreted, compiled, errors);
                        if (errors.Count > 0)
                        {
                            CompilerProblemReporter.Replace(IdeProblemSource.Compiler, entryPath, errors);
                            result.Diagnostics = ToDiagnostics(errors, "compiler", entryPath);
                            result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                            return OperationResult<BpBuildResult>.Ok(result, "Basic Plus compilation failed.");
                        }
                        CompilerProblemReporter.Replace(IdeProblemSource.Compiler, entryPath, new string[0]);
                        cancellationToken.ThrowIfCancellationRequested();

                        using (var assembled = new MemoryStream())
                        {
                            errors.Clear();
                            builder.StartAssembler(compiled, assembled, errors);
                            if (errors.Count > 0)
                            {
                                CompilerProblemReporter.Replace(IdeProblemSource.Assembler, entryPath, errors);
                                result.Diagnostics = ToDiagnostics(errors, "assembler", entryPath);
                                result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                                return OperationResult<BpBuildResult>.Ok(result, "Basic Plus assembly failed.");
                            }

                            var buildDirectory = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "CLEV3R",
                                "agent-builds",
                                result.BuildId);
                            Directory.CreateDirectory(buildDirectory);
                            result.ArtifactPath = Path.Combine(buildDirectory, result.TargetFileName);
                            File.WriteAllBytes(result.ArtifactPath, assembled.ToArray());
                        }
                    }
                }

                CompilerProblemReporter.Clear(entryPath);
                IdeProblemStore.ClearIdeError("compile");
                NEW_CLEVERGitIgnoreService.EnsureForCompiledFile(entryPath);
                result.Success = true;
                result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                _builds[result.BuildId] = result;
                return OperationResult<BpBuildResult>.Ok(result, "Basic Plus build completed.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                IdeProblemStore.ReportIdeError("compile", ex.Message, entryPath);
                result.Diagnostics.Add(new AgentDiagnostic
                {
                    Id = "build:exception",
                    Severity = "error",
                    FilePath = entryPath,
                    Message = ex.Message
                });
                result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                return OperationResult<BpBuildResult>.Fail("build_exception", ex.Message);
            }
        }

        private static void CaptureManifest(Interpreter.Builder builder, BpBuildResult result)
        {
            result.DestinationKind = builder.GetIsFolder() ? (builder.GetFolderName() ?? "").Trim().Replace("\"", "").ToUpperInvariant() : "";
            result.DestinationName = builder.GetIsFolder() ? (builder.GetProjectName() ?? "").Trim().Replace("\"", "") : "";
            result.SourceFiles = (builder.GetPrograms() ?? new List<string>()).ToList();
            result.MediaFiles = (builder.GetImageFullName() ?? new List<string>())
                .Concat(builder.GetSoundFullName() ?? new List<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.FileAssets = (builder.GetFileFullName() ?? new List<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IList<AgentDiagnostic> ToDiagnostics(IEnumerable<string> errors, string stage, string entryPath)
        {
            var result = new List<AgentDiagnostic>();
            var index = 0;
            foreach (var error in errors ?? Enumerable.Empty<string>())
            {
                var match = DiagnosticPattern.Match(error ?? "");
                int line;
                result.Add(new AgentDiagnostic
                {
                    Id = "build:" + stage + ":" + index++,
                    Severity = "error",
                    FilePath = match.Success ? match.Groups["file"].Value.Trim() : entryPath,
                    Line = match.Success && int.TryParse(match.Groups["line"].Value, out line) ? (int?)line : null,
                    Code = match.Success ? match.Groups["code"].Value : stage,
                    Message = match.Success ? match.Groups["message"].Value.Trim() : (error ?? "")
                });
            }
            return result;
        }

        private static string GetRuntimePath()
        {
            var assembly = Application.ResourceAssembly ?? Assembly.GetExecutingAssembly();
            return EnsureTrailingSeparator(Path.GetDirectoryName(assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
                return Path.DirectorySeparatorChar.ToString();
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
    }
}
