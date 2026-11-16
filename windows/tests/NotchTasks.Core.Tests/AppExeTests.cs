using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class AppExeTests
{
    private const string Base = @"C:\Apps\NotchTasks\";

    [Fact]
    public void RunAsDotnetDll_UsesNotchTasksExeBesideTheDll()
    {
        // `dotnet NotchTasks.dll`: the process is dotnet.exe, which must never reach the Run key or the firewall rule.
        Assert.Equal(Path.Combine(Base, "NotchTasks.exe"), AppExe.Resolve(@"C:\Program Files\dotnet\dotnet.exe", Base));
    }

    [Fact]
    public void DotnetName_IsMatchedWhateverTheCase()
    {
        Assert.Equal(Path.Combine(Base, "NotchTasks.exe"), AppExe.Resolve(@"C:\dotnet\DOTNET.EXE", Base));
        Assert.Equal(Path.Combine(Base, "NotchTasks.exe"), AppExe.Resolve(@"C:\dotnet\dotnet", Base));
    }

    [Fact]
    public void AppExe_IsKept()
    {
        Assert.Equal(@"D:\Install\NotchTasks.exe", AppExe.Resolve(@"D:\Install\NotchTasks.exe", Base));
    }

    [Fact]
    public void NoProcessPath_UsesNotchTasksExeBesideTheBase()
    {
        Assert.Equal(Path.Combine(Base, "NotchTasks.exe"), AppExe.Resolve(null, Base));
        Assert.Equal(Path.Combine(Base, "NotchTasks.exe"), AppExe.Resolve("", Base));
    }
}
