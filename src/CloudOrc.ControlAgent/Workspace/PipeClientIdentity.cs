using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace CloudOrc.ControlAgent.Workspace;

/// <summary>
/// Who is on the other end of a local named pipe — derived by the agent from the OS, never from
/// anything the client sends (plan D3). The client must connect with at least
/// TokenImpersonationLevel.Identification so the agent can read its token.
/// </summary>
public sealed record PipeClientIdentity(int ProcessId, int SessionId, string AccountName, string UserSid)
{
    /// <summary>"DOMAIN\user" -&gt; "user".</summary>
    public string BareUserName
    {
        get
        {
            var i = AccountName.LastIndexOf('\\');
            return i >= 0 ? AccountName[(i + 1)..] : AccountName;
        }
    }

    [SupportedOSPlatform("windows")]
    public static PipeClientIdentity Resolve(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetNamedPipeClientProcessId failed");
        }

        if (!ProcessIdToSessionId(pid, out var sessionId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ProcessIdToSessionId failed");
        }

        string? name = null;
        string? sid = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            name = identity.Name;
            sid = identity.User?.Value;
        });

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sid))
        {
            throw new UnauthorizedAccessException("Could not identify the pipe client.");
        }

        return new PipeClientIdentity((int)pid, (int)sessionId, name!, sid!);
    }

    /// <summary>Interactive user sessions only: not session 0 and not a service/system account.</summary>
    public bool IsInteractiveUser =>
        SessionId > 0
        && !UserSid.Equals("S-1-5-18", StringComparison.Ordinal)   // LocalSystem
        && !UserSid.Equals("S-1-5-19", StringComparison.Ordinal)   // LocalService
        && !UserSid.Equals("S-1-5-20", StringComparison.Ordinal);  // NetworkService

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
