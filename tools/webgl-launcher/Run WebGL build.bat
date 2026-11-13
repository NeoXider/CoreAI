@echo off
rem Starts the local model proxy and a local web server in this folder, then opens the build in the default browser.
rem A WebGL build cannot be opened by double-clicking index.html: browsers refuse to load it from a file.
rem The build's chat talks to http://127.0.0.1:1234/v1. The proxy there forwards to the model server named in
rem model-server.txt (first line) next to this file; edit it to point at another server.
setlocal
cd /d "%~dp0"

set "PROXY_PORT=1234"
set "UPSTREAM=http://127.0.0.1:8080/v1"
if exist "%~dp0model-server.txt" set /p UPSTREAM=<"%~dp0model-server.txt"

powershell -NoProfile -Command "try { (New-Object Net.Sockets.TcpClient('127.0.0.1', %PROXY_PORT%)).Close(); exit 0 } catch { exit 1 }"
if not errorlevel 1 (
    echo Port %PROXY_PORT% is already in use: the chat will use what runs there.
    goto serve
)

if not exist "%~dp0g11_proxy.py" (
    echo g11_proxy.py is missing next to this file: the chat will have no model.
    goto serve
)

set "PYTHON="
where python >nul 2>nul && set "PYTHON=python"
if not defined PYTHON where py >nul 2>nul && set "PYTHON=py -3"
if not defined PYTHON (
    echo Python 3 is not installed: the chat will have no model. The game itself still runs.
    goto serve
)

echo Model proxy: http://127.0.0.1:%PROXY_PORT%/v1 -^> %UPSTREAM%
start "CoreAI model proxy (close to stop)" /min %PYTHON% "%~dp0g11_proxy.py" --port %PROXY_PORT% --upstream %UPSTREAM% --upstream-timeout 3600

:serve
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0serve.ps1" -Port 8770
if errorlevel 1 pause
