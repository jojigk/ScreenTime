using System.ComponentModel;
using System.Text;
using static ScreenTimeService.Locking.NativeMethods;

namespace ScreenTimeService.Locking;

/// <summary>
/// Launches a process inside an interactive user session from a SYSTEM service. Session 0
/// isolation means SYSTEM can't just start a process and have it show up in the user's desktop —
/// this duplicates that session's own logon token and uses it to launch the process as that
/// (standard, non-elevated) user instead. Shared by <see cref="SessionLockService"/> (locking)
/// and the agent supervisor (relaunching a killed/missing agent).
/// </summary>
public sealed class InteractiveProcessLauncher(ILogger<InteractiveProcessLauncher> logger)
{
    private readonly object _privilegeLock = new();
    private bool _privilegesEnabled;

    public Task<bool> LaunchAsync(int sessionId, string? applicationName, string commandLine, string? workingDirectory) =>
        Task.Run(() => Launch(sessionId, applicationName, commandLine, workingDirectory));

    private bool Launch(int sessionId, string? applicationName, string commandLine, string? workingDirectory)
    {
        EnsurePrivilegesEnabled();

        var userToken = IntPtr.Zero;
        var primaryToken = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var processInfo = new PROCESS_INFORMATION();

        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                ThrowLastError(nameof(WTSQueryUserToken));
            }

            if (!DuplicateTokenEx(
                    userToken,
                    TOKEN_ALL_ACCESS,
                    IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                    TOKEN_TYPE.TokenPrimary,
                    out primaryToken))
            {
                ThrowLastError(nameof(DuplicateTokenEx));
            }

            CreateEnvironmentBlock(out environment, primaryToken, false);

            var startupInfo = new STARTUPINFO
            {
                cb = System.Runtime.InteropServices.Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default",
            };

            var commandLineBuilder = new StringBuilder(commandLine);

            var created = CreateProcessAsUser(
                primaryToken,
                applicationName,
                commandLineBuilder,
                processAttributes: IntPtr.Zero,
                threadAttributes: IntPtr.Zero,
                inheritHandles: false,
                creationFlags: CREATE_UNICODE_ENVIRONMENT | NORMAL_PRIORITY_CLASS,
                environment: environment,
                currentDirectory: workingDirectory,
                ref startupInfo,
                out processInfo);

            if (!created)
            {
                ThrowLastError(nameof(CreateProcessAsUser));
            }

            logger.LogInformation("Launched '{CommandLine}' in session {SessionId}.", commandLine, sessionId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to launch '{CommandLine}' in session {SessionId}.", commandLine, sessionId);
            return false;
        }
        finally
        {
            if (processInfo.hThread != IntPtr.Zero) CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero) CloseHandle(processInfo.hProcess);
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    /// <summary>
    /// LocalSystem holds these privileges but they're disabled by default; WTSQueryUserToken and
    /// CreateProcessAsUser both fail with ERROR_PRIVILEGE_NOT_HELD until they're explicitly enabled.
    /// </summary>
    private void EnsurePrivilegesEnabled()
    {
        lock (_privilegeLock)
        {
            if (_privilegesEnabled)
            {
                return;
            }

            EnablePrivilege(SE_TCB_NAME);
            EnablePrivilege(SE_ASSIGNPRIMARYTOKEN_NAME);
            EnablePrivilege(SE_INCREASE_QUOTA_NAME);
            _privilegesEnabled = true;
        }
    }

    private void EnablePrivilege(string privilegeName)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var processToken))
        {
            ThrowLastError(nameof(OpenProcessToken));
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                ThrowLastError(nameof(LookupPrivilegeValue));
            }

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED },
            };

            if (!AdjustTokenPrivileges(processToken, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                ThrowLastError(nameof(AdjustTokenPrivileges));
            }
        }
        finally
        {
            CloseHandle(processToken);
        }
    }

    private static void ThrowLastError(string apiName)
    {
        var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{apiName} failed with Win32 error {error}.");
    }
}
