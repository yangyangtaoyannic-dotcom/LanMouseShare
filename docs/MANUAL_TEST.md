# Manual Test Checklist

Use two Windows machines on the same LAN. Supported combinations are Win10 + Win10 and Win7 SP1 + Win10.

1. Install .NET Framework 4.8 on both machines.
2. Build the solution in Release mode.
3. Allow UDP `47531`, TCP `47532`, TCP `47540`, UDP `47533`, and UDP `47534` through Windows Firewall.
4. Start `LanMouseShare.Service.exe --console` on both machines for development testing.
5. Start `LanMouseShare.Agent.exe` on both machines.
6. Confirm both Agent windows show `LanMouseShare 0.1.47`.
7. Confirm each Agent discovers the other device, or set the other PC's IPv4 address manually.
8. Enter the same six-digit code on one side and click Pair; enter it on the other side and click Approve.
9. Confirm both Agents show the paired and connected state only after both sides are actually paired.
10. Clear pairing or use mismatched old config on one side and confirm keyboard, mouse, clipboard, and files are blocked until pairing is fixed.
11. Set the peer direction on both machines so the layouts are opposite.
12. Move the pointer to the configured physical edge and continue moving outward; confirm remote control starts without a 12px push distance.
13. Test A to B and B to A switching in all configured directions that matter for your setup.
14. Confirm the current control target text changes when control moves to the peer and returns locally.
15. Confirm keyboard input follows the current target: when the target is A, both A and B keyboards input to A; when the target is B, both A and B keyboards input to B.
16. Confirm keyboard input works both ways, including letters, numbers, Enter, Backspace, arrow keys, and Ctrl/Alt/Shift shortcuts.
17. Press the configured pause hotkey, default `Ctrl+F11`, and confirm both PCs pause mouse, keyboard, clipboard, and new file sends.
18. Press the configured pause hotkey again and confirm both PCs resume capability without automatically switching screens.
19. Press the configured stop hotkey, default `Ctrl+F10`, and confirm both PCs stop all remote control and return to local control.
20. Change each hotkey in the UI and confirm the new shortcut is used.
21. Copy text on either machine and confirm the other machine receives it while not paused.
22. Copy an image on either machine and confirm the other machine receives it while not paused.
23. Pause control and confirm clipboard changes no longer sync.
24. Drag one or more ordinary files onto the Agent window and confirm they appear in the other user's desktop `Mouse传输文件` folder.
25. Pause control and confirm dropping files onto the Agent window is blocked.
26. Enable corner blocking and confirm the configured edge corners do not switch screens, while the middle of the same edge still switches.
27. Enable start with Windows and confirm the current user `Run` entry is created; disable it and confirm it is removed.
28. Enable minimize to tray on close, close the window, and confirm the Agent hides to tray while service continues.
29. Reduce the Agent window height and confirm the middle content scrolls with the mouse wheel.
30. Build a package and confirm `LanMouseShareSetup.exe` and `LanMouseShare-<version>-source.zip` both exist.
31. Open the source zip and confirm it does not contain `bin`, `obj`, `dist`, `.vs`, logs, or caches.
## Debug logging checks

1. Decide the roles: one PC is the controller, the other is the agent.
2. On the agent PC open the debug logging section, select `Agent` as the role, and enter `<controller IPv4>:47540` as the controller address.
3. On the controller PC select `Controller` as the role. Enter the same debug token on both PCs, or leave both empty.
4. Save on both PCs and confirm the status text shows a connection, for example `log forwarding: connected` on the agent and one connected client on the controller.
5. Confirm `%ProgramData%\LanMouseShare\Logs\live` receives a new log file on both PCs.
6. Confirm `%ProgramData%\LanMouseShare\Logs\remote\<device>_<id8>` receives the forwarded agent log on the controller PC.
7. Trigger a real action (pair, edge switch, clipboard sync, file drop) and confirm both sides record it.
8. Enable verbose logging and confirm low-level events appear; disable it afterwards.
9. Clear the controller address on the agent and confirm forwarding falls back to the discovered or paired peer address.
10. Set a wrong debug token on the agent and confirm the controller rejects it and the agent keeps retrying without crashing.
11. Create a log file with an old timestamp in `live` and restart the service; confirm files older than the retention days are removed while newer files stay.
12. Change the retention days to 1, save, and confirm the value is accepted; try 0 and 99 and confirm the UI falls back to the previous valid value.

