# LAN Mouse Share Package

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

## 0.1.41 Behavior

- The Agent window shows `LanMouseShare 0.1.41`, current connection status, and the current control target.
- The Agent UI is restored to Chinese text. XAML stores Chinese labels as XML numeric entities to avoid source encoding damage.
- The middle of the Agent window scrolls vertically, so smaller displays can reach every setting.
- Input control is split into a session controller and an input router. UI code no longer owns the core routing decisions.
- The current target is explicit: when the target is PC A, keyboards on both PCs input to PC A; when the target is PC B, keyboards on both PCs input to PC B.
- Edge switching no longer uses a 12px push threshold. Switching happens at the configured physical edge while the pointer keeps moving outward.
- Optional corner blocking prevents switching through the two corners on the configured edge in both directions. It is off by default and uses an 80px corner area.
- Returning from the remote PC no longer applies the old 600ms edge cooldown, so continuous A-to-B-to-A movement is not time-blocked.
- Cross-screen entry and return share the same proportional coordinate mapping: left/right preserve Y position, and up/down preserve X position.
- Administrator controls are available in the startup section: current permission status, `Run as administrator by default`, and `Restart as administrator now`.
- Administrator default startup uses a highest-privilege scheduled task for logon startup and avoids also keeping the normal `HKCU\Run` startup entry.
- Known remote-control or cross-screen apps such as Sunlogin, Mouse without Borders, Remote Desktop, AnyDesk, TeamViewer, Parsec, RustDesk, and ToDesk are treated as possible input-injection conflict windows.
- If a conflict window may block simulated input, the Agent prompts the user to return local control, continue temporarily, or ignore that process for the current run.
- Failed or ineffective `SendInput` mouse injection is tracked; repeated failures release control back to the local PC instead of leaving the pointer trapped.
- Pause is a two-PC pause: mouse, keyboard, clipboard, and new file sends stop on both PCs until resumed.
- `Ctrl+F10` stops all cross-screen control on both PCs, no matter which side is currently controlling.
- Hotkeys are shown in the UI and can be customized. Defaults are `Ctrl+F11` for pause/resume, `Ctrl+F10` for stop control, and `Ctrl+Alt+End` for exit.
- Startup options are off by default: start with Windows, minimize to tray on close, and corner blocking.
- If minimize to tray is off, closing the Agent exits the Agent and requests the local service to stop.
- Pairing must be verified by a protected handshake before keyboard, mouse, clipboard, or files are accepted.
- File transfer supports files dropped onto the Agent window. Received files are saved to the desktop `Mouse传输文件` folder.
- The installer uses exit codes and logs instead of normal install/uninstall pop-up dialogs.

## Logs

- Logs: `%ProgramData%\LanMouseShare\Logs`
- Agent logs use `Agent_yyyyMMdd-HHmmss_pid.log`.
- Service logs use `Service_yyyyMMdd-HHmmss_pid.log`.
- Installer errors are also written to `%TEMP%\LanMouseShareSetup.log`.

## Development Mode

Instead of installing the service, run `Scripts\Run-Service-Console.bat` on both PCs, then start the Agent.

## Release Packages

Every release should keep a unique versioned directory under `dist\LanMouseShare-<version>`.

Each package must include:

- `LanMouseShareSetup.exe`
- `LanMouseShare-<version>-source.zip`

The source zip contains `src`, `tests`, `docs`, `scripts`, `LanMouseShare.sln`, and `.gitignore`, while excluding `bin`, `obj`, `dist`, `.vs`, logs, and caches.

## Uninstall

Use Windows "Programs and Features" or run `LanMouseShareSetup.exe /uninstall` as Administrator. Manual package mode can still use `Scripts\Uninstall-Service.bat`.
