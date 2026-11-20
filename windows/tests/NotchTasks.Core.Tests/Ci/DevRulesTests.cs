using NotchTasks.Core.Agents;
using NotchTasks.Core.Ci;
using Xunit;

namespace NotchTasks.Core.Tests.Ci;

// Ported from electron/test/devscan.test.js: the DevRules.classify, node, project/base and folderFromArgv cases.
// Electron has no explicit cases for mmss, symbol, python, ruby and script_; those are covered here through their behaviour.
[Collection("L10n")]
public sealed class DevRulesTests
{
    public DevRulesTests()
    {
        L10n.Init(AppContext.BaseDirectory, "en");
    }

    private static DevMatch Expect(DevMatch? m)
    {
        Assert.NotNull(m);
        return m!;
    }

    [Fact]
    public void Classify_WindowsAndCrossPlatformBuildCommands()
    {
        var next = Expect(DevRules.Classify("node", ["node", @"C:\p\node_modules\next\dist\bin\next", "build"]));
        Assert.Equal("next", next.Tool);
        Assert.Equal("build", next.Action);

        var ms = Expect(DevRules.Classify("msbuild", ["MSBuild.exe", "/t:Restore", "/p:Configuration=Release", @"C:\src\Shop\Shop.sln"]));
        Assert.Equal("MSBuild", ms.Tool);
        Assert.Equal("Restore", ms.Action);
        Assert.Equal("Shop", ms.Subject);

        Assert.Equal("build Release", Expect(DevRules.Classify("dotnet", ["dotnet", "build", "-c", "Release"])).Action);
        Assert.Equal("Cargo", Expect(DevRules.Classify("cargo", ["cargo", "build", "--release"])).Tool);
        Assert.Null(DevRules.Classify("cargo", ["cargo", "run"]));
        Assert.Equal("app", Expect(DevRules.Classify("git", ["git", "clone", "https://github.com/acme/app.git"])).Subject);
        Assert.Null(DevRules.Classify("git", ["git", "status"]));
        Assert.Equal("winget", Expect(DevRules.Classify("winget", ["winget.exe", "upgrade", "--all"])).Tool);
        Assert.Equal("pip", Expect(DevRules.Classify("python", ["python", "-m", "pip", "install", "requests"])).Tool);
        Assert.Null(DevRules.Classify("python", ["python", "-m", "pip", "list"]));
        Assert.Equal("Gradle", Expect(DevRules.Classify("java", ["java", "-cp", "gradle-wrapper.jar", "org.gradle.wrapper.GradleWrapperMain", "assembleDebug"])).Tool);
        Assert.Equal("render animation", Expect(DevRules.Classify("blender", ["blender", "-b", @"C:\art\hall.blend", "-a"])).Action);
        Assert.Null(DevRules.Classify("blender", ["blender", @"C:\art\hall.blend"]));
        Assert.Null(DevRules.Classify("notepad", ["notepad"]));
    }

    [Fact]
    public void Classify_Node_PackageScriptsDevServersAndWatchMode()
    {
        Assert.Equal(new DevMatch("npm", "run build", "hammer.fill"),
            DevRules.Classify("node", ["node", @"C:\Program Files\nodejs\node_modules\npm\bin\npm-cli.js", "run", "build"]));
        Assert.Null(DevRules.Classify("node", ["node", @"C:\p\node_modules\vite\bin\vite.js", "dev"]));
        Assert.Equal("vite", Expect(DevRules.Classify("node", ["node", @"C:\p\node_modules\vite\bin\vite.js", "build"])).Tool);
        Assert.Equal("vitest", Expect(DevRules.Classify("node", ["node", "/p/node_modules/vitest/vitest.mjs", "run"])).Tool);
        Assert.Null(DevRules.Classify("node", ["node", "/p/node_modules/vitest/vitest.mjs", "--watch"]));
        Assert.Null(DevRules.Classify("node", ["node", "server.js"]));
    }

    [Fact]
    public void Folders_ProjectAndFolderFromArgv_BothSeparators()
    {
        Assert.Equal("mobile-app", Folders.Project(@"C:\work\mobile-app\ios"));
        Assert.Equal("shop", Folders.Project("/Users/dev/shop/web/"));
        Assert.Equal("Shop", Folders.FolderFromArgv(["MSBuild.exe", "/t:Build", @"C:\src\Shop\Shop.sln"]));
        Assert.Equal("shop", Folders.FolderFromArgv(["node", "C:/p/shop/node_modules/next/dist/bin/next", "build"]));
        Assert.Null(Folders.FolderFromArgv(["node", "build"]));
        Assert.Equal("hall", DevRules.Base(@"C:\art\hall.blend"));
    }

    [Fact]
    public void Base_TakesLastComponentAndDropsScriptExtension()
    {
        Assert.Equal("hall", DevRules.Base(@"C:\art\hall.blend"));
        Assert.Equal("tool", DevRules.Base("/usr/bin/tool.mjs/"));
        Assert.Equal("run", DevRules.Base("run.rb"));
        Assert.Equal("notes.txt", DevRules.Base("notes.txt"));
    }

    [Fact]
    public void Mmss_RoundsUpAndAddsHours()
    {
        Assert.Equal("0:00", DevRules.Mmss(0));
        Assert.Equal("0:00", DevRules.Mmss(-5));
        Assert.Equal("0:30", DevRules.Mmss(30));
        Assert.Equal("1:00", DevRules.Mmss(59.2));
        Assert.Equal("59:59", DevRules.Mmss(3599));
        Assert.Equal("1:00:00", DevRules.Mmss(3600));
        Assert.Equal("1:02:06", DevRules.Mmss(3725.2));
    }

