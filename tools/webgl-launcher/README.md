# WebGL launcher

A Unity WebGL build cannot be opened by double-clicking `index.html`: browsers refuse to load it from a file. Copy
`serve.ps1`, `Run WebGL build.bat`, `model-server.txt` and `tools/G11Proxy/g11_proxy.py` next to the build's
`index.html` and start the `.bat`. The folder can then be moved anywhere: the `.bat` finds everything next to itself.

The `.bat` does two things:

1. Starts the model proxy (`g11_proxy.py`, Python 3 standard library only) on `http://127.0.0.1:1234/v1` in a
   minimized window, forwarding to the model server named on the first line of `model-server.txt` (default
   `http://127.0.0.1:8080/v1`). The build's chat talks to `127.0.0.1:1234`; the proxy adds the CORS headers a browser
   needs and waits up to an hour for a long answer. When the port is already taken (LM Studio, a proxy started
   earlier) the `.bat` leaves it alone and the chat uses what runs there. Without Python the game runs, the chat does
   not. Close the proxy's window to stop it.
2. Serves the folder on `http://localhost:8770/` (the next free port up to 8779 when that one is taken) and opens the
   default browser. Nothing is installed: Windows PowerShell and .NET's `HttpListener` do the work. Close the window
   to stop.

`serve.ps1 -Port <n>` picks another first port and `-NoBrowser` only serves. Files built with Brotli or gzip names
(`.br`, `.gz`) get the matching `Content-Encoding`; builds with Unity's decompression fallback (`.unityweb`) need
nothing special.

## Block World alone

`CoreAI.Editor.CoreAIG11WebGlBuild.Build` builds the 18 frozen demo scenes into `artifacts/G11-WebGL`. To build the
Block World alone, name the other 17 scenes in `COREAI_G11_EXCLUDE_SCENES` (file names without the extension,
separated by `;`) and start Unity with `-batchmode -quit -buildTarget WebGL -executeMethod
CoreAI.Editor.CoreAIG11WebGlBuild.Build`. The build takes about 35 minutes on a warm Library.
