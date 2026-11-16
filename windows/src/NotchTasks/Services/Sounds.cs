using System.IO;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Core.Sounds;

namespace NotchTasks.Services;

/// Plays the sound an event has in settings (SoundMap[eventId]). Runs on the UI thread. Silence when sounds are off,
/// the name is empty or unknown, or the file is missing. A player is kept until it ends or fails, so it is not collected mid-play.
public static class Sounds
{
    private static readonly HashSet<MediaPlayer> Playing = [];
    private static SettingsStore? _settings;

    /// Gives the sounds the settings to read. Call once at start-up, on the UI thread.
    public static void Attach(SettingsStore settings) => _settings = settings;

    public static void Play(string eventId)
    {
        var settings = _settings;
        if (settings is null || !settings.Sounds)
        {
            return;
        }
        if (!settings.SoundMap.TryGetValue(eventId, out var name) || string.IsNullOrEmpty(name))
        {
            return;
        }
        var file = ResolveFile(name);
        if (file is null)
        {
            return;
        }

        var player = new MediaPlayer { Volume = settings.Volume };
        Playing.Add(player);
        player.MediaEnded += (_, _) => Release(player);
        player.MediaFailed += (_, _) => Release(player);
        player.Open(new Uri(file, UriKind.Absolute));
        player.Play();
    }

    /// A full path as it is, or the Windows file for a Mac sound name under C:\Windows\Media; null when there is none.
    private static string? ResolveFile(string name)
    {
        if (Path.IsPathRooted(name))
        {
            return File.Exists(name) ? name : null;
        }
        var fileName = SoundFiles.FileName(name);
        if (fileName is null)
        {
            return null;
        }
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", fileName);
        return File.Exists(file) ? file : null;
    }

    private static void Release(MediaPlayer player)
    {
        player.Close();
        Playing.Remove(player);
    }
}