    [Fact]
    public void Symbol_PicksSymbolByWordInAction()
    {
        Assert.Equal("checkmark.diamond.fill", DevRules.Symbol("test unit"));
        Assert.Equal("icloud.and.arrow.up.fill", DevRules.Symbol("publish"));
        Assert.Equal("shippingbox.fill", DevRules.Symbol("install Git.Git"));
        Assert.Equal("hammer.fill", DevRules.Symbol("compile"));
    }

    [Fact]
    public void DefaultSymbol_ByTool()
    {
        Assert.Equal("server.rack", DevRules.DefaultSymbol("GitLab Runner"));
        Assert.Equal("arrow.down.circle.fill", DevRules.DefaultSymbol("curl"));
        Assert.Equal("film.stack", DevRules.DefaultSymbol("ffmpeg"));
        Assert.Equal("arrow.triangle.branch", DevRules.DefaultSymbol("Git"));
        Assert.Equal("hammer.fill", DevRules.DefaultSymbol("Xcode"));
    }

    [Fact]
    public void Classify_MsBuild_TargetsAndProject()
    {
        var m = Expect(DevRules.Classify("msbuild", ["msbuild", "/t:Restore", "/p:Configuration=Release", "App.sln"]));
        Assert.Equal("MSBuild", m.Tool);
        Assert.Equal("Restore", m.Action);
        Assert.Equal("App", m.Subject);
    }

    [Fact]
    public void Classify_Winget_Install()
    {
        Assert.Equal(new DevMatch("winget", "install Git.Git", "shippingbox.fill"),
            DevRules.Classify("winget", ["winget", "install", "Git.Git"]));
    }

    [Fact]
    public void Classify_Xcodebuild_InfoFlagsAreNotTasks_SchemeAndWorkspaceAreRead()
    {
        Assert.Null(DevRules.Classify("xcodebuild", ["xcodebuild", "-list"]));
        var m = Expect(DevRules.Classify("xcodebuild", ["xcodebuild", "-scheme", "App", "-workspace", @"C:\src\Shop\App.xcworkspace", "build"]));
        Assert.Equal("Xcode", m.Tool);
        Assert.Equal("build · App", m.Action);
        Assert.Equal("Shop", m.Subject);
    }

    [Fact]
    public void Classify_Docker_ComposePrefixesAction()
    {
        Assert.Equal("compose build", Expect(DevRules.Classify("docker", ["docker", "compose", "build"])).Action);
    }

    [Fact]
    public void Classify_Transfers_CopyDownloadAndCurlOnlyWithOutputFile()
    {
        Assert.Equal(new DevMatch("rsync", L10n.L("copying"), "arrow.left.arrow.right", "dst"),
            DevRules.Classify("rsync", ["rsync", "-a", "src/", "dst/"]));
        var curl = Expect(DevRules.Classify("curl", ["curl", "-o", @"C:\dl\setup.exe", "https://example.com/setup.exe"]));
        Assert.Equal(L10n.L("downloading"), curl.Action);
        Assert.Equal("setup.exe", curl.Subject);
        Assert.Null(DevRules.Classify("curl", ["curl", "https://api.example.com/items"]));
    }

    [Fact]
    public void Classify_Bun_RunsOnlyFiniteScripts()
    {
        Assert.Equal(new DevMatch("bun", "run build", "hammer.fill"), DevRules.Classify("bun", ["bun", "run", "build"]));
        Assert.Null(DevRules.Classify("bun", ["bun", "run", "dev"]));
    }

    [Fact]
    public void Classify_Python_ToolsAndScripts()
    {
        Assert.Equal("pytest", Expect(DevRules.Classify("python", ["python", "-m", "pytest"])).Tool);
        Assert.Equal("install", Expect(DevRules.Classify("python", ["python", "-m", "poetry", "install"])).Action);
        Assert.Equal("Django", Expect(DevRules.Classify("python", ["python", "manage.py", "test"])).Tool);
        Assert.Null(DevRules.Classify("python", ["python", "manage.py", "runserver"]));
        Assert.True(DevRules.IsPython("python3"));
        Assert.False(DevRules.IsPython("pyth"));
    }

    [Fact]
    public void Classify_Ruby_BundlerRspecAndPod()
    {
        Assert.Equal("Bundler", Expect(DevRules.Classify("ruby", ["ruby", @"C:\x\bin\bundle", "install"])).Tool);
        Assert.Equal("RSpec", Expect(DevRules.Classify("ruby", ["ruby", "bin/rspec", "spec"])).Tool);
        Assert.Equal("CocoaPods", Expect(DevRules.Classify("ruby", ["ruby", "/gems/bin/pod", "install"])).Tool);
        Assert.Null(DevRules.Classify("ruby", ["ruby", "-e", "x"]));
    }

    [Fact]
    public void Lists_CarryTheElectronEntries()
    {
        Assert.Contains("msbuild", DevRules.Names);
        Assert.Contains("winget", DevRules.Names);
        Assert.Contains("dev", DevRules.Endless);
        Assert.Contains("Tải file", DevRules.KnownTools);
        Assert.Contains(DevRules.RenderApps, r => r.Exe == "blender" && r.Name == "Blender");
    }
}
