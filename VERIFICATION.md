# Local verification record

## Version 0.1.1 — release candidate verification

- Version defaults are synchronized to **0.1.1** across MSBuild, build/package/release scripts, Inno Setup and the main/manual CI path. The new release tag is `v0.1.1`; existing published tags must not be moved.
- Full `scripts/Build.ps1 -Configuration Release -Version 0.1.1 -ArtifactsRoot artifacts/validation-v0.1.1` passed: Core **83/83**, Host/Control **93/93**, Discovery **35/35**, App/update fixtures, Editor **9/9** and lifecycle checks. Host and WinUI builds reported **0 warnings / 0 errors**. The latest TRX files were parsed and all report zero failures.
- Fixed the combined-protocol listen-conflict test fixture to reserve a TCP-compatible UDP port with bounded retries; concurrent tests can occupy the UDP side of a TCP candidate on Windows. Targeted and repeated Host runs passed before the complete build.
- Python packaging/icon/tray contract suite: **18/18** passed. `scripts/Release.ps1 -TagName v0.1.1 -OutputRoot artifacts/release-v0.1.1` generated all three assets; both manifest hashes, portable ZIP CRC and flat-root App/Host entries were independently checked. Published App and Host file versions are **0.1.1.0**.
- Against this exact candidate portable payload, UIA layout checks passed at default/minimum sizes, with checkbox-label center differences of **0.5 px**, no main-page update command, and real TCP/UDP forwarding preserved when the window closes.
- The full native tray acceptance did **not** complete on two candidate runs: the existing UIA Invoke and physical-click fallback timed out selecting Open while the native popup remained visible. The earlier eight-check successful run below applies to the previous local payload; it is not a new full-tray certification for this candidate. The failing runs cleaned up only their isolated App/Host instances.
- No real installation, installed-application replacement, Windows display-language change, service/firewall changes, or WSL/container/VM startup was performed. Remote push, Actions and Release publication must be verified independently after publication.

## Batch checkbox alignment and tray-only updates — current published payload

- Batch-select and select-all labels explicitly center vertically with native checkbox glyphs; the main-page update button is removed. The native tray menu now wires Check Updates to the guarded update handler and restores a hidden window before showing results.
- Python regression suite: **18/18** passed. App/update fixture assertions and lifecycle harness passed; `git diff --check` passed.
- Regenerated `artifacts/LanBridge-win-x64` and ran `tests/LanBridge.Tray.Acceptance` against its App/Host with isolated scratch data. All **8** acceptance checks passed, including the nested UI layout smoke. Batch/select-all label and control centers differ by **0.5 px** at default and minimum window sizes; toolbar/columns remain aligned and the main page exposes no Check Updates command.
- The real native tray Check Updates item restored the hidden window and displayed the real request result. This machine's GitHub request failed with `The SSL connection could not be established, see inner exception.` This verifies command routing and failure feedback, not a successful remote version check. No update was installed; TCP/UDP forwarding remained alive.
- Tray exit and restart were exercised only for the isolated test instance: exact UI/Host exit, tray removal, port release, saved configuration and restored TCP/UDP forwarding all passed. No service installation, firewall modification or WSL/container/VM operation was performed.
- These are local source/published-payload checks; no new GitHub Release or replacement of installed software is claimed.

## Bilingual installer — Windows UI language default

- Installer declares English and Simplified Chinese; uses `LanguageDetectionMethod=uilanguage`, `UsePreviousLanguage=no`, and `ShowLanguageDialog=yes`. English is the fallback; users can override the default. The application UI itself remains Chinese.
- Simplified Chinese messages are versioned under `scripts/Languages/ChineseSimplified.isl`, preserving the translation's attribution; CI does not depend on a developer-machine language-file path.
- `python -m unittest discover -s tests -p 'test_*.py' -v`: **14/14** packaging/icon tests passed, including the new language contract.
- Inno Setup **7.1.0** successfully compiled `scripts/LanBridge.iss` with both language files using the existing `artifacts/final-release/LanBridge-win-x64` payload. Output: `artifacts/bilingual-installer/LanBridge-Setup.exe`. This verifies the installer-script change, not a fresh application build or a new complete Release asset set.
- No real installation, application replacement, Windows display-language change, or installer-wizard UI smoke was performed. Existing GitHub Release assets were not replaced by this verification.

