using NotchTasks.Core.Agents;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/devscan.test.js (DevRules.project, folderFromArgv) and electron/test/providers.test.js (ago).
[Collection("L10n")]
public class FoldersAndAgoTests
{
    // Ago.Text is compared as English copy: the strings are loaded in English for every test of the class
    public FoldersAndAgoTests() => AsksFixtures.InitEnglish();

    [Fact]
    public void Project_HandlesBothSeparators_AndSkipsPlatformFolders()
    {
        Assert.Equal("mobile-app", Folders.Project(@"C:\work\mobile-app\ios"));
        Assert.Equal("shop", Folders.Project("/Users/dev/shop/web/"));
    }

    [Fact]
    public void FolderFromArgv_TakesTheProjectOfTheFirstPath()
    {
        Assert.Equal("Shop", Folders.FolderFromArgv(new[] { "MSBuild.exe", "/t:Build", @"C:\src\Shop\Shop.sln" }));
    }

    [Fact]
    public void FolderFromArgv_NodeModulesPath_GivesTheFolderThatHoldsIt()
    {
        Assert.Equal("p", Folders.FolderFromArgv(new[] { "node", @"C:\p\node_modules\vitest\vitest.mjs", "run" }));
    }

    [Fact]
    public void FolderFromArgv_NoPath_IsNull()
    {
        Assert.Null(Folders.FolderFromArgv(new[] { "node", "server.js" }));
        Assert.Null(Folders.FolderFromArgv(new[] { "x", "-flag", "--other" }));
        Assert.Null(Folders.FolderFromArgv(Array.Empty<string>()));
    }

    [Fact]
    public void Ago_SecondsMinutesHours()
    {
        Assert.Equal("30 sec", Ago.Text(30));
        Assert.Equal("59 sec", Ago.Text(59.9));
        Assert.Equal("2 min", Ago.Text(120));
        Assert.Equal("2 hours", Ago.Text(7200));
    }
}
