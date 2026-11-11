param([int]$Port = 8770, [switch]$NoBrowser)
# Serves this folder (a Unity WebGL build) on http://localhost:<Port>/ and opens the default browser.
# No installs needed: Windows PowerShell and .NET's HttpListener. Close the window or press Ctrl+C to stop.
$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
if (-not $root.EndsWith([System.IO.Path]::DirectorySeparatorChar)) { $root = $root + [System.IO.Path]::DirectorySeparatorChar }

$mime = @{
    ".html" = "text/html; charset=utf-8"; ".htm" = "text/html; charset=utf-8"
    ".js" = "application/javascript"; ".mjs" = "application/javascript"; ".json" = "application/json"
    ".css" = "text/css"; ".wasm" = "application/wasm"; ".data" = "application/octet-stream"
    ".unityweb" = "application/octet-stream"; ".bundle" = "application/octet-stream"
    ".png" = "image/png"; ".jpg" = "image/jpeg"; ".jpeg" = "image/jpeg"; ".gif" = "image/gif"
    ".svg" = "image/svg+xml"; ".ico" = "image/x-icon"; ".webp" = "image/webp"
    ".txt" = "text/plain; charset=utf-8"; ".xml" = "application/xml"
    ".mp3" = "audio/mpeg"; ".ogg" = "audio/ogg"; ".wav" = "audio/wav"; ".mp4" = "video/mp4"
    ".ttf" = "font/ttf"; ".otf" = "font/otf"; ".woff" = "font/woff"; ".woff2" = "font/woff2"
}

# WHY a range: another program may already hold the port (a second copy of this server, a local tool).
$listener = $null
$firstPort = $Port
foreach ($candidate in $firstPort..($firstPort + 9)) {
    $attempt = New-Object System.Net.HttpListener
    $attempt.Prefixes.Add("http://localhost:$candidate/")
    try {
        $attempt.Start()
        $listener = $attempt
        $Port = $candidate
        break
    }
    catch {
        Write-Host "Port $candidate is busy, trying the next one."
    }
}
if ($null -eq $listener) {
    Write-Host "Could not listen on any port from $firstPort to $($firstPort + 9)."
    exit 1
}

Write-Host "Serving $root"
Write-Host "Open http://localhost:$Port/ (the browser is being opened). Close this window to stop."
if (-not $NoBrowser) { Start-Process "http://localhost:$Port/" }

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $response = $context.Response
        try {
            $relative = [System.Uri]::UnescapeDataString($context.Request.Url.AbsolutePath).TrimStart('/')
            if ([string]::IsNullOrEmpty($relative)) { $relative = "index.html" }
            $full = [System.IO.Path]::GetFullPath((Join-Path $root $relative))
            if ((Test-Path -LiteralPath $full -PathType Container)) { $full = Join-Path $full "index.html" }
            if (-not $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $full -PathType Leaf)) {
                $response.StatusCode = 404
                $bytes = [System.Text.Encoding]::UTF8.GetBytes("Not found")
                $response.ContentType = "text/plain; charset=utf-8"
            }
            else {
                $name = $full
                # Unity writes precompressed files as name.ext.br or name.ext.gz when the fallback is off.
                $encoding = $null
                if ($name.EndsWith(".br")) { $encoding = "br"; $name = $name.Substring(0, $name.Length - 3) }
                elseif ($name.EndsWith(".gz")) { $encoding = "gzip"; $name = $name.Substring(0, $name.Length - 3) }
                $extension = [System.IO.Path]::GetExtension($name).ToLowerInvariant()
                $type = $mime[$extension]
                if (-not $type) { $type = "application/octet-stream" }
                $bytes = [System.IO.File]::ReadAllBytes($full)
                $response.StatusCode = 200
                $response.ContentType = $type
                if ($encoding) { $response.AddHeader("Content-Encoding", $encoding) }
                $response.AddHeader("Cache-Control", "no-cache")
            }
            $response.ContentLength64 = $bytes.Length
            if ($context.Request.HttpMethod -ne "HEAD") {
                $response.OutputStream.Write($bytes, 0, $bytes.Length)
            }
        }
        catch {
            try { $response.StatusCode = 500 } catch { }
        }
        finally {
            try { $response.OutputStream.Close() } catch { }
        }
    }
}
finally {
    $listener.Stop()
}