## Version 0.1.44 checks

1. Move the pointer out through the configured edge and confirm `screen.switch.out` is logged on the local PC and `screen.switch.back` is logged when control returns.
2. Confirm the returning pointer does not immediately jump back to the peer: no `screen.switch.out` right after `screen.switch.back` unless the pointer really leaves the edge again.
3. Move the pointer around on both PCs with verbose logging on and confirm `mouse.local.position` and `mouse.remote.position` lines appear.
4. Move the Agent window to a second monitor (if available) and confirm `mouse.screen.change` records the new monitor bounds.
5. Confirm `net.rtt` lines appear about every five seconds on both PCs after pairing, and that a timeout is logged as WARN instead of crashing the service.
6. Copy text and an image on each PC and confirm `clipboard.send`, `clipboard.apply`, and `clipboard.image.ignored` appear where expected.
7. Confirm `Logs\SystemInfo-Service.txt` and `Logs\SystemInfo-Agent.txt` exist on both PCs and contain OS version, monitor resolution and DPI, adapter MAC/IPv4, and the two listening ports.
8. Confirm the controller PC receives `remote\<device>_<id8>\SystemInfo-Agent.txt` from the agent automatically.
9. Stop and start the service and confirm the system info files are regenerated with the current state.
10. Deliberately switch control A to B and B to A several times in a row; both PCs must keep responding to local keyboard and mouse, and the log must not contain repeated `control.link.arbitrated`.
11. Confirm `Ctrl+F10` still stops control immediately on both PCs, even while the pointer is on the remote screen.

## Version 0.1.45 control-model checks

These steps verify `docs\CONTROL-STATE-MODEL.md`. Run them on two real PCs.

1. Confirm both Agent windows show `LanMouseShare 0.1.45`.
2. Both PCs start idle: local keyboard and mouse work on both.
3. Switch A to B: A logs `control.link.role` (Idle to Owner), `control.link.begin`, and `screen.switch.out`; B logs `control.link.role` (Idle to Target).
4. Return control: A logs `screen.switch.back`, B logs `control.link.released`, and both log `control.link.role` back to Idle.
5. While A controls B, push the pointer to the far edge on B (the side facing A) and keep pushing outward. B must only request a release; A must take control back within about one second; B must never log `control.link.begin` for that move.
6. Switch back and forth 20 times quickly. No round may leave either PC with a frozen keyboard or mouse, and the log must not contain repeated `control.link.timeout` or `control.link.arbitrated`.
7. While A controls B, close B's Agent. A must recover local input within about one second and log WARN `control.link.timeout reason=AckTimeout`.
8. While A controls B, unplug B's network cable for five seconds. A must recover local input and log the same ack timeout; reconnect and confirm switching works again.
9. Push the pointer out on both PCs at almost the same moment. After a few hundred milliseconds exactly one PC must remain the controller, both keyboards must work, and the loser must log `control.link.arbitrated`.
10. While B is controlled, confirm B's own keyboard, mouse, wheel and clipboard keep working locally.
11. Press `Ctrl+F11` on either PC: both PCs stop cross-screen control and both keyboards work; press again to resume.
12. Press `Ctrl+F10` on either PC: both PCs return to idle immediately, even while the pointer is on the remote screen.
13. Confirm a `control.link.lease` DEBUG line appears about every two seconds while a PC is the controller or the target, and that `control.link.stale` (if present) does not keep growing.
14. Confirm `%ProgramData%\LanMouseShare\Logs\live\*.log` lets you rebuild every state change from `control.link.*` alone (epoch must be monotonic per PC).

