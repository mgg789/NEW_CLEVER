using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Clev3r.AgentCore
{
    public sealed class OperationResult<T>
    {
        public bool Success { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public T Data { get; set; }

        public static OperationResult<T> Ok(T data, string message = null)
        {
            return new OperationResult<T> { Success = true, Code = "ok", Message = message ?? "", Data = data };
        }

        public static OperationResult<T> Fail(string code, string message)
        {
            return new OperationResult<T> { Success = false, Code = code ?? "operation_error", Message = message ?? "" };
        }
    }

    public enum AgentToolRisk
    {
        ReadOnly,
        ProjectWrite,
        Ev3Write,
        Ev3Run,
        Ev3Destructive
    }

    public sealed class AgentDiagnostic
    {
        public string Id { get; set; }
        public string Severity { get; set; }
        public string FilePath { get; set; }
        public int? Line { get; set; }
        public int? Column { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
    }

    public sealed class AgentToolDefinition
    {
        public string Namespace { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string InputSchemaJson { get; set; }
        public AgentToolRisk Risk { get; set; }
        public bool DeferLoading { get; set; }

        public string QualifiedName
        {
            get { return string.IsNullOrWhiteSpace(Namespace) ? Name : Namespace + "." + Name; }
        }
    }

    public sealed class AgentToolResult
    {
        public AgentToolResult()
        {
            Diagnostics = new List<AgentDiagnostic>();
        }

        public bool Success { get; set; }
        public bool ApprovalRequired { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string DataJson { get; set; }
        public IList<AgentDiagnostic> Diagnostics { get; set; }

        public static AgentToolResult Ok(string dataJson = null, string message = null)
        {
            return new AgentToolResult { Success = true, Code = "ok", Message = message ?? "", DataJson = dataJson ?? "{}" };
        }

        public static AgentToolResult Fail(string code, string message)
        {
            return new AgentToolResult { Success = false, Code = code ?? "tool_error", Message = message ?? "" };
        }

        public static AgentToolResult NeedsApproval(string message)
        {
            return new AgentToolResult { Success = false, ApprovalRequired = true, Code = "approval_required", Message = message ?? "" };
        }
    }

    public interface IAgentToolHandler
    {
        AgentToolDefinition Definition { get; }
        Task<AgentToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken);
    }

    public interface IAgentToolRegistry
    {
        IList<AgentToolDefinition> Definitions { get; }
        Task<AgentToolResult> ExecuteAsync(string qualifiedName, string argumentsJson, CancellationToken cancellationToken);
    }

    public sealed class AgentToolRegistry : IAgentToolRegistry
    {
        private readonly Dictionary<string, IAgentToolHandler> _handlers =
            new Dictionary<string, IAgentToolHandler>(StringComparer.OrdinalIgnoreCase);

        public IList<AgentToolDefinition> Definitions
        {
            get { return _handlers.Values.Select(item => item.Definition).OrderBy(item => item.QualifiedName).ToList(); }
        }

        public void Register(IAgentToolHandler handler)
        {
            if (handler == null || handler.Definition == null || string.IsNullOrWhiteSpace(handler.Definition.Name))
                throw new ArgumentException("A tool handler and definition name are required.", "handler");

            var name = handler.Definition.QualifiedName;
            if (_handlers.ContainsKey(name))
                throw new InvalidOperationException("A tool named '" + name + "' is already registered.");

            _handlers.Add(name, handler);
        }

        public async Task<AgentToolResult> ExecuteAsync(string qualifiedName, string argumentsJson, CancellationToken cancellationToken)
        {
            IAgentToolHandler handler;
            if (string.IsNullOrWhiteSpace(qualifiedName) || !_handlers.TryGetValue(qualifiedName, out handler))
                return AgentToolResult.Fail("tool_not_found", "Unknown CLEV3R tool: " + (qualifiedName ?? ""));

            try
            {
                return await handler.ExecuteAsync(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return AgentToolResult.Fail("cancelled", "The tool operation was cancelled.");
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail("tool_exception", ex.Message);
            }
        }
    }

    public sealed class RobotProfile
    {
        public decimal? WheelDiameterMm { get; set; }
        public decimal? WheelBaseMm { get; set; }
        public string Description { get; set; }
    }

    public static class RobotProfileValidator
    {
        public static bool TryCreate(string wheelDiameter, string wheelBase, string description, out RobotProfile profile, out string error)
        {
            profile = null;
            error = "";
            decimal diameter;
            decimal width;
            if (!TryPositiveDecimal(wheelDiameter, out diameter))
            {
                error = "Wheel diameter must be a positive number in millimetres.";
                return false;
            }
            if (!TryPositiveDecimal(wheelBase, out width))
            {
                error = "Wheelbase must be a positive number in millimetres.";
                return false;
            }
            if ((description ?? "").Length > 800)
            {
                error = "Robot description must not exceed 800 characters.";
                return false;
            }

            profile = new RobotProfile
            {
                WheelDiameterMm = diameter,
                WheelBaseMm = width,
                Description = description ?? ""
            };
            return true;
        }

        private static bool TryPositiveDecimal(string value, out decimal result)
        {
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out result) && result > 0)
                return true;
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result) && result > 0)
                return true;
            result = 0;
            return false;
        }
    }

    public sealed class Ev3StatusData
    {
        public bool Connected { get; set; }
        public bool Busy { get; set; }
        public bool ProgramRunning { get; set; }
        public string DeviceName { get; set; }
        public string ConnectionType { get; set; }
        public string CurrentPath { get; set; }
        public string LastError { get; set; }
    }

    public sealed class Ev3FileEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsDirectory { get; set; }
        public bool IsRunnable { get; set; }
        public long Size { get; set; }
    }

    public sealed class Ev3FileContent
    {
        public string Path { get; set; }
        public string Encoding { get; set; }
        public string Content { get; set; }
        public long Size { get; set; }
        public bool Truncated { get; set; }
    }

    public sealed class Ev3DeploymentRequest
    {
        public string BuildId { get; set; }
        public string DestinationDirectory { get; set; }
    }

    public sealed class Ev3DeploymentResult
    {
        public Ev3DeploymentResult()
        {
            UploadedPaths = new List<string>();
        }

        public string ProgramPath { get; set; }
        public IList<string> UploadedPaths { get; set; }
    }

    public sealed class WorkspaceFileEntry
    {
        public string RelativePath { get; set; }
        public bool IsDirectory { get; set; }
        public long? Size { get; set; }
        public DateTimeOffset? ModifiedAt { get; set; }
    }

    public sealed class WorkspaceDocument
    {
        public string RelativePath { get; set; }
        public bool IsActive { get; set; }
        public bool IsDirty { get; set; }
    }

    public sealed class WorkspaceContextData
    {
        public WorkspaceContextData()
        {
            Files = new List<WorkspaceFileEntry>();
            OpenDocuments = new List<WorkspaceDocument>();
            Diagnostics = new List<AgentDiagnostic>();
        }

        public string ProjectRoot { get; set; }
        public string ProjectName { get; set; }
        public string Language { get; set; }
        public string ActiveDocument { get; set; }
        public IList<WorkspaceFileEntry> Files { get; set; }
        public IList<WorkspaceDocument> OpenDocuments { get; set; }
        public IList<AgentDiagnostic> Diagnostics { get; set; }
        public RobotProfile Robot { get; set; }
        public Ev3StatusData Ev3 { get; set; }
    }

    public sealed class BpBuildResult
    {
        public BpBuildResult()
        {
            Diagnostics = new List<AgentDiagnostic>();
            SourceFiles = new List<string>();
            MediaFiles = new List<string>();
            FileAssets = new List<string>();
        }

        public bool Success { get; set; }
        public string BuildId { get; set; }
        public string EntryPath { get; set; }
        public string TargetFileName { get; set; }
        public string ArtifactPath { get; set; }
        public string DestinationKind { get; set; }
        public string DestinationName { get; set; }
        public IList<string> SourceFiles { get; set; }
        public IList<string> MediaFiles { get; set; }
        public IList<string> FileAssets { get; set; }
        public IList<AgentDiagnostic> Diagnostics { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }

    public sealed class DocumentationSearchHit
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public string SourcePath { get; set; }
        public double Score { get; set; }
    }

    public sealed class DocumentationTopic
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Markdown { get; set; }
        public string SourcePath { get; set; }
    }

    public interface IWorkspaceContextService
    {
        Task<OperationResult<WorkspaceContextData>> SaveAllAndCaptureAsync(CancellationToken cancellationToken);
        Task<OperationResult<WorkspaceContextData>> CaptureAsync(CancellationToken cancellationToken);
    }

    public interface IBpBuildService
    {
        Task<OperationResult<BpBuildResult>> CompileAsync(string entryPath, CancellationToken cancellationToken);
        OperationResult<BpBuildResult> GetBuild(string buildId);
    }

    public interface IDocumentationSearchService
    {
        Task<OperationResult<IList<DocumentationSearchHit>>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
        Task<OperationResult<DocumentationTopic>> ReadAsync(string id, CancellationToken cancellationToken);
        string GetCompactGuide();
    }

    public interface IAgentApprovalService
    {
        Task<bool> RequestApprovalAsync(AgentToolDefinition definition, string actionSummary, CancellationToken cancellationToken);
    }

    public interface IEv3SessionService
    {
        Ev3StatusData State { get; }
        event EventHandler StateChanged;
        Task<OperationResult<Ev3StatusData>> ConnectAsync(CancellationToken cancellationToken);
        Task<OperationResult<Ev3StatusData>> DisconnectAsync(CancellationToken cancellationToken);
        Task<OperationResult<Ev3StatusData>> RefreshStatusAsync(CancellationToken cancellationToken);
        Task<OperationResult<IList<Ev3FileEntry>>> ListAsync(string path, CancellationToken cancellationToken);
        Task<OperationResult<Ev3FileContent>> ReadAsync(string path, int maxBytes, CancellationToken cancellationToken);
        Task<OperationResult<bool>> UploadAsync(string localPath, string destinationPath, CancellationToken cancellationToken);
        Task<OperationResult<bool>> CreateDirectoryAsync(string path, CancellationToken cancellationToken);
        Task<OperationResult<bool>> DeleteAsync(string path, CancellationToken cancellationToken);
        Task<OperationResult<bool>> SetDeviceNameAsync(string name, CancellationToken cancellationToken);
        Task<OperationResult<Ev3DeploymentResult>> DeployAsync(Ev3DeploymentRequest request, CancellationToken cancellationToken);
        Task<OperationResult<bool>> RunAsync(string path, CancellationToken cancellationToken);
        Task<OperationResult<bool>> StopAsync(CancellationToken cancellationToken);
    }
}
