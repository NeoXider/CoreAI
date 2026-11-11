# WebGL launcher

A Unity WebGL build cannot be opened by double-clicking `index.html`: browsers refuse to load it from a file. Copy
`serve.ps1` and `Run WebGL build.bat` next to the build's `index.html` and start the `.bat`. It serves the folder
on `http://localhost:8770/` (the next free port up to 8779 when that one is taken) and opens the default browser.
Nothing is installed: Windows PowerShell and .NET's `HttpListener` do the work. Close the window to stop.

`serve.ps1 -Port <n>` picks another first port and `-NoBrowser` only serves. Files built with Brotli or gzip names
(`.br`, `.gz`) get the matching `Content-Encoding`; builds with Unity's decompression fallback (`.unityweb`) need
nothing special.

## Block World alone

`CoreAI.Editor.CoreAIG11WebGlBuild.Build` builds the 18 frozen demo scenes into `artifacts/G11-WebGL`. To build the
Block World alone, name the other 17 scenes in `COREAI_G11_EXCLUDE_SCENES` (file names without the extension,
separated by `;`) and start Unity with `-batchmode -quit -buildTarget WebGL -executeMethod
CoreAI.Editor.CoreAIG11WebGlBuild.Build`. The build takes about 35 minutes on a warm Library.
