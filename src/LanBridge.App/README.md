# LanBridge.App

Real WinUI 3 desktop UI (C# / .NET 10), unpackaged x64, Windows App SDK 2.6.4. Uses the system light/dark theme, native controls and Mica. No browser or WPF.

## Build

Run with Visual Studio's native MSBuild (the WinUI XAML toolchain requires full MSBuild):

```powershell
& 'D:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe' src/LanBridge.App/LanBridge.App.csproj -restore -p:Platform=x64 -p:Configuration=Release -verbosity:minimal -nologo
```

Output: `bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/LanBridge.App.exe` under this project. The Windows App SDK runtime is bundled; .NET 10 desktop-compatible runtime must be installed (or publish with `-p:SelfContained=true`).

Distribution must put the published **LanBridge.Host.exe and its dependencies alongside LanBridge.App.exe**. The UI deliberately does not search arbitrary PATH locations, request elevation, install a service, or change firewall rules. At startup it connects to the current user's pipe; when absent, it starts this sibling executable with `--console`, hidden and `UseShellExecute=false`. The `连接 / 启动后台` action retries explicitly. Closing the window cancels UI requests/timers but never terminates the backend.

## Behavior

- No seed/sample rules. List comes exclusively from backend state.
- Add/edit share one editor with all rule fields, explicit manual/Hyper-V/WSL2/Docker/Podman sources, manual-address fallback and target TCP probe. WSL discovery only inspects running distributions; container discovery uses Windows-reachable published ports and never publishes ports. Saves are read back before the dialog reports success.
- Select a row to edit, start/stop, delete with confirmation, copy an address, or probe TCP. UDP never presents a TCP probe as proof of reachability.
- Runtime status and traffic refresh every 3 seconds, with a semaphore preventing overlapping polls. Polls pause while dialogs are open. Disconnection makes cached statuses explicitly unknown.
- Copying a wildcard listener opens a choice of actual up/non-loopback interface addresses; loopback addresses are clearly labelled local-only. Neither action promises firewall/routing availability.
- UI errors appear in an InfoBar. Unhandled UI exceptions are written to `%LocalAppData%/LanBridge/ui-error.log` without suppressing the crash.

## Lightweight presentation/lifecycle tests

These are a dependency-free executable harness, not a VSTest project:

```powershell
dotnet run --project tests/LanBridge.App.Tests/LanBridge.App.Tests.csproj
```

The harness links the real presentation and launch-policy source and checks IPv4/IPv6 address formatting, disconnected/listening/stopped/error/pending status semantics and sibling-only/current-user/hidden-console launch flags. Each behavior was run red (`NotImplementedException`) before implementation, then green.

## Verification notes

Native MSBuild Debug and Release builds both completed successfully on Windows 11 / SDK 10.0.403 / Visual Studio 2026. The Debug executable was launched successfully and remained alive. Windows UI Automation read back the Chinese native window, controls, disabled actions and explicit missing-sibling-backend error. Window screenshot capture returned a black DirectComposition surface and the composited screen was on another desktop, so appearance has not been visually certified. Do not treat this as end-to-end CRUD, LAN, Hyper-V, dark-mode, or firewall verification; those need the packaged sibling Host and an interactive test environment.
