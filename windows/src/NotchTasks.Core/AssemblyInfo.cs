using System.Runtime.CompilerServices;

// The Core tests reach the sync seams' internal test hooks (SyncDeps.Delay).
[assembly: InternalsVisibleTo("NotchTasks.Core.Tests")]
