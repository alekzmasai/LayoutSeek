# LayoutSeek 0.3.4 preview

[Русский](RELEASE_NOTES.md)

LayoutSeek 0.3.4 is a portable preview for Windows 11 24H2, x64. The interface is in Russian.

This release adds optional startup for the portable app. The checkbox creates a shortcut in the current user's Windows Startup folder. Startup mode opens in the tray and enables correction after the app list loads. Windows can separately disable the shortcut in Startup settings.

Fixed ignored startup-checkbox clicks when returning to the app window.

The Search engine is unchanged from 0.3.3. It corrects keyboard-layout mistakes in native taskbar and Start search, using installed app names and physical-key mappings.

Extract the portable ZIP and keep its four runtime files together. Run `SearchLayout.exe`, then click «Включить». No installer or administrator privileges are required. The binaries are unsigned; SmartScreen may show a warning.

This remains a preview. Russian ↔ English US has native test coverage; other layout pairs have synthetic coverage only. Actual Windows sign-in is untested, and an early missing-first-character case remains unexplained. See the [README](https://github.com/alekzmasai/LayoutSeek/blob/main/README.en.md) for limits, startup removal and build instructions.
