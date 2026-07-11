using Clev3r.AgentCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Clever.Model.Agent
{
    internal static class Clev3rToolRegistryFactory
    {
        private const string NamespaceName = "clev3r";
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        internal static AgentToolRegistry Create(
            IWorkspaceContextService workspace,
            IBpBuildService build,
            IEv3SessionService ev3,
            IDocumentationSearchService documentation,
            IAgentApprovalService approvals)
        {
            var registry = new AgentToolRegistry();
            Register(registry, "workspace_context", "Read the active project tree, open documents, diagnostics, robot profile and EV3 status.", ObjectSchema(""), AgentToolRisk.ReadOnly,
                async (args, ct) => ToToolResult(await workspace.CaptureAsync(ct).ConfigureAwait(false)));

            Register(registry, "diagnostics_list", "List current structured CLEV3R editor and compiler diagnostics.", ObjectSchema(""), AgentToolRisk.ReadOnly,
                async (args, ct) =>
                {
                    var result = await workspace.CaptureAsync(ct).ConfigureAwait(false);
                    if (!result.Success)
                        return AgentToolResult.Fail(result.Code, result.Message);
                    return AgentToolResult.Ok(Serialize(result.Data.Diagnostics));
                });

            Register(registry, "compile_program", "Save project editors and compile a .bp entry file without deploying or running it.",
                ObjectSchema("\"entryPath\":{\"type\":\"string\",\"description\":\"Absolute or project-relative .bp path; defaults to the active document.\"}"),
                AgentToolRisk.ProjectWrite,
                async (args, ct) =>
                {
                    var context = await workspace.SaveAllAndCaptureAsync(ct).ConfigureAwait(false);
                    if (!context.Success)
                        return AgentToolResult.Fail(context.Code, context.Message);
                    var requested = GetString(args, "entryPath");
                    var entry = ResolveProjectPath(context.Data.ProjectRoot, string.IsNullOrWhiteSpace(requested) ? context.Data.ActiveDocument : requested);
                    var result = await build.CompileAsync(entry, ct).ConfigureAwait(false);
                    if (!result.Success)
                        return AgentToolResult.Fail(result.Code, result.Message);
                    var toolResult = AgentToolResult.Ok(Serialize(result.Data), result.Message);
                    toolResult.Success = result.Data != null && result.Data.Success;
                    toolResult.Code = toolResult.Success ? "ok" : "compile_failed";
                    toolResult.Diagnostics = result.Data == null ? new List<AgentDiagnostic>() : result.Data.Diagnostics;
                    return toolResult;
                });

            Register(registry, "robot_context", "Read the current typed robot profile and live EV3 status.", ObjectSchema(""), AgentToolRisk.ReadOnly,
                async (args, ct) =>
                {
                    var context = await workspace.CaptureAsync(ct).ConfigureAwait(false);
                    if (!context.Success)
                        return AgentToolResult.Fail(context.Code, context.Message);
                    return AgentToolResult.Ok(Serialize(new { robot = context.Data.Robot, ev3 = context.Data.Ev3 }));
                });

            Register(registry, "docs_search", "Search authoritative CLEV3R Basic Plus documentation and verified examples.",
                ObjectSchema("\"query\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20}", "query"),
                AgentToolRisk.ReadOnly,
                async (args, ct) => ToToolResult(await documentation.SearchAsync(GetString(args, "query"), Math.Max(1, Math.Min(20, GetInt(args, "limit", 8))), ct).ConfigureAwait(false)));

            Register(registry, "docs_read", "Read one documentation topic or verified example returned by docs_search.",
                ObjectSchema("\"id\":{\"type\":\"string\"}", "id"), AgentToolRisk.ReadOnly,
                async (args, ct) => ToToolResult(await documentation.ReadAsync(GetString(args, "id"), ct).ConfigureAwait(false)));

            Register(registry, "ev3_connect", "Connect to an EV3 using the IDE connection chooser when required.", ObjectSchema(""), AgentToolRisk.ReadOnly,
                async (args, ct) => ToToolResult(await ev3.ConnectAsync(ct).ConfigureAwait(false)));
            Register(registry, "ev3_disconnect", "Disconnect the current EV3.", ObjectSchema(""), AgentToolRisk.ReadOnly,
                async (args, ct) => ToToolResult(await ev3.DisconnectAsync(ct).ConfigureAwait(false)));
            Register(registry, "ev3_status", "Refresh EV3 connection, device and running-program status.", ObjectSchema(""), AgentToolRisk.ReadOnly,
                async (args, ct) => ToToolResult(await ev3.RefreshStatusAsync(ct).ConfigureAwait(false)));
            Register(registry, "ev3_list_files", "List files and directories on EV3.",
                ObjectSchema("\"path\":{\"type\":\"string\",\"default\":\"/home/root/lms2012/prjs/\"}"), AgentToolRisk.ReadOnly,
                async (args, ct) => ToToolResult(await ev3.ListAsync(GetString(args, "path"), ct).ConfigureAwait(false)));
            Register(registry, "ev3_read_file", "Read a text or binary file from EV3 with a bounded result.",
                ObjectSchema("\"path\":{\"type\":\"string\"},\"maxBytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1048576}", "path"), AgentToolRisk.ReadOnly,
                async (args, ct) => ToToolResult(await ev3.ReadAsync(GetString(args, "path"), GetInt(args, "maxBytes", 262144), ct).ConfigureAwait(false)));

            RegisterApproved(registry, approvals, "ev3_upload_file", "Upload a file from the active project to EV3.",
                ObjectSchema("\"localPath\":{\"type\":\"string\"},\"destinationPath\":{\"type\":\"string\"}", "localPath", "destinationPath"), AgentToolRisk.Ev3Write,
                args => "Upload '" + GetString(args, "localPath") + "' to '" + GetString(args, "destinationPath") + "'",
                async (args, ct) => ToToolResult(await ev3.UploadAsync(GetString(args, "localPath"), GetString(args, "destinationPath"), ct).ConfigureAwait(false)));
            RegisterApproved(registry, approvals, "ev3_create_directory", "Create a directory on EV3.",
                ObjectSchema("\"path\":{\"type\":\"string\"}", "path"), AgentToolRisk.Ev3Write,
                args => "Create EV3 directory '" + GetString(args, "path") + "'",
                async (args, ct) => ToToolResult(await ev3.CreateDirectoryAsync(GetString(args, "path"), ct).ConfigureAwait(false)));
            RegisterApproved(registry, approvals, "ev3_delete_path", "Delete a file or empty directory on EV3.",
                ObjectSchema("\"path\":{\"type\":\"string\"}", "path"), AgentToolRisk.Ev3Destructive,
                args => "Delete EV3 path '" + GetString(args, "path") + "'",
                async (args, ct) => ToToolResult(await ev3.DeleteAsync(GetString(args, "path"), ct).ConfigureAwait(false)));
            RegisterApproved(registry, approvals, "ev3_set_device_name", "Change the connected EV3 device name.",
                ObjectSchema("\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":32}", "name"), AgentToolRisk.Ev3Write,
                args => "Rename EV3 to '" + GetString(args, "name") + "'",
                async (args, ct) => ToToolResult(await ev3.SetDeviceNameAsync(GetString(args, "name"), ct).ConfigureAwait(false)));
            RegisterApproved(registry, approvals, "ev3_deploy_build", "Deploy a successful compile_program build to EV3 without running it.",
                ObjectSchema("\"buildId\":{\"type\":\"string\"},\"destinationDirectory\":{\"type\":\"string\"}", "buildId"), AgentToolRisk.Ev3Write,
                args => "Deploy build '" + GetString(args, "buildId") + "' to EV3",
                async (args, ct) => ToToolResult(await ev3.DeployAsync(new Ev3DeploymentRequest
                {
                    BuildId = GetString(args, "buildId"),
                    DestinationDirectory = GetString(args, "destinationDirectory")
                }, ct).ConfigureAwait(false)));
            RegisterApproved(registry, approvals, "ev3_run_file", "Run an existing .rbf program on EV3.",
                ObjectSchema("\"path\":{\"type\":\"string\"}", "path"), AgentToolRisk.Ev3Run,
                args => "Run EV3 program '" + GetString(args, "path") + "'",
                async (args, ct) => ToToolResult(await ev3.RunAsync(GetString(args, "path"), ct).ConfigureAwait(false)));
            Register(registry, "ev3_stop_program", "Stop the running EV3 user program immediately.", ObjectSchema(""), AgentToolRisk.ReadOnly,
                async (args, ct) => ToToolResult(await ev3.StopAsync(ct).ConfigureAwait(false)));

            return registry;
        }

        private static void Register(AgentToolRegistry registry, string name, string description, string schema, AgentToolRisk risk, Func<string, CancellationToken, Task<AgentToolResult>> execute)
        {
            registry.Register(new DelegateToolHandler(new AgentToolDefinition
            {
                Namespace = NamespaceName,
                Name = name,
                Description = description,
                InputSchemaJson = schema,
                Risk = risk
            }, execute));
        }

        private static void RegisterApproved(
            AgentToolRegistry registry,
            IAgentApprovalService approvals,
            string name,
            string description,
            string schema,
            AgentToolRisk risk,
            Func<string, string> summarize,
            Func<string, CancellationToken, Task<AgentToolResult>> execute)
        {
            var definition = new AgentToolDefinition
            {
                Namespace = NamespaceName,
                Name = name,
                Description = description,
                InputSchemaJson = schema,
                Risk = risk
            };
            registry.Register(new DelegateToolHandler(definition, async (args, ct) =>
            {
                var approved = await approvals.RequestApprovalAsync(definition, summarize(args), ct).ConfigureAwait(false);
                return approved ? await execute(args, ct).ConfigureAwait(false) : AgentToolResult.Fail("approval_declined", "The EV3 action was declined by the user.");
            }));
        }

        private static AgentToolResult ToToolResult<T>(OperationResult<T> result)
        {
            if (result == null)
                return AgentToolResult.Fail("operation_missing", "The CLEV3R service returned no result.");
            return result.Success
                ? AgentToolResult.Ok(Serialize(result.Data), result.Message)
                : AgentToolResult.Fail(result.Code, result.Message);
        }

        private static string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }

        private static string ObjectSchema(string properties, params string[] required)
        {
            return "{\"type\":\"object\",\"properties\":{" + properties + "},\"required\":[" +
                string.Join(",", (required ?? new string[0]).Select(item => "\"" + item + "\"")) + "],\"additionalProperties\":false}";
        }

        private static string GetString(string json, string property)
        {
            using (var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json))
            {
                JsonElement value;
                return document.RootElement.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : "";
            }
        }

        private static int GetInt(string json, string property, int fallback)
        {
            using (var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json))
            {
                JsonElement value;
                int parsed;
                return document.RootElement.TryGetProperty(property, out value) && value.TryGetInt32(out parsed) ? parsed : fallback;
            }
        }

        private static string ResolveProjectPath(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";
            return System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(root ?? "", path);
        }

        private sealed class DelegateToolHandler : IAgentToolHandler
        {
            private readonly Func<string, CancellationToken, Task<AgentToolResult>> _execute;

            internal DelegateToolHandler(AgentToolDefinition definition, Func<string, CancellationToken, Task<AgentToolResult>> execute)
            {
                Definition = definition;
                _execute = execute;
            }

            public AgentToolDefinition Definition { get; private set; }

            public Task<AgentToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken)
            {
                return _execute(argumentsJson, cancellationToken);
            }
        }
    }
}
