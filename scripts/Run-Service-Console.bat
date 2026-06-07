@echo off
setlocal

set "ROOT=%~dp0.."
"%ROOT%\Service\LanMouseShare.Service.exe" --console
