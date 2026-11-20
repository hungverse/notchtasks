using System.IO;

namespace NotchTasks.Services;

/// Runs a change of the file shelf that saves state.json. A failed write is logged and reported as false, so the caller leaves the
/// UI as it was (the shelf's list is only replaced once its save has worked).
public static class ShelfSave
{
    /// Runs the change. Returns false, after logging the error, when the file could not be read or written.
    public static bool TrySave(Action change)
    {
        try
        {
            change();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(e);
            return false;
        }
    }
}
