# Follow-up implementation and release decision

Historical 2026-10-16 evidence. See [PR-READINESS.md](PR-READINESS.md) for the 2026-10-20 implementation, larger improvements and current validation scope. Artifact hashes and counts below describe the earlier candidate.

Date: 2026-10-16
Candidate: 1.1.2, Windows x64, .NET 10 LTS
Base commit: 2b920833aa4d553afdd3feca309426384194783c
Scope: resolves the actionable first-pass findings and implements the remaining removal, isolation, performance, runtime, packaging, and qualification work. This report supersedes the first-pass remaining-work list in SECURITY-AUDIT.md.
State: local source and setup candidate. No commit, push, public release, or replacement of an existing user installation.

## Decision

Proceed with controlled candidate testing. Hold public release until the exact setup EXE passes disposable-Windows-VM acceptance. Local source, behavior, sandbox, performance, fresh-install, upgrade, and uninstall checks passed. The current host is physical ASUS hardware with no hypervisor, so its installer acceptance cannot satisfy the VM gate. The setup is unsigned. Code signing requires an appropriate publisher certificate and was not fabricated with a self-signed substitute.

## Implemented changes

### Safe recoverable removal

The path-based Windows Recycle Bin API was replaced with explicit app Recovery, rather than claiming atomic validation for a path-based shell operation. Recovery is the default mode. The selected file is opened and validated on one Windows handle, its source and destination ancestor directories are pinned against replacement, and the same object is renamed into a same-volume .DuplicateVideoCleaner-Recovery directory. A flushed manifest records original location, identity, size, and times before the move. Reparse ancestors and changed files fail closed.

Activity > Recovery lists entries, restores without overwriting existing files, and offers separately confirmed permanent purge. Restore and purge validate the stored snapshot; journal failures before mutation preserve files, while completion audit failures return an accurate warning. Corrupt manifests and inaccessible folders are isolated with diagnostics so valid entries remain available. Recovery folders are excluded from scans. Scan/removal/action audit logs reject symlink and hard-link redirection and escape CR/LF injection.

Recovery retains storage until explicit permanent purge. Moving the source folder externally invalidates its absolute bound recovery locations; payloads remain preserved for manual recovery. Uninstall preserves recovery media. Network filesystems and physical power-loss durability were not qualified; tests use generated local Windows filesystem fixtures.

Primary API references: https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info and https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew .

### Metadata isolation

ffprobe runs in a zero-capability Windows AppContainer at low integrity, inside a verified non-breakaway job limited to one process, 512 MiB committed memory, 20 seconds of CPU, and kill-on-close. The process is created suspended, assigned to the job, and its token, AppContainer SID, capability count, integrity level, and job membership are verified before execution. Only explicit pipe handles are inherited. A clean environment omits caller credentials and runtime hooks. Native error dialogs are suppressed during child startup. Failure to initialize isolation does not launch an unrestricted process.

The broker stages trusted tools and a private read-only copy of each selected input. Original media ACLs are unchanged. Tool staging runs away from the UI thread and checks cancellation. JSON staging is limited to the executable's own runtimeconfig/deps files used by the managed test fixture; unrelated JSON is excluded. Source snapshots come from read-only handles and are revalidated with parent directories pinned before copying, closing the cache/staging identity race found during independent review. Captured output, process/pipe timeout, bounded cache, and cancellation remain enforced. Profile-deletion failures are reported.

Live adversarial tests verified selected-copy reads, denied private original/sibling reads and writes, denied staged-media writes, absence of a sentinel secret environment variable, no connection reaching a live TCP listener, and denied child creation. Flood, timeout, cancellation, process cleanup, identity replacement, and unrelated-JSON regressions passed. These checks demonstrate specific boundaries, not immunity to all native parser or OS vulnerabilities.

AppContainer can access OS resources explicitly available to AppContainers and its own temporary profile. Full media staging adds I/O and temporary disk usage; at most two inputs are processed concurrently, and the parser timeout begins after staging. Normal completion and cancellation remove staging/profile resources. Abrupt process termination can leave temporary artifacts. Initial failing-test artifacts were cleaned; no dvc-sandbox directories remained at the final inspection.

Primary references: https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer , https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation , and https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information . FFmpeg's primary security list was reviewed at https://ffmpeg.org/security.html ; the native binary was not represented as covered by NuGet's vulnerability query.

### Runtime and large libraries

The application, test projects, screenshot tool, fixture paths, and workflow now target .NET 10. Version is 1.1.2. Microsoft lists .NET 10 as LTS: https://dotnet.microsoft.com/en-us/platform/support/policy .

Review creates 25 group cards per page and 25 detail rows per expanded file page. Selection, export, and removal reconciliation cover all pages. Activity retains the latest 200 controls; diagnostics remain in the cleanup log. Focus order matches visible groups. Duration and group spans preserve hours beyond 24. Report wording uses selected/removal size because Recovery does not immediately reclaim disk space.

