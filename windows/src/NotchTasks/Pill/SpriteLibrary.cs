using NotchTasks.Core;

namespace NotchTasks.Pill;

/// One sprite of the catalog (Sprites\catalog.json): its id, its category (a Mac category key) and its title (the key of its name).
public sealed record SpriteEntry(string Id, string Category, string Title)
{
    /// The name in the open language.
    public string Name => L10n.L(Title);
}

/// The Icon Library's sprites (Animation.swift SpriteLibrary): the categories in the Mac's order. Pets are not in the library.
public static class SpriteLibrary
{
    /// The categories of the library, in the Mac's order. Their names are the keys of the Mac's texts.
    public static IReadOnlyList<string> Categories { get; } = ["AI", "Cày cuốc", "Dễ thương", "Robot", "Anime"];

    /// Every catalog sprite in the catalog's order.
    public static IReadOnlyList<SpriteEntry> All { get; } =
        SpriteCache.Entries().Select(e => new SpriteEntry(e.Id, e.Category, e.Title)).ToList();

    /// The sprites of one category, in the catalog's order.
    public static IReadOnlyList<SpriteEntry> In(string category) => All.Where(e => e.Category == category).ToList();
}
