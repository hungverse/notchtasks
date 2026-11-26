# Fluent System Icons

The icon fonts the app draws its glyphs from. Source: https://github.com/microsoft/fluentui-system-icons (MIT, see LICENSE here).

- Files: `FluentSystemIcons-Regular.ttf`, `FluentSystemIcons-Filled.ttf`, and their name-to-codepoint maps `FluentSystemIcons-Regular.json` and `FluentSystemIcons-Filled.json`.
- Commit: `a3de63245b4fa49c30e20d9838593a105affe774` (the `main` head on 2026-11-26).
- Fetched 2026-11-26 from `raw.githubusercontent.com/microsoft/fluentui-system-icons/a3de63245b4fa49c30e20d9838593a105affe774/...`. All five files were re-fetched at that commit and match the bundled copies byte for byte (SHA-256).
- Glyph codes in the app are the codepoints the JSON names for each `ic_fluent_<name>_20_<style>` key. `Pill/ModuleGlyphs.cs` and `ModuleCatalog.Glyph` name the key beside each code.
