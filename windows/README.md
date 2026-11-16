# NotchTasks for Windows

Native Windows app (C#, WPF, .NET 10) that shows AI agents' questions in a floating pill at the top of the screen. The Mac app in `mac/` is the reference for behaviour and look; `electron/` is read only for Windows-specific details.

- `src/NotchTasks` - the WPF app (`NotchTasks.exe`)
- `src/NotchTasks.Core` - shared, AOT-safe core logic
- `src/NotchTasksHook` - the hook executable (`NotchTasksHook.exe`), copied next to the app
- `tests/NotchTasks.Core.Tests` - xUnit tests

Commands, from the repo root:

- `dotnet build windows/NotchTasks.sln`
- `dotnet test windows/NotchTasks.sln`
- `dotnet run --project windows/src/NotchTasks`
