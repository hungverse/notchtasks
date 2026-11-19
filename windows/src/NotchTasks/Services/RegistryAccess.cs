using System.IO;
using System.Security;
using Microsoft.Win32;
using NotchTasks.Core;

namespace NotchTasks.Services;

/// IRegistry over the current user's registry (HKEY_CURRENT_USER). A DWORD is read as its digits, so "0" is a zero value.
/// A key that cannot be read (missing, or denied) reads as missing: the reads never throw.
public sealed class RegistryAccess : IRegistry
{
    public string? GetString(string keyPath, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(name)?.ToString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    public long? GetLong(string keyPath, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(name) switch
            {
                long value => value,
                int value => value,
                _ => null,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> SubKeyNames(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Array.Empty<string>();
        }
    }

    public void SetString(string keyPath, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string keyPath, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