## Initial GitHub publication — local release verification

- Parent reran `scripts/Build.ps1 -Configuration Release -Version 0.1.0 -ArtifactsRoot artifacts/final-validation`: Core **83/83**, Host/Control **93/93**, Discovery **35/35**, App/update assertions, Editor **9/9**, and lifecycle harness passed. Host/WinUI builds: **0 warnings / 0 errors**.
- Parent ran Python packaging/icon tests: **13/13** passed. Additional release regressions cover the binary-upload host, draft asset verification before publication, installer macro version override, and invalid leading-zero tags.
- Real PowerShell updater fixtures passed for successful replacement/restart, injected copy-failure rollback, invalid/traversal/duplicate/config ZIP entries, size limits, corrupt/missing/locked packages, readiness ACK, timeout and uncommitted disposal. They use isolated temporary directories, not the user's application files. Installer execution is intentionally interactive and not claimed transactional.
- Parent ran `scripts/Release.ps1 -TagName v0.1.0 -OutputRoot artifacts/final-release`: generated installer, flat-root portable ZIP and SHA256SUMS successfully. This does not imply a remote Release or real installation/upgrade has occurred.
- Local repository initialized on `main`; `origin` is `git@github.com:wwbgo/LanBridge.git`. Initial discovery found no remote refs or Releases. Push/remote CI results must be read from GitHub after publication.
- Candidate source files were scanned for private-key blocks, GitHub token formats and quoted secret assignments; no matches were reported. Generated binaries, Python caches, `bin/`, `obj/`, `artifacts/`, local credential files and historical raw Core test transcripts are excluded from Git.
- Earlier verification sections remain historical; their PIDs and hashes are not current system-state assertions. The earlier listener-preflight backend changes are covered by backend tests; no newly completed GUI preflight acceptance is claimed by this release work.

## Compact rule-list layout — current source and published UI

- Removed the standing product subtitle, selection tutorial and footer caveats; contextual guidance remains in tooltips and accessibility help.
- Kept start/stop and edit on one toolbar; copy, TCP probe and confirmed deletion remain available under More. Batch select/count/actions appear only in batch mode.
- Added aligned rule/listener/target/status columns; retained protocol labels, active-connection counts and hover traffic totals without the standing zero-traffic line.
- App source assertions were observed failing before implementation. A real published UI smoke reproduced 29-pixel header drift in batch mode before the header-inset fix.
- Final `tests/UiLayoutSmoke.ps1 -AppProcessId <current PID>` passed all four named checks against the regenerated `artifacts/LanBridge-win-x64` payload: default single-select/quiet toolbar, batch controls/header alignment, minimum 760x600 toolbar/column layout, and restoration to single-select. It uses existing rules and never starts, edits or deletes them.
- The More flyout was opened and its copy/probe/delete entries were observed in the published UI; no destructive menu action was invoked. No fresh end-to-end forwarding or firewall acceptance is claimed for this layout-only change.
- Full Release build passed: Core 47/47, Host 77/77, Discovery 35/35, App assertions, Editor 8/8 and lifecycle harness. Host and WinUI builds reported 0 warnings / 0 errors.
- Standard release App/Host were regenerated and reopened. `LanBridge-Setup.exe` and the `LanBridge-single` payload were not regenerated and remain historical.

## Repository relocation — current verification

- Working directory is now `D:/github/LanBridge`; the previous source directory was removed after copy verification.
- All 4,196 migrated files were checked by relative path, size and SHA-256 before old-file removal; no differences or cleanup errors were found.
- Full `scripts/Build.ps1 -Configuration Release` passed from the new directory: Core 47/47, Host 77/77, Discovery 35/35, App assertions, Editor 8/8 and lifecycle checks. Host/WinUI builds reported 0 warnings and 0 errors.
- `scripts/Package.ps1 -Configuration Release` regenerated `artifacts/LanBridge-win-x64` from current source, including default single-selection, opt-in batch selection and removal of double-tap editing.
- App and Host were reopened from that new published directory. A live accessibility capture showed both existing rules, the batch-selection control and default-single-selection help. No rule was edited or started for this check.
- The migrated `LanBridge-Setup.exe` and `LanBridge-single` payload are older artifacts; they were preserved, not regenerated by this migration. Historical release/UI results below do not certify current artifacts.

