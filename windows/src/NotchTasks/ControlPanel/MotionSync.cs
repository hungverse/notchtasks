using System.IO;
using NotchTasks.Core;
using NotchTasks.Pill;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel;

/// Sync motion (Animations.sync on the Mac): every animated icon restarts at once. The moment is kept in state.json, so the
/// icons are still in step after a restart. The pill's sprites move to the new moment at once.
public static class MotionSync
{
    public static void Now()
    {
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SpriteClock.SetEpoch(epoch);
        try
        {
            AnimationEpoch.Write(AppPaths.StateFile, epoch);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException("NotchTasks: could not save the animation sync", e));
        }
    }
}
