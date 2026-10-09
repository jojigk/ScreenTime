using System.Security.AccessControl;
using System.Security.Principal;

namespace ScreenTimeService.Security;

/// <summary>
/// Locks down C:\ProgramData\ScreenTime so config/state can only be read or written by
/// Administrators and SYSTEM — a standard user (including the one running the agent) must
/// not be able to edit the quota or forge usage state to bypass it.
/// </summary>
public static class AclHelper
{
    public static void EnsureProgramDataDirectory(string directoryPath)
    {
        var directoryInfo = Directory.Exists(directoryPath)
            ? new DirectoryInfo(directoryPath)
            : Directory.CreateDirectory(directoryPath);

        var security = new DirectorySecurity();
        // Drop inherited rules so no ambient "Users" grant survives onto this folder.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var flags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));

        directoryInfo.SetAccessControl(security);
    }
}
