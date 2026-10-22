using NEW_CLEVER.AgentCore;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NEW_CLEVER.Model.Agent
{
    internal sealed class WpfAgentApprovalService : IAgentApprovalService
    {
        public Task<bool> RequestApprovalAsync(AgentToolDefinition definition, string actionSummary, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
            if (dispatcher == null)
                return Task.FromResult(false);

            return dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var title = "Codex · NEW_CLEVER";
                var message = "Codex requests permission to perform this action:\n\n" + (actionSummary ?? definition.Name) + "\n\nContinue?";
                return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            }).Task;
        }
    }
}
