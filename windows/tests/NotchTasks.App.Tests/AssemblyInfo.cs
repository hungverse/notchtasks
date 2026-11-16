using Xunit;

// The windows are WPF objects on STA threads and share AppPaths.Root and the one Application: one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
