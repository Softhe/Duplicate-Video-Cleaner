# UI redesign — build and implementation plan

Target: the "Native Calm" design (concept A, v3) in two themes. Dark is the default (`docs/redesign/mockups/dark/`), and light is the alternative (`docs/redesign/mockups/light/`).
Goal: replace the whole visual layer. Scanner, metadata probe, sandbox, removal, Recovery and settings behaviour stay unchanged.

## 1. Decision: WPF on .NET 10 with the built-in Fluent theme

| | WPF + Fluent theme (recommended) | WinUI 3 (Windows App SDK) | Custom-drawn WinForms |
| --- | --- | --- | --- |
| Look | Windows 11 Fluent styles for standard controls, light and dark, accent colour | Exact Windows 11 controls (NavigationView, ContentDialog, InfoBar) | Every control painted by hand |
| Packaging | Unchanged: self-contained single-file EXE, portable ZIP, Inno Setup | Single-file publish is not supported; adds the Windows App SDK runtime and a larger package | Unchanged |
| New dependencies | None (part of .NET) | Windows App SDK NuGet + native runtime | None |
| UI tests (FlaUI/UIA) | Kept: same accessible names via `AutomationProperties.Name` | Kept, but more UIA differences | Kept |
| Effort | Medium: new XAML views, reuse all logic | High: new framework, unpackaged-app quirks (pickers, drag and drop) | High, and the result stays fragile |

WPF keeps the release pipeline (`build/BuildRelease.ps1` publishes `PublishSingleFile=true`) and adds no third-party code, which matters given the security audit. The few Windows 11 controls WPF lacks (navigation pane, toggle switch, content dialog) are small styled controls built in the project. **As built:** the app does not use the experimental `ThemeMode` API. Every control it uses has its own template in `Themes/Controls.xaml`, coloured from `Themes/Dark.xaml` or `Themes/Light.xaml`, so the look is exact in both themes and does not depend on an experimental API. The window keeps the native title bar, coloured through DWM, so snap layouts and system menus keep working.

## 2. Style rules (v3)

1. One focal point and one accent-filled button per screen. Accent appears in at most three places: the primary button, the navigation indicator and selection checkboxes.
2. Show exceptions, not confirmations. Matching metadata is plain text; only differences get a caution tone.
3. No cards inside cards. Use spacing and a single divider.
4. One chip style and one tag style (KEEP / REMOVE / PROTECTED as coloured text labels).
5. The same expanded, labelled navigation on every screen.
6. Colour cues carry meaning, not decoration:
   - Confidence pills: High green, Medium yellow, Low pink, No removal suggested neutral.
   - Filled KEEP / REMOVE / PROTECTED badges.
   - ✓ for attributes that match the keeper, ≠ for ones that differ.
   - A thin role-coloured edge on file rows.
   - Coloured activity icons only for Restore and Permanently delete.

### Themes

Two themes, with the same layout, components and colour-cue system. Only the colour resources differ.

- **Dark (default):**
  - Base `#141414` (title bar and navigation), content `#1A1A1A`.
  - Cards `rgba(255,255,255,0.04)`, strokes `rgba(255,255,255,0.06)`.
  - Text is white at 100 / 79 / 54 %.
  - Accent is the lighter indigo (`#9EA3F5` fill, black text on it).
  - Success `#6CCB5F`, caution `#FCE100`, critical `#FF99A4`.
- **Light (WinUI light tokens):**
  - Base `#F3F3F3`, content `#F9F9F9`.
  - Text is black at 90 / 61 / 45 %.
  - Accent is the darker indigo (`#3F44B0`, white text).
  - Success `#0F7B0F`, caution `#9D5D00`, critical `#C42B1C`.

The full token tables for both themes are in `docs/redesign/mockups/NOTES.md`.

**Theme setting.** A new `Theme` value in `AppSettings`, one of `Dark`, `Light` or `System`.
- **Default:** `Dark`, also for users upgrading from 1.1.x, whose settings file has no value yet.
- **Where to change it:** an About / Settings flyout opened from the navigation pane's footer.
- **`System`:** follows the Windows app mode and switches live when Windows changes it.
- **Switching:** swaps the merged colour dictionary and updates the title bar with `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`, with no restart.
- **High contrast:** Windows high-contrast mode overrides both themes.

### Where the build deliberately differs from the mockups

