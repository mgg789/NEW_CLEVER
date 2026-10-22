using NEW_CLEVER.AgentCore;
using NEW_CLEVER.Model.Utils;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NEW_CLEVER.Model.Agent
{
    internal sealed class NEW_CLEVERAgentServices : IDisposable
    {
        private static readonly Lazy<NEW_CLEVERAgentServices> Instance =
            new Lazy<NEW_CLEVERAgentServices>(() => new NEW_CLEVERAgentServices());

        private NEW_CLEVERAgentServices()
        {
            SessionBackend = new DisconnectedCodexSessionBackend();
            SlashCommands = new SlashCommandCatalog();
            ToolRegistry = new AgentToolRegistry();
        }

        internal static NEW_CLEVERAgentServices Current
        {
            get { return Instance.Value; }
        }

        internal ICodexSessionBackend SessionBackend { get; private set; }
        internal SlashCommandCatalog SlashCommands { get; private set; }
        internal AgentToolRegistry ToolRegistry { get; private set; }
        internal IWorkspaceContextService WorkspaceContext { get; private set; }
        internal IBpBuildService Build { get; private set; }
        internal IEv3SessionService Ev3 { get; private set; }
        internal IDocumentationSearchService Documentation { get; private set; }
        internal IExampleLibraryService Examples { get; private set; }
        internal IProjectSourceSearchService ProjectSource { get; private set; }
        internal IAgentApprovalService Approvals { get; private set; }
        internal IGitWorkspaceService GitWorkspace { get; private set; }
        internal IEditorNavigationService EditorNavigation { get; private set; }
        private CodexRuntimeManager _runtimeManager;
        private CodexMcpBridgeHost _bridgeHost;
        private readonly WpfCodexUserInputService _userInput = new WpfCodexUserInputService();
        private Task _backendStartTask;

        internal void ConfigureFoundation(
            IWorkspaceContextService workspaceContext,
            IBpBuildService build,
            IEv3SessionService ev3,
            IDocumentationSearchService documentation,
            IExampleLibraryService examples,
            IProjectSourceSearchService projectSource,
            IAgentApprovalService approvals)
        {
            WorkspaceContext = workspaceContext ?? throw new ArgumentNullException("workspaceContext");
            Build = build ?? throw new ArgumentNullException("build");
            Ev3 = ev3 ?? throw new ArgumentNullException("ev3");
            Documentation = documentation ?? throw new ArgumentNullException("documentation");
            Examples = examples ?? throw new ArgumentNullException("examples");
            ProjectSource = projectSource ?? throw new ArgumentNullException("projectSource");
            Approvals = approvals ?? throw new ArgumentNullException("approvals");
            GitWorkspace = new GitWorkspaceService(WorkspaceContext);
            EditorNavigation = new EditorNavigationService(WorkspaceContext);
            ToolRegistry = NEW_CLEVERToolRegistryFactory.Create(WorkspaceContext, Build, Ev3, Documentation, Examples, ProjectSource, Approvals, GitWorkspace, EditorNavigation);
        }

        internal void InitializeDefaultFoundation()
        {
            if (WorkspaceContext != null)
                return;

            var build = new BpBuildService();
            var ev3 = new Ev3SessionService(build);
            var workspace = new WorkspaceContextService();
            var examples = new ExampleLibraryService();
            examples.Warmup();
            ConfigureFoundation(
                workspace,
                build,
                ev3,
                new DocumentationSearchService(),
                examples,
                new ProjectSourceSearchService(workspace),
                new WpfAgentApprovalService());

            _runtimeManager = new CodexRuntimeManager(AiAppSettingsStore.Settings.CodexExecutablePath);
            _bridgeHost = new CodexMcpBridgeHost(ToolRegistry);
            SetSessionBackend(new CodexAppServerBackend(_runtimeManager, _bridgeHost, ToolRegistry));
        }

        internal void StartSessionBackend()
        {
            if (_backendStartTask == null || _backendStartTask.IsCompleted)
                _backendStartTask = StartSessionBackendSafeAsync();
        }

        private async Task StartSessionBackendSafeAsync()
        {
            try
            {
                await SessionBackend.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The backend owns and publishes its actionable disconnected/error state.
                // Codex startup must never prevent the IDE from opening.
            }
        }

        internal void SetSessionBackend(ICodexSessionBackend backend)
        {
            if (backend == null)
                throw new ArgumentNullException("backend");
            SessionBackend.Dispose();
            backend.UserInputHandler = _userInput.HandleAsync;
            SessionBackend = backend;
        }

        public void Dispose()
        {
            SessionBackend.Dispose();
            if (_bridgeHost != null)
                _bridgeHost.Dispose();
            if (_runtimeManager != null)
                _runtimeManager.Dispose();
            var disposableEv3 = Ev3 as IDisposable;
            if (disposableEv3 != null)
                disposableEv3.Dispose();
        }
    }
}
