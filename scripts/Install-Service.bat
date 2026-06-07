@echo off
setlocal

net session >nul 2>&1
if not "%errorlevel%"=="0" (
  echo Please run this script as Administrator.
  exit /b 1
)

set "ROOT=%~dp0.."
set "SERVICE_EXE=%ROOT%\Service\LanMouseShare.Service.exe"

if not exist "%SERVICE_EXE%" (
  echo Service executable not found: %SERVICE_EXE%
  exit /b 1
)

set "INSTALLUTIL=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe"
if not exist "%INSTALLUTIL%" set "INSTALLUTIL=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe"

net stop LanMouseShare >nul 2>&1
sc.exe delete LanMouseShare >nul 2>&1

"%INSTALLUTIL%" "%SERVICE_EXE%"
if not "%errorlevel%"=="0" exit /b %errorlevel%

sc.exe config LanMouseShare start= demand
exit /b %errorlevel%