The mockups are a visual reference, not a feature spec. Where they show something the app does not do, the build follows the app:

- **Recovery location:** Recovery folders sit beside each source file's folder, so there can be several. The Activity header lists the scanned root, not a single Recovery path, and the confirmation dialog says "beside each file's folder" when the selection spans folders.

## 3. Architecture changes

Today `MainForm.cs` (840 lines) mixes layout with workflow logic (scan, selection, removal, export, settings, activity). Before any new view is written, that logic moves into a UI-independent layer so both the old and the new UI can run against it during the transition.

```
DuplicateVideoCleaner.Core      (class library, net10.0-windows, no UI)
  DuplicateScanner, MetadataProbe, WindowsProbeSandbox, FileRemovalService,
  RecoveryStore, RecoveryHistory, ReviewSession, DryRunExporter, SettingsService, ...
  Workspace/ScanWorkflow.cs       scan + cancellation + diagnostics (from MainForm.ScanAsync)
  Workspace/ReviewWorkflow.cs     selection, Suggested/All/None, removal, reconcile, export
  Workspace/ActivityLog.cs        bounded activity entries (latest 200)

DuplicateVideoCleaner           (WPF app)
  App.xaml, Theme/Dark.xaml, Theme/Light.xaml, Theme/Controls.xaml
  Shell/MainWindow.xaml         title bar, navigation pane, page host, dark title bar via DWM
  Pages/ScanPage, ReviewPage, ActivityPage   (+ view models)
  Dialogs/ConfirmRemovalDialog  in-window content dialog over a smoke layer, Cancel default
  Controls/NavigationPane, ToggleSwitch style, RoleLabel, GroupListItem, FileComparisonCard
```

View models are plain `INotifyPropertyChanged` classes (no MVVM framework dependency). Long lists (group list, Recovery table) use virtualising `ListView`s, which also removes the current paging (`Previous groups` / `Next groups`).

## 4. Phases

Each phase ends green: `DuplicateVideoCleaner.Tests` and `DuplicateVideoCleaner.UiTests` pass, and the app is shippable.

**Phase 0: Prep (no visible change)**
- Copy the mockups and notes into `docs/redesign/` (done with this plan).
- Write down the list of UIA names the UI tests and `build/ReadmeScreenshots` rely on (32 distinct names, for example `Scan videos`, `Expand group 1`, `Scan workspace`). These are the contract the new UI must keep, or the tests are updated deliberately in the same change.

**Phase 1: Extract the core**
- Create `DuplicateVideoCleaner.Core` and move every non-UI file into it. Unit tests point at Core.
- Move the workflow code out of `MainForm` into `ScanWorkflow` / `ReviewWorkflow` / `ActivityLog`. `MainForm` becomes a thin WinForms view over them.
- Add unit tests for the extracted workflows (selection rules, protected clips, removal reconciliation, busy and cancel states). This is where regressions like the 1.1.2 ones would otherwise slip in.

**Phase 2: WPF shell and theme**
- Switch the app project to `UseWPF`.
- Add `Theme/Dark.xaml` and `Theme/Light.xaml` for the colour resources, plus a shared `Theme/Controls.xaml` that refers to colours only by key.
- Add the `Theme` setting (default `Dark`) and live switching from section 2.
- Main window: title bar that follows the active theme (`DwmSetWindowAttribute` immersive dark mode), navigation pane, page host, window-position restore (reuse `HasReachableTitleBar`), single-instance behaviour and startup maintenance warnings as today.
- The WPF UI replaces the WinForms UI outright in 1.2.0, with no fallback. During development the WinForms views stay on the branch until Phase 3 reaches parity, then are deleted (`MainForm`, `WorkspaceControls`, `AppTheme`, `RecoveryDialog`, `DeletionConfirmationDialog`, `AccessibleGridCells`). `UseWindowsForms` is removed, and nothing WinForms ships.

**Phase 3: Pages**
- Scan: drop zone plus path box with a recent-folders dropdown, time window, Oldest/Newest, the two toggles, scan progress with Cancel, and the "nothing is deleted" line.
- Review: header with summary and Select control, Rescan with a "…" menu (Clear results), a virtualised group list, a comparison detail pane with ✓/≠ marks against the keeper, and a bottom bar with removal method, Export dry run… and Move selected….
- Confirmation dialog: summary, the "every original in a time window is selected" warning, Cancel focused by default, Enter/Esc cancel.
- Activity & Recovery: merge the Recovery dialog into the page (Select all, Restore selected as primary, Permanently delete selected as destructive) plus the activity log and Open cleanup log.
- Both themes: check every page and dialog in Dark and Light, and run the UI tests in both.
- Accessibility: keyboard order, visible focus, UIA names per Phase 0, high-contrast fallback (the Fluent theme switches when Windows high contrast is on; verify it).