## Version 0.1.47 relay checks

1. Block inbound UDP 47534 on both PCs (Windows Firewall inbound rule for the Agent executable) and repeat the edge switch. Control must still work within about one second; the log may show `control.lease.relay.fail` / `control.ack.relay.fail`, but must not show repeated `control.link.timeout reason=AckTimeout`.
2. Block UDP 47534 on both PCs and additionally stop the Service on the second PC. The controller must drop back to local input within about one second (`control.link.timeout`), never leaving a swallowed keyboard.
3. Keep UDP 47534 open and stop only the second PC's Service. Lease and ack must travel over UDP direct (`control.link.arbitrated path=udp`), and control must still work.
4. Re-enable everything and confirm `control.link.arbitrated path=relay` appears when only the relay path is usable, and that switching stays stable for at least 20 rounds.

## Version 0.1.47 pointer and keyboard checks

1. Move the pointer to the remote screen and drive it to each of its four borders; the pointer must reach the very edge (last pixel) instead of stopping short, and it must follow the local pointer 1:1 without lag spikes.
2. Park the pointer on the remote border and stop moving: control must NOT return to the controller. Nudge it outwards once by a few pixels and stop: control must still NOT return.
3. Push the pointer outwards through the entry edge while moving: control returns to the controller once (log shows `screen.switch.return ... reason=EdgePush`) and the local pointer continues at the matching local edge.
4. Repeat the push 10 times in a row: every push returns control exactly once, no bounce, and the local pointer never jumps back to the remote screen on its own.
5. While controlling the remote PC, hold `Shift` and press letter keys: the remote receives the modified keys and the local PC does not type them.
6. Hold `Ctrl` on the controller, release control with `Ctrl+F10`, then use the local keyboard: no stuck modifier (letters must not act as shortcuts).
7. Type a full sentence with punctuation and Enter in a remote text field; verify no duplicated or missing characters.
8. Verify the remote log contains matching `key.forward` (controller) and `key.inject` (target) lines for the keys pressed, and that `mouse.remote.send` shows `abs=<x>,<y>` while the pointer is inside the remote screen.
9. Verify that clamping at the remote border no longer produces `Control released because input injection was blocked` in the target log.
10. Leave both PCs idle for 30 seconds while controlling the remote screen: no `control.link.timeout`, no `control.link.released`, and control keeps working afterwards.

## Version 0.1.50 bandwidth and clipboard checks

1. Start both Agents and check each window title shows `LanMouseShare 0.1.50`.
2. Open the Agent log: the first line must show `Version=0.1.50`, and the direct input channel must log `DirectInputChannel listening on UDP 47534 qos=dscp46` when it binds.
3. Control the remote PC and confirm the controller log contains `mouse.remote.send abs=<x>,<y>` while moving inside the remote screen, and that the service log contains `net.input.socket udp=47533 qos=dscp46`.
4. Compare pointer smoothness with 0.1.47: fast movement across the remote screen must not stutter, and the status line must show a plausible network delay in milliseconds.
5. Turn OFF `推送日志到主控端` on the controlled PC. The controller must stop receiving that PC's log lines within a few seconds, the status line must show the transfer is off, and the controlled PC must keep writing its own `Logs\live` files.
6. Turn the toggle back ON and confirm forwarding resumes and old files are not re-sent from the beginning.
7. While moving the pointer across the boundary continuously, confirm log forwarding does not make the pointer jerky (input is prioritized, log batches shrink while input is active).
8. On the controller, copy a small file in Explorer (`Ctrl+C`), then switch to the controlled PC and press `Ctrl+V`: the file must appear in `C:\Users\Public\Desktop\Mouse传输文件` and be pasted into the target folder. Repeat in the other direction.
9. Copy several files at once and verify all of them arrive and are all present on the other clipboard.
10. Disconnect the network while a file transfer is in progress: control must return to local within about one second, and neither Agent may crash.
## Version 0.1.52 signalling and latch checks

