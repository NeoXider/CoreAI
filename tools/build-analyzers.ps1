param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "CoreAI.UnityAsyncAnalyzers\CoreAI.UnityAsyncAnalyzers.csproj"
$outDir = Join-Path $root "..\Assets\CoreAiUnity\RoslynAnalyzers"
dotnet build $proj -c $Configuration
$dll = Join-Path $root "CoreAI.UnityAsyncAnalyzers\bin\$Configuration\netstandard2.0\CoreAI.UnityAsyncAnalyzers.dll"
if (-not (Test-Path $dll)) { throw "Build output missing: $dll" }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Copy-Item -Force $dll (Join-Path $outDir "CoreAI.UnityAsyncAnalyzers.dll")
Write-Host "Copied analyzer to $outDir"