## Current source changes — editor fix, checkbox batch selection and quiet connection feedback

These results apply to the latest working tree and the newly generated Release package. The live GUI was opened from `artifacts/LanBridge-single/app/LanBridge.App.exe` and verified against the current payload.

| Gate | Observed result |
|---|---|
| App console assertion harness | passed; includes no raw target connection errors, checkbox/select-all batch actions, selected-count/mixed-state reporting, hidden connected-status row, and partial-progress reporting |
| Editor console assertion harness | 8/8 passed; includes visible preselection of saved listen address/ports |
| WinUI Release build (`dotnet build src/LanBridge.App/LanBridge.App.csproj -c Release --no-restore`) | succeeded, 0 warnings, 0 errors |
| Published UI verification | Launched regenerated App (PID 23480) and Host (PID 20380) from `artifacts/LanBridge-single/app`; visually confirmed per-rule checkboxes, top-level select-all, selected-count display, and both rules initially unchecked |
| `scripts/Package-Single.ps1 -Configuration Release` | succeeded; generated current installer and self-contained payload |
| Batch checkbox selection in published UI | Visually verified the checkbox/select-all controls in the regenerated payload; full start/stop/delete execution was not manually exercised |
| Published installer SHA-256 | `18B413B56354C70A58CF30135A9D02642670658D9BD4E074A942A170ADEABFBC` |
| Published App EXE SHA-256 | `0F4464FB2E08243627AF11A4F3775DD5DEB0DDB4CFF42996FB3CC980D29F7D3A` |
| Core / Host / Discovery Release tests | 47/47, 77/77 and 35/35 passed; replaced the environment-dependent `localhost.localdomain` fixture with loopback-resolving `localhost` and re-ran the suite |
| Lifecycle harness | all 4 named checks passed |
| Full `scripts/Build.ps1 -Configuration Release` | succeeded; Host and WinUI Release builds reported 0 warnings and 0 errors |

## Prior packaged release acceptance — historical baseline


The test and acceptance results below were recorded against an older payload before the current source changes. They remain historical evidence only; the artifact paths have since been overwritten by the current package, and these release acceptance suites have not been rerun against it.

| Gate | Observed result |
|---|---|
| Core Release | 47 passed, 0 failed, 0 skipped |
| Host Release | 77 passed, 0 failed, 0 skipped; includes privileged-operation tests and commit-timeout reconciliation |
| Discovery Release | 35 passed, 0 failed, 0 skipped |
| App console assertion harness | 7 named groups passed |
| Editor options console harness | 8/8 passed |
| Icon asset validation | 5/5 passed; ICO frames 16–256 px and Release resource/output wiring verified |
| WinUI Release build (`-p:Platform=x64`) | succeeded, 0 warnings, 0 errors |
| `scripts/Package-Single.ps1 -Configuration Release` | installer and self-contained payload generated |
| Black-box Host acceptance against `artifacts/LanBridge-single/app/LanBridge.Host.exe` | 14 named checks passed |
| Published tray acceptance against `artifacts/LanBridge-single/app/LanBridge.App.exe` | Previously 6/6 passed; two fresh attempts reached hide/forwarding but native popup physical-click fallback timed out, so this gate is not currently re-certified |

### Actual release acceptance

The black-box suites use child processes, isolated scratch configurations, real named-pipe IPC and real loopback sockets. Host checks include 128 KiB TCP half-close, concurrent isolated UDP clients, one combined TCP/UDP rule forwarding both protocols, explicit UDP-unverified probe output, switching combined → TCP → combined, restart restoration of both protocols, disabling combined releases both ports, invalid-edit preservation and deletion persistence. Tray checks use the published WinUI executable and verify window hiding, native popup selection for open/exit, version-4 select restoration, graceful backend shutdown, TCP/UDP port release, and enabled-rule restoration.

The user authorized temporarily stopping the console Host. Both release acceptance suites used isolated scratch `--data-dir` locations; no test rules were saved to the normal user configuration.

The published WinUI smoke verifies automatic connection, absence of the old connect/start button, source selector left of Discover on the same row, editable/custom port and IP input, actual selection of `22 · SSH` and `127.0.0.1`, existence of TCP/UDP/TCP-UDP choices, and cancel without saving. UI Automation must write the ComboBox's descendant Edit ValuePattern: the parent ComboBox reports writable but throws on SetValue. Helper/source tests verify labeled-port numeric parsing, invalid-input rejection, address preservation and discovery-feedback policy.

