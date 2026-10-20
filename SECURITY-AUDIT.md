# Security and correctness audit

Historical first-pass report. See [FOLLOWUP-AUDIT.md](FOLLOWUP-AUDIT.md) for the implemented follow-ups and current release decision.

Date: 2026-10-16
Base commit: 2b920833aa4d553afdd3feca309426384194783c
Scope: current source in repository, including desktop workflow, scanning, metadata subprocesses, file removal, settings, report export, dependencies, installer configuration, and release workflow.
State: local source fixes; no commit, push, publication, or installed application replacement.

## Assessment

The baseline passed 24 core checks, but independent source review and native Computer Use verification found additional security and correctness gaps. Three sub-agents reviewed file operations, metadata/workflow correctness, and actual Windows UI/build configuration. Reviewers cross-checked the security-sensitive changes. The fixes below pass the expanded suites. This is a scoped source and behavior audit, not a guarantee that the application or its native dependencies contain no vulnerabilities.

## Findings and fixes

| Finding | Priority | Change and verification |
| --- | --- | --- |
| Permanent deletion validated a path, closed the validation handle, then deleted by path. A concurrent replacement could remove an unreviewed object. | High integrity risk when another process can mutate the source | Open with read/delete access and read sharing only, validate identity/size/times/attributes on that handle, then use Windows FileDispositionInfo on the same object. Active writers fail safely. Extended DOS/UNC paths preserve long-path behavior. Tests cover changed/replaced files, active writers, successful deletion, and paths beyond 300 characters. |
| ffprobe inherited broad protocol handling, captured unlimited stdout/stderr, and stopped timing after process exit rather than after pipe completion. | Medium security and availability risk | Explicit absolute -i input; file-only protocol whitelist; stdout limited to 1 MiB characters and stderr to 64 KiB characters; cancellation/timeout covers readers and process exit; failure cleanup kills process tree and bounds the exit wait. Real child-process fixtures check arguments, floods, timeout, cancellation, and process termination. |
| Metadata cache used path, size, and write time, allowing same-size replacements with preserved timestamps to reuse stale metadata. Cache could grow across repeated scans. | Medium correctness risk | Include Windows file identity and creation time; clear at 2,048 entries. Replacement regression preserves original timestamps and verifies key invalidation. |
| GitHub release workflow bypassed the hardened dependency helper, downloaded mutable latest FFmpeg without expected hash comparison, and hardcoded an outdated portable version. | High release integrity risk | Reuse BuildRelease.ps1 and its pinned hash verification; validate tag against project version; separate read-only build and write-authorized publication jobs; manual runs produce artifacts without publishing; publish setup EXE and checksum only. Embedded PowerShell blocks parse. Hosted execution remains unverified. |
| Long scan context expanded Review beyond the default window and hid action buttons. | Medium usability and correctness risk | Percent-width columns and wrapping keep context, actions, and metadata within the window. New screen-bounds test passes after the fix and fails against an isolated original-layout copy. Computer Use confirms visible controls at approximately 1146 x 733. |
| Duration formatting wrapped hours after 24 hours. | Low correctness risk | Display total hours. A 25-hour duration now displays 25:00:00.123 rather than 01:00:00.123. |
| Generic CSV escaping did not neutralize formula prefixes. Current exported paths are absolute, reducing direct reachability of this issue. | Defense in depth | Neutralize formula and whitespace prefixes before CSV quoting. Regression covers formula strings, tabs, newlines, and ordinary paths. |

## Verification

- Baseline Release core suite: 24/24 passed.
- Final Release core suite: 29/29 passed.
- Final Release UI suite: 11/11 passed. Evidence: DuplicateVideoCleaner.UiTests/TestResults/audit-ui.trx.
- Separate real-video check: MetadataSuccessReachesReviewConfidenceAndExport passed with the pinned packaged ffprobe and a generated one-second 64x48 MPEG-4 video. Metadata availability, high advisory confidence for two copies, and CSV export passed. Evidence: DuplicateVideoCleaner.UiTests/TestResults/audit-real-media.trx.
- Original MetadataProbe source in an isolated copy failed the new safe-arguments fixture; working source passed.
- Original UI layout in an isolated copy failed LongScanContextKeepsReviewActionsInsideTheWindow because Rescan videos appeared outside the window; working source passed.
- Computer Use exercised the actual WinForms app with isolated settings and generated fixtures. Scan completion, low-confidence metadata fallback, oldest keeper suggestion, Recycle Bin default, selection clearing, disabled removal without selections, and Activity state were confirmed. Generated files remained intact.
- Screenshots: validation/computer-use-review-fixed.jpg and validation/computer-use-details-fixed.jpg.
- Test-BuildScripts.ps1 -VerifyDependency passed parsing, installer configuration checks, corrupted-cache rejection, and repeated pinned-cache verification.
- NuGet vulnerability query for all solution projects, including transitive packages, reported no known vulnerable packages from nuget.org on the audit date. This does not audit the bundled native FFmpeg binary.
- git diff --check passed.

## Reviewed controls retained

Settings use validated defaults, bounded numeric fields, atomic staged replacement, unique temporary names, and a path-specific mutex. Metadata uses ArgumentList without a shell. Scans skip reparse points and report inaccessible/disappearing entries. Removal logs intent before mutation, stops on logging failure, and preserves completed outcomes. Completed scan context controls export and logging. Confirmation defaults to cancellation. Installer configuration requests per-user privileges. These controls were inspected and exercised where covered by the suites.

## Remaining limits and follow-up

1. Recycle Bin deletion still uses a path-based Windows shell API after snapshot validation. Concurrent filesystem mutation can race that call. Permanent deletion now has stronger object-bound validation. Actual Recycle Bin removal was not exercised in this audit.
2. ffprobe remains a native parser running under the user's account. The file whitelist blocks HTTP/TCP protocol handlers but permits referenced local files and may access UNC shares. It is not a sandbox or a complete network prohibition. Native parser fuzzing and a comprehensive FFmpeg vulnerability assessment were outside this run.
3. .NET 9 reaches end of support on November 10, 2026. Plan migration to a supported runtime before then. Source: https://dotnet.microsoft.com/en-us/platform/support/policy and https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/ . Runtime migration was not mixed into these targeted security fixes.
4. GitHub workflow YAML and hosted packaging/publication were not executed. Embedded PowerShell parsed and the build helper dependency checks passed locally. GitHub's Windows runner inventory currently lists Inno Setup: https://raw.githubusercontent.com/actions/runner-images/main/images/windows/Windows2025-Readme.md . Missing compiler paths fail closed.
5. Existing installer/release artifacts predate these changes. No installer was rebuilt or installed in this audit. No release-signing claim is made.
6. Timestamp grouping and metadata confidence do not prove identical video content. Review files before removal. Very large result sets still create review controls on the UI thread; no large-library performance qualification was performed.

## Reproduce current checks

```powershell
dotnet run --project DuplicateVideoCleaner.Tests -c Release
dotnet test DuplicateVideoCleaner.UiTests -c Release
./build/Test-BuildScripts.ps1 -VerifyDependency
dotnet list DuplicateVideoCleaner.slnx package --vulnerable --include-transitive --source https://api.nuget.org/v3/index.json
git diff --check
```
