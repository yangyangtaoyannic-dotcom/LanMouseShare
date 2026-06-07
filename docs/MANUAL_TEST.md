# Manual Test Checklist

Use two Windows machines on the same LAN. Supported combinations are Win10 + Win10 and Win7 SP1 + Win10.

1. Install .NET Framework 4.8 on both machines.
2. Build the solution in Release mode.
3. Allow UDP `47531`, TCP `47532`, UDP `47533`, and UDP `47534` through Windows Firewall.
4. Start `LanMouseShare.Service.exe --console` on both machines for development testing.
5. Start `LanMouseShare.Agent.exe` on both machines.
6. Confirm both Agent windows show `LanMouseShare 0.1.41`.
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
