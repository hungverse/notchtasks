using System.Windows;

namespace NotchTasks.Pill;

/// The open pill's sections, one per module (Mac: each module's card). A module registers how to build its section from the
/// app state; the open pill shows the sections of the active modules in their order, with calls first. A builder returns
/// null when its module has nothing to show.
public static class SectionRegistry
{
    private static readonly Dictionary<string, Func<AppState, FrameworkElement?>> Builders = new(StringComparer.Ordinal);

    /// Sets how a module's section is built. A later registration for the same module replaces the earlier one.
    public static void Register(string module, Func<AppState, FrameworkElement?> build) => Builders[module] = build;

    /// Removes a module's builder: its section no longer shows. Called when the pill that registered it closes.
    public static void Unregister(string module) => Builders.Remove(module);

    /// The section of one module, or null when the module has none registered or nothing to show.
    public static FrameworkElement? Section(string module, AppState state) =>
        Builders.TryGetValue(module, out var build) ? build(state) : null;

    /// The modules in section order: "calls" first, then the rest in their own order.
    public static IReadOnlyList<string> Order(IReadOnlyList<string> modules) =>
        modules.Where(m => m == "calls").Concat(modules.Where(m => m != "calls")).ToList();

    /// The sections to show now: the active modules' sections in Order.
    public static IReadOnlyList<FrameworkElement> For(AppState state)
    {
        var sections = new List<FrameworkElement>();
        foreach (var module in Order(state.Snapshot.ActiveModules))
        {
            if (Section(module, state) is { } section)
            {
                sections.Add(section);
            }
        }
        return sections;
    }
}
