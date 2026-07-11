using Clev3r.AgentCore;
using Clever.Model.Utils;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Clever.Model.Agent
{
    internal sealed class Clev3rAgentServices : IDisposable
    {
        private static readonly Lazy<Clev3rAgentServices> Instance =
            new Lazy<Clev3rAgentServices>(() => new Clev3rAgentServices());

        private Clev3rAgentServices()
        {
            SessionBackend = new DisconnectedCodexSessionBackend();
            SlashCommands = new SlashCommandCatalog();
            ToolRegistry = new AgentToolRegistry();
        }

        internal static Clev3rAgentServices Current
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
        internal IAgentApprovalService Approvals { get; private set; }
        private CodexRuntimeManager _runtimeManager;
        private CodexMcpBridgeHost _bridgeHost;
        private readonly WpfCodexUserInputService _userInput = new WpfCodexUserInputService();
        private Task _backendStartTask;

        internal void ConfigureFoundation(
            IWorkspaceContextService workspaceContext,
            IBpBuildService build,
            IEv3SessionService ev3,
            IDocumentationSearchService documentation,
            IAgentApprovalService approvals)
        {
            WorkspaceContext = workspaceContext ?? throw new ArgumentNullException("workspaceContext");
            Build = build ?? throw new ArgumentNullException("build");
            Ev3 = ev3 ?? throw new ArgumentNullException("ev3");
            Documentation = documentation ?? throw new ArgumentNullException("documentation");
            Approvals = approvals ?? throw new ArgumentNullException("approvals");
            ToolRegistry = Clev3rToolRegistryFactory.Create(WorkspaceContext, Build, Ev3, Documentation, Approvals);
        }

        internal void InitializeDefaultFoundation()
        {
            if (WorkspaceContext != null)
                return;

            var build = new BpBuildService();
            var ev3 = new Ev3SessionService(build);
            ConfigureFoundation(
                new WorkspaceContextService(),
                build,
                ev3,
                new DocumentationSearchService(),
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
