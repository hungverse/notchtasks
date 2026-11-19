using System.Security.AccessControl;

namespace NotchTasks.Core.Sensors;

/// The rule the helper folder must keep: no ordinary user may write to it. Pure, so it is tested without the file system.
public static class SensorFolderRules
{
    /// The write-type rights. Read and execute rights are not here, so Users:(RX) is fine and Users:(M) is not (Modify holds
    /// WriteData, AppendData and Delete).
    public const FileSystemRights WriteRights = FileSystemRights.WriteData
        | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    /// The identities an ordinary user is in: Users, Authenticated Users, Everyone and INTERACTIVE.
    public static readonly IReadOnlySet<string> OrdinarySids = new HashSet<string>(StringComparer.Ordinal)
    {
        "S-1-5-32-545",
        "S-1-5-11",
        "S-1-1-0",
        "S-1-5-4",
    };

    /// True when the rights include a write-type right.
    public static bool GrantsWrite(FileSystemRights rights) => (rights & WriteRights) != 0;

    /// True when an Allow rule gives an ordinary identity a write-type right. The folder is refused when this holds.
    public static bool IsOrdinaryWrite(string identitySid, AccessControlType type, FileSystemRights rights) =>
        type == AccessControlType.Allow && OrdinarySids.Contains(identitySid) && GrantsWrite(rights);
}
