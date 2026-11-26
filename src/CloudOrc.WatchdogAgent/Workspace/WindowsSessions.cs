using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace CloudOrc.WatchdogAgent.Workspace;

public sealed record InteractiveSession(int SessionId, string Username, string? Domain, DateTimeOffset? LogonTime, bool Active);

/// <summary>Seams for the supervisor so its loop can be tested without real sessions.</summary>
public interface ISessionHost
{
    IReadOnlyList<InteractiveSession> GetUserSessions();

    IReadOnlyCollection<int> GetSessionsRunning(string processName);

    /// <summary>User SID of the session's logged-on user (needs SYSTEM); null when unavailable.</summary>
    string? GetSessionUserSid(int sessionId);

    /// <summary>Starts <paramref name="exePath"/> as the session's user on its interactive desktop. Needs SYSTEM (SeTcbPrivilege).</summary>
    bool TryStartInSession(int sessionId, string exePath, string arguments, out string? error);

    /// <summary>Running processes with this name: session, process id, start time (UTC; null when unreadable).</summary>
    IReadOnlyList<SessionProcess> GetProcesses(string processName);

    /// <summary>Ends one process (hung panel / panel that ignores a fallback switch). Needs SYSTEM for other users' processes.</summary>
    bool TryKill(int processId, out string? error);
}

public sealed record SessionProcess(int SessionId, int ProcessId, DateTimeOffset? StartedAt);

/// <summary>WTS / CreateProcessAsUser implementation (supported service-to-user-session launch, handoff §47).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSessionHost : ISessionHost
{
    public IReadOnlyList<InteractiveSession> GetUserSessions()
    {
        var result = new List<InteractiveSession>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            return result;
        }

        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                if (info.SessionId == 0)
                {
                    continue;
                }

                var state = (WTS_CONNECTSTATE_CLASS)info.State;
                if (state is not (WTS_CONNECTSTATE_CLASS.WTSActive or WTS_CONNECTSTATE_CLASS.WTSDisconnected))
                {
                    continue;
                }

                var user = QueryString(info.SessionId, WTS_INFO_CLASS.WTSUserName);
                if (string.IsNullOrWhiteSpace(user))
                {
                    continue;
                }

                result.Add(new InteractiveSession(
                    info.SessionId,
                    user,
                    QueryString(info.SessionId, WTS_INFO_CLASS.WTSDomainName),
                    QueryLogonTime(info.SessionId),
                    state == WTS_CONNECTSTATE_CLASS.WTSActive));
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return result;
    }

    public IReadOnlyCollection<int> GetSessionsRunning(string processName)
    {
        var sessions = new HashSet<int>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
        {
            try
            {
                sessions.Add(p.SessionId);
            }
            catch (InvalidOperationException)
            {
                // exited meanwhile
            }
            finally
            {
                p.Dispose();
            }
        }

        return sessions;
    }

    public string? GetSessionUserSid(int sessionId)
    {
        if (!WTSQueryUserToken((uint)sessionId, out var token))
        {
            return null;
        }

        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public IReadOnlyList<SessionProcess> GetProcesses(string processName)
    {
        var list = new List<SessionProcess>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
        {
            try
            {
                DateTimeOffset? started = null;
                try { started = new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                list.Add(new SessionProcess(p.SessionId, p.Id, started));
            }
            catch (InvalidOperationException)
            {
                // exited meanwhile
            }
            finally
            {
                p.Dispose();
            }
        }

        return list;
    }

    public bool TryKill(int processId, out string? error)
    {
        error = null;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(processId);
            p.Kill();
            p.WaitForExit(5000);
            return true;
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryStartInSession(int sessionId, string exePath, string arguments, out string? error)
    {
        error = null;
        IntPtr userToken = IntPtr.Zero, primary = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken((uint)sessionId, out userToken))
            {
                var win32 = Marshal.GetLastWin32Error();
                // Console/dev run (not LocalSystem): the only session we may start a process in is our
                // own, as ourselves. As a service the Watch runs in session 0, so this never applies there.
                if (sessionId == System.Diagnostics.Process.GetCurrentProcess().SessionId)
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, arguments)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(exePath)
                    });
                    return p is not null;
                }

                error = $"WTSQueryUserToken failed ({new Win32Exception(win32).Message}) - the Watch must run as LocalSystem.";
                return false;
            }

            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SECURITY_IMPERSONATION_LEVEL.SecurityIdentification, TOKEN_TYPE.TokenPrimary, out primary))
            {
                error = $"DuplicateTokenEx failed ({Marshal.GetLastWin32Error()}).";
                return false;
            }

            if (!CreateEnvironmentBlock(out env, primary, false))
            {
                env = IntPtr.Zero; // fall back to no custom environment
            }

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var commandLine = $"\"{exePath}\" {arguments}".TrimEnd();
            var flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
            if (!CreateProcessAsUser(primary, null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, env,
                    Path.GetDirectoryName(exePath), ref si, out var pi))
            {
                error = $"CreateProcessAsUser failed ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).";
                return false;
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    private static string? QueryString(int sessionId, WTS_INFO_CLASS cls)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, cls, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static DateTimeOffset? QueryLogonTime(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTS_INFO_CLASS.WTSSessionInfo, out var buffer, out _))
        {
            return null;
        }

        try
        {
            var info = Marshal.PtrToStructure<WTSINFOW>(buffer);
            return info.LogonTime > 0 ? DateTimeOffset.FromFileTime(info.LogonTime) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // ---------------------------------------------------------------- interop

    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive, WTSConnected, WTSConnectQuery, WTSShadow, WTSDisconnected, WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit
    }

    private enum WTS_INFO_CLASS
    {
        WTSUserName = 5,
        WTSDomainName = 7,
        WTSSessionInfo = 24
    }

    private enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }

    private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTSINFOW
    {
        public int State;
        public int SessionId;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, WTS_INFO_CLASS infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, SECURITY_IMPERSONATION_LEVEL level, TOKEN_TYPE type, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
