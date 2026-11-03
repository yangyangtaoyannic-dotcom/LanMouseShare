# LAN Mouse Share Package

## 0.2.0-beta-fix6 Behavior

- Fixed the fix5 regression where paste did nothing: the clipboard format IDs were compared sign-mismatched (FORMATETC.cfFormat is a signed short, RegisterClipboardFormat returns a positive int) so every format lookup failed; GetDataHere (the path Explorer really uses to read FileContents) was unimplemented; and the HGLOBAL allocator used GMEM_MOVEABLE without GlobalLock, corrupting the heap and risking Agent crashes. All three are fixed and verified with a local reflection test covering QueryGetData / GetData (HGLOBAL + lazy ISTREAM) / GetDataHere.
- Contents requests with lindex=-1 (OLE flush / bulk render) are rejected so nothing is fetched until an actual paste (transfer-on-paste preserved in both directions).
- The "open transfer folder" button now opens the real receive directory (public desktop, same as the service's ResolveIncomingFolder).
- Version: 0.2.0-beta-fix6 (BuildInfo single source of truth).

## 0.2.0-beta-fix6 Behavior

- Fixed the fix5 regression where paste did nothing: the clipboard format IDs were compared sign-mismatched (FORMATETC.cfFormat is a signed short, RegisterClipboardFormat returns a positive int) so every format lookup failed; GetDataHere (the path Explorer really uses to read FileContents) was unimplemented; and the HGLOBAL allocator used GMEM_MOVEABLE without GlobalLock, corrupting the heap and risking Agent crashes. All three are fixed and verified with a local reflection test covering QueryGetData / GetData (HGLOBAL + lazy ISTREAM) / GetDataHere.
- Contents requests with lindex=-1 (OLE flush / bulk render) are rejected so nothing is fetched until an actual paste (transfer-on-paste preserved in both directions).
- The "open transfer folder" button now opens the real receive directory (public desktop, same as the service's ResolveIncomingFolder).
- Version: 0.2.0-beta-fix6 (BuildInfo single source of truth).

## 0.2.0-beta-fix5 Behavior

- Fixed the fix4 regression: the virtual clipboard is now published from a dedicated long-lived STA thread with a message pump (`OleInitialize` + PeekMessage loop). `OleSetClipboard` requires OLE-initialized STA — on the fix4 MTA thread it always failed with `0x800401F0 (CO_E_NOTINITIALIZED)`, so the clipboard stayed empty and Explorer's paste was still greyed out. Paste callbacks are marshalled back to this STA, so big fetches never freeze the UI or the input hooks.
- Single-file fetch timeout raised from 35 s to 10 min (a 69 s transfer was observed for one large file — the old timeout aborted the paste halfway).
- Corner blocking on the return path (fix4) is confirmed working on both machines (blocked pushes logged at the peer's bottom-left corner, normal returns elsewhere).
- Version: 0.2.0-beta-fix5 (BuildInfo single source of truth).

## 0.2.0-beta-fix4 Behavior

- File clipboard is now a true delayed-render clipboard: after a copy on the peer, file names appear on the local clipboard immediately (Explorer's right-click "Paste" is no longer greyed out), and the bytes are fetched only at paste time — both directions, any paste gesture.
- The virtual clipboard object is hosted on a long-lived MTA thread (paste callbacks never freeze the UI), FileContents is served as a lazy IStream (OleFlushClipboard no longer forces a full fetch when you copy something else), publishing retries on a busy clipboard, and multi-file enumeration lists every file.
- Corner blocking now also applies to the return path (owner-side push back through the peer's edge), so B→A behaves the same as A→B.
- Version: 0.2.0-beta-fix4 (BuildInfo single source of truth).

## 0.2.0-beta-fix3 Behavior

- File clipboard is fully lazy: copying files on one machine no longer transfers anything; the transfer starts only when you paste (Ctrl+V) on the other machine, in both directions.
- Auto-start fixed: the scheduled task is created with `/IT` (no password prompt, works on Win7/10); if task creation fails, the Run registry key is kept as fallback; the Agent re-registers startup on load (self-heal).
- Corner blocking is now synced across machines: the checkbox state rides the layout-update protocol, so blocking works the same in both directions (A→B and B→A).
- Version: 0.2.0-beta-fix3 (BuildInfo single source of truth).

## 0.2.0-beta-fix2 Behavior

- Single instance: double-clicking the shortcut again no longer starts a second Agent (two tray icons, duplicated mouse hooks, pointer jumping). It activates the existing window and exits.
- Service lifecycle: the agent-managed service runs with `--agent <pid>` and stops itself within ~2 s after its owner process disappears, so nothing keeps running after the Agent is closed or crashes.
- Stuck-pipe recovery: if the IPC pipe fails 20 polls in a row (~10 s), the Agent restarts the service by itself; failures are logged only a few times instead of ~6.5 万 lines a day.
- Installer cleanup: kills leftover service processes on upgrade, removes legacy `Program Files (x86)\LanMouseShare`, ships no more `.pdb` files and keeps only the newest rollback backup.

## 0.1.52 Behavior

- Control signalling now uses one persistent TCP connection per peer instead of a fresh connection per message, so heartbeat and lease relay no longer trigger a connect storm that made control feel laggy and randomly drop back.
- Lease and acknowledgement packets are relayed through the paired Service link every 100 ms; UDP 47534 carries only mouse/keyboard data.
- Input frames carry a 16-bit sequence number, so duplicated or reordered datagrams are dropped instead of moving the pointer twice.
- After entering the remote screen (or after the controlled side releases), the edge switch stays latched until the pointer has moved at least 16 px away from the edge for 500 ms, which stops the "bounce back and forth" behaviour.
- If the network feels slow, clear `详细日志` and optionally untick `推送日志到主控端`; logs stay local for 3 days either way.

Install this package on both Windows PCs.

## Requirements

- Windows 7 SP1 or Windows 10.
- .NET Framework 4.8 Runtime.
- Administrator rights for service installation and firewall rules.

## Install

Recommended:

1. Run `LanMouseShareSetup.exe` as Administrator.
2. Repeat on the second PC.
3. Use the Agent window on both PCs to discover, pair, set screen direction, and test keyboard, mouse, clipboard, and file sharing.

Manual package mode:

1. Run `Scripts\Configure-Firewall.bat` as Administrator.
2. Run `Scripts\Install-Service.bat` as Administrator.
3. Run `Scripts\Start-Agent.bat`.
4. Repeat on the second PC.
5. If discovery does not show the other PC, type the other PC's IPv4 address into `Manual peer IP`, click `Use IP`, then pair with the six-digit code.

## 0.1.50 Behavior

- The Agent window shows `LanMouseShare 0.1.50`, current connection status, the current control target, the debug log status, and the measured network delay.
- Each PC can be configured as Controller or Agent. Agent PCs stream their logs to the controller over TCP 47540 so both sides can be inspected from one place.
- Each PC writes `Logs\SystemInfo-Service.txt` and `Logs\SystemInfo-Agent.txt` at startup (OS version, CPU/memory, monitor resolution and DPI, adapter MAC/IPv4, listening ports, role and controller address) and the agent forwards its copy to the controller together with the logs.
- Control ownership is now a single lease-based state machine (`Idle` / `Owner` / `Target`, see `Docs\CONTROL-STATE-MODEL.md`). The controller sends a 50 ms lease and expects an ack within 700 ms; the controlled PC only injects while a 600 ms lease is fresh. Either side therefore returns to idle on its own when the other side dies, stops, or the network drops, so a frozen keyboard or mouse is bounded to under a second.
- Lease and ack travel two paths with identical semantics: the direct UDP 47534 channel (fast path) and a relay through the already paired Service link (`SendControlLease` / `SendControlLeaseAck` -> peer `PollEvents`). The relay needs no extra firewall rule, so a blocked 47534 no longer disables cross-screen control; duplicates are dropped idempotently by epoch.
- Mouse input travels in compact frames on the direct channel: 2 bytes per relative move, 4 bytes per absolute move, up to 200 events per datagram, with automatic fallback to the general frame when keys, wheel, or larger coordinates are involved. A typical pointer stream is now roughly 100-200 B/s instead of about 3 KB/s.
- Interactive traffic is marked DSCP 46 (EF) on the UDP input channel and on relayed input, while log streaming is marked DSCP 8 and batched down to 20 lines / 60 ms while input is active, so log shipping no longer competes with the pointer. DHCP-enabled switches honour the marking; plain home routers just forward it.
- Log shipping can be switched off per PC with the `推送日志到主控端` toggle. Logs are always written locally under `%ProgramData%\LanMouseShare\Logs\live` first; the status line reports whether transfer is on.
- Copying files on one PC puts them on the other PC's clipboard once they finish transferring; the clipboard hint and the file transfer may arrive in any order.
- The controlled PC never promotes itself to controller. Returning the pointer to its own edge only sends a release request; the controller decides when and where the pointer comes back.
- Simultaneous edge switching is arbitrated by epoch plus device id, so both PCs converge on exactly one controller instead of locking each other out.
- New diagnostic events: `control.link.role`, `control.link.begin`, `control.link.lease`, `control.link.released`, `control.link.timeout`, `control.link.arbitrated`, `control.link.stale`, plus `screen.switch.*`, `mouse.*.position`, `mouse.screen.change`, `net.rtt`, `clipboard.*`, `input.ignored`, `ui.stall`.
- The Agent UI is restored to Chinese text. XAML stores Chinese labels as XML numeric entities to avoid source encoding damage.
- The middle of the Agent window scrolls vertically, so smaller displays can reach every setting.
- Input control is split into a session controller and an input router. UI code no longer owns the core routing decisions.
- The current target is explicit: when the target is PC A, keyboards on both PCs input to PC A; when the target is PC B, keyboards on both PCs input to PC B.
- The controller injects absolute pointer coordinates while the pointer is inside the remote screen, so the remote cursor tracks 1:1 instead of accumulating relative-motion error. Only at the remote screen border does it send relative steps, which carry the outward-push intent back to the controller.
- Returning from the remote PC requires a sustained outward push (12 px accumulated within 250 ms), followed by a 500 ms cooldown. Sitting on the edge, or nudging it once, no longer hands control back.
- Optional corner blocking prevents switching through the two corners on the configured edge in both directions. It is off by default and uses an 80px corner area.
- A pointer clamped at the remote screen border is normal and never treated as an injection failure. It is logged as `mouse.clamp.suspected` instead of releasing control.
- Injected key presses are tracked and released on stop, pause, release, or disconnect, so modifiers never stay stuck on the local keyboard.
- Cross-screen entry and return share the same proportional coordinate mapping: left/right preserve Y position, and up/down preserve X position.
- Administrator controls are available in the startup section: current permission status, `Run as administrator by default`, and `Restart as administrator now`.
- Administrator default startup uses a highest-privilege scheduled task for logon startup and avoids also keeping the normal `HKCU\Run` startup entry.
- Known remote-control or cross-screen apps such as Sunlogin, Mouse without Borders, Remote Desktop, AnyDesk, TeamViewer, Parsec, RustDesk, and ToDesk are treated as possible input-injection conflict windows.
- If a conflict window may block simulated input, the Agent prompts the user to return local control, continue temporarily, or ignore that process for the current run.
- Conflict windows are detected before injection; repeated blocked injections release control back to the local PC instead of leaving the pointer trapped. New diagnostic events: `key.forward`, `key.inject`, `mouse.remote.send`, `mouse.clamp.suspected`.
- Pause is a two-PC pause: mouse, keyboard, clipboard, and new file sends stop on both PCs until resumed.
- `Ctrl+F10` stops all cross-screen control on both PCs, no matter which side is currently controlling.
- Hotkeys are shown in the UI and can be customized. Defaults are `Ctrl+F11` for pause/resume, `Ctrl+F10` for stop control, and `Ctrl+Alt+End` for exit.
- Startup options are off by default: start with Windows, minimize to tray on close, and corner blocking.
- If minimize to tray is off, closing the Agent exits the Agent and requests the local service to stop.
- Pairing must be verified by a protected handshake before keyboard, mouse, clipboard, or files are accepted.
- File transfer supports files dropped onto the Agent window. Received files are saved to the public desktop `Mouse传输文件` folder (`C:\Users\Public\Desktop\Mouse传输文件`), which the LocalSystem service and the logged-on user both see.
- Clipboard can carry files: copy files on one PC, paste them on the other. Files travel through the existing file channel and the clipboard is updated when the last one lands.
- The installer uses exit codes and logs instead of normal install/uninstall pop-up dialogs.

## Logs

- Logs: `%ProgramData%\LanMouseShare\Logs\live`
- Agent logs use Agent_yyyyMMdd-HHmmss_pid.log.
- Forwarded agent logs land in `%ProgramData%\LanMouseShare\Logs\remote\<device>_<id8>`.
- Logs older than 3 days are pruned automatically at startup and every 6 hours.
- Service logs use `Service_yyyyMMdd-HHmmss_pid.log`.
- Installer errors are also written to `%TEMP%\LanMouseShareSetup.log`.

## Development Mode

Instead of installing the service, run `Scripts\Run-Service-Console.bat` on both PCs, then start the Agent.

## Release Packages

Every release should keep a unique versioned directory under `dist\LanMouseShare-<version>`.

Each package must include:

- `LanMouseShareSetup.exe`
- `LanMouseShare-<version>-source.zip`
- `source\` (the same source tree unpacked, one folder per release)

The source zip contains `src`, `tests`, `docs`, `scripts`, `patches`, `LanMouseShare.sln`, `PROJECT_GUIDE.md`, and `.gitignore`, while excluding `bin`, `obj`, `dist`, `.vs`, logs, and caches. The `source\` folder inside the package is produced from the same staging directory, so it always matches the zip.

## Uninstall

Use Windows "Programs and Features" or run `LanMouseShareSetup.exe /uninstall` as Administrator. Manual package mode can still use `Scripts\Uninstall-Service.bat`.
## 0.1.56 Behavior

- Copying files or folders only announces the clipboard content; the bytes are transferred when you paste on the other PC.
- The announcement prefers the direct UDP control channel and falls back to the paired TCP relay, so a paste within one second works.
- Folders are zipped by the sender and unpacked by the receiver, keeping the original tree.
- A new Cancel Transfers button stops an incoming transfer.
- Pasting into an application that does not accept files stays silent.

## 0.1.56 Behavior

- Cross-PC file paste is now request driven: the controlling side swallows its own
  Ctrl+V and asks the controlled side to pull the file and paste it, because both
  low-level hooks drop injected key events.
- Remote input injection runs on the UDP receive thread instead of the UI thread, so a
  minimized or busy window no longer delays mouse and keyboard.
- The clipboard file list is written on a dedicated STA thread and flushed, so the
  content survives the writing thread and no longer blocks the input hook.

## 0.2.0-beta-fix1 Behavior

- Clipboard announce order is text → image → file. This is the fix for "no clipboard
  sharing at all": `ContainsText()` is checked first, the image branch follows, and the
  file branch only runs when the drop list actually carries files.
- Copying a file or folder only announces it (`IsLazy = true`); the bytes move when you
  paste on the other PC, so large copies no longer block the clipboard owner.
- Auto start is silent: the Run key and the scheduled task both use `... \Agent\LanMouseShare.Agent.exe" /minimized`,
  and the window starts hidden in the notification area.
- Corner blocking (`BlockScreenCorners`) is verified to be wired from config through
  `RemoteEdgeReturnDetector.Update`.

Build notes for this release:

- `SetupVersion` stays numeric (`0.2.0`) because `IsInstalledCurrentOrNewer()` compares it
  with `Version.TryParse`; the package folder name carries the `-beta-fix1` suffix.
- The unfinished 0.2.0 controller refactor (`ControllerIntegration.cs` and `Controllers\*.cs`)
  is excluded from compilation in `src\Desktop.Agent\Desktop.Agent.csproj`: it calls an
  `InputInjector`/`AppLog` API that does not exist yet, and no shipping code path
  references it. The files are kept in the source tree for the follow-up refactor.
