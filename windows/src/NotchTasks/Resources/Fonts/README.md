# Fluent System Icons

The icon fonts the app draws its glyphs from. Source: https://github.com/microsoft/fluentui-system-icons (MIT, see LICENSE here).

- Files: `FluentSystemIcons-Regular.ttf`, `FluentSystemIcons-Filled.ttf`, and their name-to-codepoint maps `FluentSystemIcons-Regular.json` and `FluentSystemIcons-Filled.json`.
- Fetched 2026-11-26 from the default branch (`main`) through `raw.githubusercontent.com/microsoft/fluentui-system-icons/main/...`.
- Commit SHA: not recorded. The download used raw file URLs only; the commit that `main` pointed to was not looked up.
- Glyph codes in the app are the codepoints the JSON names for each `ic_fluent_<name>_20_<style>` key. `Pill/ModuleGlyphs.cs` and `ModuleCatalog.Glyph` name the key beside each code.
