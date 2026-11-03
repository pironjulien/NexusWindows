@echo off
rem Marketplace launcher: starts the NexusWindows engine installed on this PC (stable path of every install).
rem NexusSystem writes its own declaration with this path; this file serves the copy from the Antigravity Marketplace.
setlocal
set "ENGINE=%USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe"
if exist "%ENGINE%" goto run
>&2 echo NexusWindows is not installed on this PC. Ask Antigravity to install it ^(see the nexus-windows skill^), or follow https://github.com/pironjulien/NexusWindows#installation, then open a new conversation.
exit /b 1
:run
"%ENGINE%" %*
exit /b %ERRORLEVEL%