### Deliverable and limitations

- Historical previous installer: 94,954,149 bytes (90.56 MiB); its SHA-256 (`73ddb0c4...`) and published assembly hashes below do not apply to the current payload.
- Current installer and App EXE hashes are recorded in the current source-change table above.
- Single-file installer distribution; installed WinUI application still requires DLLs/resources. Host itself is .NET single-file.
- Installer generation and payload were tested; installation/uninstallation was not exercised.
- No service installation, live firewall changes, or WSL/container/VM startup occurred. Privileged-operation tests use in-memory PowerShell cmdlet fakes; actual UAC consent and live firewall/Hyper-V mutation remain unverified.
- No fresh cross-device LAN/VM end-to-end success or pixel-perfect visual certification is claimed.
- The current published WinUI GUI is running from the newly generated payload (PID 23480; Host PID 20380); checkbox selection controls were visually verified, and the published tray acceptance gate remains historical.

Reproduction: use `dotnet test` for Core/Host/Discovery and `dotnet run --project` for App/Editor harnesses. Published Host acceptance takes one positional path:

```powershell
dotnet run --project tests/LanBridge.Acceptance/LanBridge.Acceptance.csproj -c Release -- artifacts/LanBridge-single/app/LanBridge.Host.exe
```

Acceptance refuses an existing user's backend; obtain permission before stopping it and restore normal operation afterward. UI refinement smoke requires an existing backend and creates no rule. `scripts/Test-Local.ps1` uses development-folder output and does not replace published-binary acceptance.

## Historical verification (not current release totals)

### Verified by parent against actual outputs

- `dotnet test tests/LanBridge.Core.Tests/LanBridge.Core.Tests.csproj -c Release --nologo -v minimal`: 39 passed, 0 failed, 0 skipped.
- `dotnet test tests/LanBridge.Host.Tests/LanBridge.Host.Tests.csproj -c Release --nologo -v minimal`: 24 passed, 0 failed, 0 skipped.
- `dotnet run --project tests/LanBridge.App.Tests/LanBridge.App.Tests.csproj -c Release`: 4 console assertion groups passed (endpoint formatting, statuses, host launch policy, discovery source/help labels).
- `dotnet build src/LanBridge.App/LanBridge.App.csproj -c Release -p:Platform=x64`: succeeded, 0 warnings, 0 errors.
- `scripts/Package.ps1`: actual self-contained x64 App/Host publish and assembly succeeded.
- Black-box `tests/LanBridge.Acceptance` against the published `artifacts/LanBridge-win-x64/LanBridge.Host.exe`: 8 checks passed. Starts actual child process, exercises real named-pipe calls and loopback sockets, 128KiB TCP including half-close, simultaneous isolated UDP clients, invalid edit preservation, restart restore, port release, delete persistence.
- `tests/UiSmoke.ps1`: actual published WinUI app launched, connected to backend, add-rule dialog opened; UI Automation exposed Hyper-V/WSL2/Docker/Podman source options; cancel returned to main screen. Closing tested GUI does not terminate backend. No rule was created by this UI smoke test.

## Not certified

- Discovery library: 29 fixture/process safety tests passed, covering WSL/Docker/Podman parsing, transport trust checks, output limits, cancellation, and process cleanup.
- Read-only local discovery run: WSL produced a visible missing-`hostname` warning and no invented address; Docker compatibility CLI produced a context warning and no invented mapping; Podman produced two published local TCP candidates (`127.0.0.1:55432`, `127.0.0.1:19092`) plus warnings for unpublished ports. The run did not start or reconfigure a distro, container, or machine.
- Real Windows service installation/uninstallation and real firewall modifications were not executed.
- No real VM, WSL distro, container or machine was started for tests; no cross-device LAN/firewall/hypervisor end-to-end success is claimed.
- UI Automation verifies structure and actions, not pixel-perfect rendering or accessibility conformance.

The source/CLI snapshot may change during final integration. Rerun `scripts/Test-Local.ps1` for current engine/backend evidence; run `tests/UiSmoke.ps1` only after packaging. The latter opens a real window and may launch a per-user backend. Run black-box acceptance with no existing LanBridge backend for the current user; it refuses to modify an existing backend.
