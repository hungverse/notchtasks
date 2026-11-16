using System.IO;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Core.Sounds;
using NotchTasks.Pill;

namespace NotchTasks.Services;

/// Plays the sound an event has in settings (SoundMap[eventId]) with PlaySound from winmm, so no media stack is loaded.
/// Runs on the UI thread. Silence when sounds are off, the name is empty or unknown, or the file is missing.
/// The volume is applied by scaling the WAV's samples (WavVolume); the scaled bytes are cached per file and volume step,
/// and the bytes of the last sound are kept in a field, because PlaySound with SND_MEMORY plays from them while it runs.
public static class Sounds
{
    private const uint SndMemory = 0x0004;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const double VolumeStep = 0.05;

    private static readonly Dictionary<string, byte[]> FileBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string File, int Step), byte[]> Scaled = [];
    private static byte[]? _playing;
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

        var step = (int)Math.Round(Math.Clamp(settings.Volume, 0, 1) / VolumeStep);
        if (!Scaled.TryGetValue((file, step), out var data))
        {
            if (!FileBytes.TryGetValue(file, out var bytes))
            {
                bytes = File.ReadAllBytes(file);
                FileBytes[file] = bytes;
            }
            data = WavVolume.Scale(bytes, step * VolumeStep);
            Scaled[(file, step)] = data;
        }

        _playing = data;
        Win32.PlaySound(data, nint.Zero, SndMemory | SndAsync | SndNoDefault);
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
