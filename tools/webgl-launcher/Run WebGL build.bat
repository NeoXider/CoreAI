@echo off
rem Starts a local web server in this folder and opens the build in the default browser.
rem A WebGL build cannot be opened by double-clicking index.html: browsers refuse to load it from a file.
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0serve.ps1" -Port 8770
if errorlevel 1 pause
