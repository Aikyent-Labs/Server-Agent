using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Workspace;

namespace CloudOrc.WorkspaceUI.Services;

public interface IAgentPipe
{
    Task<WorkspacePipeResponse?> SendAsync(WorkspacePipeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Talks to the Control Agent over the local workspace pipe. Connects with Identification-level
/// impersonation so the agent can read WHO is asking (it never trusts a name we send).
/// </summary>
public sealed class AgentPipeClient(string pipeName = WorkspaceProtocol.PipeName) : IAgentPipe
{
    public async Task<WorkspacePipeResponse?> SendAsync(WorkspacePipeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await pipe.ConnectAsync(3000, timeout.Token).ConfigureAwait(false);

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, WorkspaceProtocol.JsonOptions) + "\n");
            await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

            var buffer = new byte[WorkspaceProtocol.MaxMessageBytes];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await pipe.ReadAsync(buffer.AsMemory(total, buffer.Length - total), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0)
                {
                    break;
                }
            }

            var text = Encoding.UTF8.GetString(buffer, 0, total).Trim();
            return text.Length == 0 ? null : JsonSerializer.Deserialize<WorkspacePipeResponse>(text, WorkspaceProtocol.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"Agent pipe '{pipeName}' unavailable: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
