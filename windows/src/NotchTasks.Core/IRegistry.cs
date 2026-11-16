namespace NotchTasks.Core;

/// The part of the registry the app reads and writes: values under a key path of HKEY_CURRENT_USER. The app implements it
/// over Microsoft.Win32.Registry; tests use a fake, so no test touches the real registry.
public interface IRegistry
{
    /// The value as text (a number as its digits), or null when the key or the value is missing.
    string? GetString(string keyPath, string name);

    /// Writes a string value, creating the key when it is missing.
    void SetString(string keyPath, string name, string value);

    /// Deletes the value. A missing key or value is not an error.
    void Delete(string keyPath, string name);
}
