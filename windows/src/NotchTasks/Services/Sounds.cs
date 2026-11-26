using System.IO;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Core.Sounds;
using NotchTasks.Pill;

namespace NotchTasks.Services;

/// Plays the sound an event has in settings (SoundMap[eventId]) with PlaySound from winmm, so no media stack is loaded.
/// Runs on the UI thread. Silence when sounds are off, the name is empty or unknown, or the file is missing.
/// The volume is applied by scaling the WAV's samples (WavVolume); the scaled bytes are cached per file and volume step
/// (full volume plays the file's own bytes). All buffers are pinned, and the last one is kept in a field, because
/// PlaySound with SND_MEMORY|SND_ASYNC plays from them while it runs.
public static class Sounds
{
    private const uint SndMemory = 0x0004;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const double VolumeStep = 0.05;
    private const int FullVolumeStep = 20;  // 1.0: the file's own bytes play, with no scaled copy

    private static readonly Dictionary<string, byte[]> FileBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string File, int Step), byte[]> Scaled = [];
    private static byte[]? _playing;
    private static MediaPlayer? _media;
    private static readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);
    private static SettingsStore? _settings;

    /// Gives the sounds the settings to read. Call once at start-up, on the UI thread.
    public static void Attach(SettingsStore settings) => _settings = settings;

    /// Plays the sound the event has in the settings, when sounds are on.
    public static void Play(string eventId)
    {
        var settings = _settings;
        if (settings is null || !settings.Sounds)
        {
            return;
        }
        if (!settings.SoundMap.TryGetValue(eventId, out var name))
        {
            return;
        }
        PlayName(name);
    }

    /// Plays a sound by its name (a Mac sound name or a file path) at the settings' volume, whether or not sounds are on:
    /// the Sound pane's preview. Silence for "" or a name with no file.
    public static void PlayName(string name)
    {
        var settings = _settings;
        if (settings is null || string.IsNullOrEmpty(name))
        {
            return;
        }
        var file = ResolveFile(name);
        if (file is null)
        {
            return;
        }
        if (!string.Equals(Path.GetExtension(file), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            PlayMedia(file, settings.Volume);
            return;
        }

        // PlaySound with SND_ASYNC keeps reading the buffer after the call, so every buffer is a pinned array: the GC cannot move it.
        var step = (int)Math.Round(Math.Clamp(settings.Volume, 0, 1) / VolumeStep);
        var bytes = FileBytesOf(file);
        if (bytes is null)
        {
            return;
        }
        var data = step == FullVolumeStep ? bytes : ScaledBytesOf(file, step, bytes);

        _playing = data;
        Win32.PlaySound(data, nint.Zero, SndMemory | SndAsync | SndNoDefault);
    }

    /// Plays a file that PlaySound cannot (mp3, m4a, aac, wma) through MediaPlayer, at the volume. One player is kept in a
    /// field so the GC does not collect it mid-play; it is closed when it ends or fails, and a new sound replaces it.
    private static void PlayMedia(string file, double volume)
    {
        _media?.Close();
        var player = new MediaPlayer { Volume = Math.Clamp(volume, 0, 1) };
        player.MediaEnded += (_, _) => Release(player);
        player.MediaFailed += (_, _) => Release(player);
        player.Open(new Uri(file, UriKind.Absolute));
        player.Play();
        _media = player;
    }

    private static void Release(MediaPlayer player)
    {
        player.Close();
        if (ReferenceEquals(_media, player))
        {
            _media = null;
        }
    }

    /// The file's bytes in a pinned array, read once per file.
    private static byte[]? FileBytesOf(string file)
    {
        if (!FileBytes.TryGetValue(file, out var bytes))
        {
            try
            {
                bytes = Pinned(File.ReadAllBytes(file));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // No sound for a file that cannot be read (in use, no access). Logged once per file, not on every play.
                if (_unreadable.Add(file))
                {
                    ErrorLog.Write(new IOException($"NotchTasks: could not read the sound {file}", e));
                }
                return null;
            }
            FileBytes[file] = bytes;
        }
        return bytes;
    }

    /// The file's samples scaled to a volume step, in a pinned array, cached per file and step.
    private static byte[] ScaledBytesOf(string file, int step, byte[] bytes)
    {
        if (!Scaled.TryGetValue((file, step), out var data))
        {
            data = Pinned(WavVolume.Scale(bytes, step * VolumeStep));
            Scaled[(file, step)] = data;
        }
        return data;
    }

    /// A copy of the bytes in an array the GC does not move (pinned), for PlaySound to read while it plays.
    private static byte[] Pinned(byte[] source)
    {
        var copy = GC.AllocateUninitializedArray<byte>(source.Length, pinned: true);
        source.CopyTo(copy, 0);
        return copy;
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
}
