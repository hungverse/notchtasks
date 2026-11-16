using NotchTasks.Core.Permissions;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class FirewallRulesTests
{
    private const string AppExe = @"C:\Apps\NotchTasks\NotchTasks.exe";

    [Fact]
    public void AddArguments_AllowInboundOnPrivateAndDomain()
    {
        var expected = "advfirewall firewall add rule name=\"NotchTasks\" dir=in action=allow program=\""
            + AppExe + "\" profile=private,domain enable=yes";

        Assert.Equal(expected, FirewallRules.AddArguments("NotchTasks", AppExe));
    }

    [Fact]
    public void ShowArguments_NamesTheRule()
    {
        Assert.Equal("advfirewall firewall show rule name=\"NotchTasks\"", FirewallRules.ShowArguments("NotchTasks"));
    }

    [Fact]
    public void Shows_TrueWhenTheRuleNamesTheAppExe()
    {
        var output = "Rule Name: NotchTasks\r\nProgram: " + AppExe + "\r\nEnabled: Yes\r\n";

        Assert.True(FirewallRules.Shows(0, output, AppExe));
    }

    [Fact]
    public void Shows_FalseForAnotherProgramWithTheSameName()
    {
        var output = "Rule Name: NotchTasks\r\nProgram: " + @"C:\Old\NotchTasks.exe" + "\r\nEnabled: Yes\r\n";

        Assert.False(FirewallRules.Shows(0, output, AppExe));
    }

    [Fact]
    public void Shows_FalseWhenNetshFails()
    {
        Assert.False(FirewallRules.Shows(1, "No rules match the specified criteria.", AppExe));
    }
}
