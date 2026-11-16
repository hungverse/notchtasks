using Microsoft.Win32;
using NotchTasks.Core;

namespace NotchTasks.Services;

/// IRegistry over the current user's registry (HKEY_CURRENT_USER). A DWORD is read as its digits, so "0" is a zero value.
public sealed class RegistryAccess : IRegistry
{
    public string? GetString(string keyPath, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(name)?.ToString();
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