**Phase 4: Thumbnails (enabled by default in 1.2.0)**
- **Decoding:** run `ffmpeg` (one frame, scaled to thumbnail size, written to stdout) inside the existing `WindowsProbeSandbox` AppContainer with the same read-only handle brokering as ffprobe. Never decode in-process or through the Windows shell thumbnailer, because media parsing is the attack surface the sandbox exists for.
- **Validation:** the app reads only the expected image bytes from the sandbox. It enforces a size limit and a per-file timeout, and decodes the result with WPF's image decoder only after both checks pass.
- **Bundling:** release builds already ship the pinned LGPL shared build's DLLs (`build/BuildRelease.ps1` copies `bin\*.dll` beside `ffprobe.exe`). Adding `ffmpeg.exe` is therefore a small increase. Update `build/BuildRelease.ps1` (copy it and verify its version and LGPL configuration like ffprobe), `FFMPEG-NOTICE.md`, the portable and installed package tests, and the release acceptance checks.
- **Scheduling:** thumbnails load lazily for visible rows only, at low priority after metadata, and are cancelled when the user scrolls away or rescans. Scanning never waits on them.
- **Cache:** a bounded thumbnail cache in the user profile (`%LOCALAPPDATA%`), keyed by path plus the file snapshot (size, last-write time, file ID) so a changed file is never shown with a stale frame. Clear it on uninstall.
- **Fallback:** a neutral placeholder with the duration when ffmpeg is missing, times out or the file can't be decoded. Thumbnails never block review or removal.
- **Tests:** sandbox isolation for the ffmpeg run (same tests as ffprobe), a corrupt or hostile file producing the placeholder, cache invalidation when a file changes, and performance with `LargeLibraryTests`.

**Phase 5: Release**
- Regenerate README screenshots with `build/ReadmeScreenshots` and update the README text for the new layout.
- Run the full release acceptance (`build/Test-ReleaseAcceptance.ps1`, installed setup and portable package tests) and check the package size against 1.1.3.
- Version 1.2.0, with a CHANGELOG entry.

## 5. Risks

- **Regressions in safety behaviour.** The removal and protection rules are the product. Mitigation: Phase 1 moves logic without changing it and adds tests before any XAML exists.
- **UI test churn.** FlaUI finds controls by name, not by framework, so most tests survive if names are kept. Where WPF exposes a different control type (for example a list item instead of a panel), update those tests in the same commit as the page.
- **Fluent theme API changes.** Avoided: the app uses its own control templates instead of the experimental `ThemeMode`.
- **Performance with large libraries.** `LargeLibraryTests` and `docs/PERFORMANCE.md` set the bar. Virtualisation should improve on today's paging; measure it with `build/Measure-Performance.ps1`.

- **Thumbnail decoding of untrusted media.** It runs only inside the AppContainer sandbox with strict output limits. Include it in the security audit for 1.2.0.

## 6. Decisions

- **Look:** concept A v3, in dark (default) and light, with a Dark / Light / System setting.
- **Framework:** WPF on .NET 10 with the built-in Fluent theme.
- **Thumbnails:** enabled and bundled in 1.2.0 (sandboxed ffmpeg).
- **Old interface:** replaced outright in 1.2.0, with no WinForms fallback.

## 7. Status

All five phases are implemented on this branch:

1. Core extraction with workflow tests.
2. WPF shell, dark and light themes, theme setting.
3. Scan, Review, confirmation dialog, Activity & Recovery.
4. Sandboxed thumbnails, cache, packaging of `ffmpeg.exe`.
5. Version 1.2.0, changelog, README and screenshots, release qualification against the published 1.1.3 setup.

The mockups are the visual reference. Deliberate differences in the build:

- Keepers keep a **Remove this file** checkbox, because Select All and manual overrides can select them.
- Recovery has no duration column (Recovery records do not store metadata).
- Thumbnails are 320×180 frames cropped to 16:9.