1. Install 0.1.52 on both PCs and confirm the window title shows `0.1.52`.
2. Clear `详细日志` on both ends, then switch control from A to B and back ten times within about a minute.
3. During the run, `%ProgramData%\LanMouseShare\Logs\live\Service_*.log` on the controller must show a single `net.outbound.open` line per peer and no repeated `net.send.slow` (a few may appear on a busy Wi-Fi link).
4. `control.link.timeout` must not repeat every second; at most one timeout per real disconnect.
5. Park the pointer at the remote screen edge and release control from the controlled side: the controller must stay local until the pointer has moved at least 16 px away from that edge.
6. Enter the remote screen, immediately stop the mouse for two seconds, then move again: the controller must not jump back on its own.
7. On the controlled PC, watch `input.stale` in the Agent log: duplicated datagrams must be dropped without moving the pointer twice.
8. Disconnect the network for five seconds: control returns locally within about a second, and reconnect restores switching without restarting either Agent.
9. Untick `推送日志到主控端` on the controlled PC: controller log shows `log.hub.transfer.off` and the controlled side keeps writing `live` logs locally.
10. Copy a file in Explorer on one PC and paste on the other; the file must land in `C:\Users\Public\Desktop\Mouse传输文件` and appear on the clipboard.
## Version 0.1.56 clipboard and folder checks

1. Copy a text file on PC A, then paste on PC B within two seconds: the file appears on the clipboard and pastes normally.
2. Copy a folder that contains subfolders on PC A, then paste on PC B: exactly one folder is created and the tree matches.
3. Copy a file on PC A, wait 30 seconds, then paste on PC B: the pull request still works.
4. Start a large transfer and press Cancel Transfers: the service logs `file.transfer.cancelled` and no partial folder remains on the clipboard.
5. Paste a copied file into Notepad: nothing pops up and no error dialog appears.
6. Copy on PC B and paste on PC A: the direction is symmetric.
7. Verify `clipboard.files.announced`, `clipboard.pull.serve`, `clipboard.files.extracted`, `clipboard.files.pasted` in both logs.
8. Disable clipboard sync on either side: paste does nothing and nothing is transferred.

## Version 0.1.56 paste and minimize checks

1. Install 0.1.56 on both PCs and confirm the window shows 0.1.56.
2. On the controlling PC, copy a file (Ctrl+C). The controlled PC must show
   `clipboard.files.announced path=udp` in its Agent log; the file must NOT be
   transferred yet and no "Mouse transfer" folder window may open.
3. Bring the mouse to the controlled PC and press Ctrl+V inside Notepad.
   The file must be transferred, appear in the clipboard, and be pasted as an
   attachment name (Notepad pastes the path text on some builds; Explorer pastes the file).
4. Repeat the paste twice in a row: the second paste must also work, the log must
   show `clipboard.pull.request` then `clipboard.files.pasted` each time.
5. On the controlled PC, minimize the LanMouseShare window and keep it minimized.
   Move the mouse and type on the controlling PC: input must still reach the
   controlled PC without a visible stall, and the log must not show
   `UI thread stall detected`.
6. Restore the controlled window and repeat step 5; behaviour must be identical.
7. Stop the controlled Agent (Exit). The controlling PC must fall back to local
   control within about a second and never keep swallowing input.
8. Check `%ProgramData%\LanMouseShare\Logs\live` on both PCs for
   `clipboard.paste.request`, `clipboard.pull.request`, `clipboard.files.applied`,
   `clipboard.files.pasted`; a missing `clipboard.paste.request` means the
   controlling side never saw the Ctrl+V (report the log lines).
