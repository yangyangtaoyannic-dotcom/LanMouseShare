@echo off
setlocal

net session >nul 2>&1
if not "%errorlevel%"=="0" (
  echo Please run this script as Administrator.
  exit /b 1
)

set "ROOT=%~dp0.."
set "SERVICE_EXE=%ROOT%\Service\LanMouseShare.Service.exe"

net stop LanMouseShare >nul 2>&1

set "INSTALLUTIL=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe"
if not exist "%INSTALLUTIL%" set "INSTALLUTIL=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe"

"%INSTALLUTIL%" /u "%SERVICE_EXE%"
exit /b %errorlevel%
