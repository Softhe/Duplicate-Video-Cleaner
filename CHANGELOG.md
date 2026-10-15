# Changelog

## 1.1.1 — 2026-10-15

- Serialized settings writes so concurrent saves retain a readable preferences file.
- Recomputed group keepers and suggestions after removal.

- Stopped removal when scanned file metadata or Windows file identity changes, snapshots are missing, or files are reparse points.
- Recorded audit intent and outcome per file, stopped batches on audit failure, and preserved accurate completed-removal results.
- Bound cleanup logs and dry-run reports to the original scan folder and rules.
- Reported skipped scan entries, discarded canceled scans, and stopped canceled metadata probes.
- Prevented repeated dry-run exports from overwriting earlier reports and surfaced invalid export destinations.

- Added a per-user Windows EXE installer with Start menu integration and uninstall support, plus a portable ZIP from the same verified payload.
- Included screenshot documentation in both packages and published download checksums.
- Pinned the FFmpeg dependency to a dated release and expected SHA256; corrupted downloads and cache entries fail before extraction.

- Reworked every workspace and the removal confirmation dialog with a dark navy palette, mint accents, brighter text, and larger Segoe UI typography.
- Added compact review summaries, selection markers, consistent action styling, and dark dropdowns.
- Corrected theme application to preserve semantic status colors and selected file backgrounds.
- Added UI checks for summary density, multiple groups, action bounds, and expansion state, with optional screenshot evidence.
- Fixed the main window and the confirmation dialog showing the default icon on the title bar and taskbar instead of the application icon.

## 1.1.0 — 2026-08-02

- Added persistent scan and window preferences.
- Added FFprobe duration/resolution metadata and advisory confidence.
- Added accessible metadata columns and exact size comparison.
- Added CSV and text dry-run exports.
- Added automated service, deletion-integration, and UIA3 workflow tests.
- Added a reproducible icon and documented portable release package.
- Replaced the wizard/table interface with a dark sidebar workspace, expandable duplicate-group cards, a persistent safety dock, and current-session activity view.

## 1.0.0

- Initial guided dark-mode C# replacement for `DuplicateVideoClear.ps1`.
