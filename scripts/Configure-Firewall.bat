@echo off
setlocal

net session >nul 2>&1
if not "%errorlevel%"=="0" (
  echo Please run this script as Administrator.
  exit /b 1
)

netsh advfirewall firewall add rule name="LAN Mouse Share Discovery" dir=in action=allow protocol=UDP localport=47531
netsh advfirewall firewall add rule name="LAN Mouse Share Transport" dir=in action=allow protocol=TCP localport=47532
netsh advfirewall firewall add rule name="LAN Mouse Share Input" dir=in action=allow protocol=UDP localport=47533
netsh advfirewall firewall add rule name="LAN Mouse Share Direct Input" dir=in action=allow protocol=UDP localport=47534
exit /b %errorlevel%