Measured real WinForms behavior with synthetic in-memory records:

- 10,000 results in 5,000 groups: 1,669 ms initial review and 562 controls.
- One 10,000-file group: 766 ms expansion with 25 visible detail rows.
- Tests verify page transitions, global selections, off-page export, removal reconciliation, and continued message processing.

These measurements exclude disk enumeration and real-media throughput.

### Release and acceptance

BuildRelease generates only a setup EXE and checksum as deliverables. It bundles .NET and the pinned FFmpeg dependency, executes core/UI tests and release-guard regressions, and includes refreshed screenshots. No portable ZIP was generated for 1.1.2.

GitHub Actions are pinned to resolved commit IDs. The build has read-only repository permission, validates tag/version consistency, and uses pinned FFmpeg hash verification. The publication job alone has write permission. Before staging public assets, the workflow verifies the prior 1.1.1 installer hash and requires candidate acceptance in its hosted Windows VM, including upgrade, settings preservation, installed UI, payload hashes, and uninstall. Assert-ReleaseAcceptance checks that evidence matches the exact setup SHA256. Manual workflow runs do not publish a release.

Workflow YAML and all embedded PowerShell blocks parsed locally. Build script parsing, setup requirements, dependency verification, corrupted-cache rejection, and negative acceptance cases passed. Guard tests reject physical-host evidence, missing upgrade or UI checks, and changed setup bytes. Hosted workflow execution remains unperformed because these changes were not pushed.

## Final verification evidence

- Release core/integration suite: 41/41 passed, including live AppContainer adversarial execution and recovery/removal fault cases.
- Complete UI suite: 16/16 passed on .NET 10, including Recovery restoration, large-library bounds, scan cancellation/close, confirmation, and source-context behavior.
- Real generated one-second 64x48 MP4: packaged ffprobe inside isolation returned valid metadata, High advisory confidence for two copies, and successful CSV export.
- Computer Use exercised the final payload's Recovery listing and restoration. Saved screenshots: validation/computer-use-recovery-1.1.2.jpg and validation/computer-use-recovery-restored-1.1.2.jpg. Original paths and both fixture contents were checked afterward; files were intact. Launched test applications were closed.
- Six README screenshots were regenerated from the final application, including the Recovery confirmation and storage explanation.
- Exact setup fresh-install acceptance passed: validation/local-fresh-setup-acceptance.json. Installed UI 6/6, payload hashes, bundled ffprobe WAV, preferences, launch, and uninstall cleanup passed.
- Exact setup upgrade acceptance from 1.1.1 passed: validation/local-upgrade-setup-acceptance.json. Installed UI 6/6, settings preservation, payload hashes, bundled ffprobe, launch, and uninstall cleanup passed.
- NuGet query for all projects and transitive packages reported no known vulnerable packages from nuget.org on the audit date.
- git diff --check passed.

Independent review found and resolved the metadata copy/cache race, excessive JSON staging, and unchecked profile cleanup before finalization. No further actionable blocker was identified by those source reviews. The screenshot's Unknown Hard Error occurred during sandbox fixture/child-process testing; startup error dialogs were suppressed and lifecycle tests were changed to observe broker-owned process IDs instead of expecting forbidden writes beside the media. Final suites passed with isolation enforced.

## Exact candidate

Path: release/DuplicateVideoCleaner-1.1.2-win-x64-setup.exe
Size: 85,811,496 bytes
SHA256: 42EA5E6E0309A4677A63CDCEE17E5CCA0B23E3FDD46E2EA3173839528EAF03C7
Authenticode: NotSigned

The local acceptance records both match this hash and correctly record virtualMachine=false. Their success must not be described as VM qualification.

## Remaining boundaries

- Public release remains blocked by unperformed disposable-VM acceptance of this exact artifact. A different setup hash requires fresh evidence.
- The setup is unsigned; publisher signing needs the appropriate certificate.
- Timestamp grouping and metadata confidence remain advisory and intentionally do not establish identical video content. Review selections before removal.
- Recovery, staging I/O, AppContainer OS-access rules, abrupt-termination cleanup, network filesystem, and power-loss limits are stated above. No claim of exhaustive native fuzzing or universal security is made.

## Qualification command

Run in a disposable Windows VM with the repository and both setup EXEs available:

```powershell
./build/Test-InstalledSetup.ps1 -SetupPath ./release/DuplicateVideoCleaner-1.1.2-win-x64-setup.exe -PreviousSetupPath C:/path/to/DuplicateVideoCleaner-1.1.1-win-x64-setup.exe -RequireVirtualMachine -RunUiTests -EvidencePath ./release/installer-acceptance.json
./build/Assert-ReleaseAcceptance.ps1 -SetupPath ./release/DuplicateVideoCleaner-1.1.2-win-x64-setup.exe -EvidencePath ./release/installer-acceptance.json
```
