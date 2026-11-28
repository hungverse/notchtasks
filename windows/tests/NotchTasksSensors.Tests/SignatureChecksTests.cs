using Xunit;

namespace NotchTasksSensors.Tests;

/// The Authenticode checks on real files: a Microsoft-signed system binary passes both checks; an unsigned file passes neither.
public sealed class SignatureChecksTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nt-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void MicrosoftSignedBinary_PassesTheSignatureAndTheSignerCheck()
    {
        // explorer.exe carries its Microsoft signature inside the file (system binaries under system32 are catalog-signed).
        var explorer = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "explorer.exe");

        var system = new WindowsPawnIOSystem();

        Assert.True(system.HasValidSignature(explorer));
        Assert.True(system.IsMicrosoftSigned(explorer));
    }

    [Fact]
    public void UnsignedFile_FailsBothChecks()
    {
        File.WriteAllText(_path, "not signed");
        var system = new WindowsPawnIOSystem();

        Assert.False(system.HasValidSignature(_path));
        Assert.False(system.IsMicrosoftSigned(_path));
    }
}
