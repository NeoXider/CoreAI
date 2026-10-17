#Requires -Version 5.1
<#
.SYNOPSIS
  Benchmark sweep over all 9 OpenCode free models (no Nemotron) via Neoxider Agents bridges.
.DESCRIPTION
  Starts one detached openai-server bridge per model (ports 8831-8839, see targets.json),
  runs the CoreAI Game-Creation Benchmark multi-target suite against them, then stops
  the bridges. Requires: OpenCode CLI authenticated for the free tier
  (otherwise every route answers HTTP 403 FreeTierError), and a free Unity Editor
  (close the Editor first - batchmode fails while Temp/UnityLockfile is held).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File dev-docs/opencode-free-bench-2026-10-16/run_free_sweep.ps1
#>
$ErrorActionPreference = 'Stop'

# WHY: the OpenCode free tier refuses tool-less bridge sessions (FreeTierError 403).
# The reduced-tool profile (neoxider-agents commit 0b4f600) passes the gate while still
# forbidding shell, writes, edits, prompts, subagents and MCP execution. Requires an
# up-to-date neoxider-agents checkout; without it every route answers 403.
$env:AGENT_OPENCODE_FREETIER_COMPAT = '1'

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$TargetsFile = Join-Path $Here 'targets.json'
$ProjectPath = (Resolve-Path (Join-Path $Here '..\..')).Path

$Ports = @(8831, 8832, 8833, 8834, 8835, 8836, 8837, 8838, 8839)
$Models = @(
  'opencode/beam-free',
  'opencode/big-pickle',
  'opencode/deepseek-v4.1-flash-free',
  'opencode/glm-5.3-flash-free',
  'opencode/ll-alpha-free',
  'opencode/longcat-2.5-preview-free',
  'opencode/mimo-v2.6-flash-free',
  'opencode/muse-spark-1.3-contributor-free',
  'opencode/space-bunny-free'
)

$SK = @($env:NEOXIDER_AGENTS_HOME, $env:CLAUDE_PLUGIN_ROOT,
  (Join-Path $HOME '.claude/skills/cli-agents'),
  (Join-Path $HOME '.claude/skills/neoxider-agents')) |
  Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'agent.ps1')) } |
  Select-Object -First 1
if (-not $SK) { throw 'Locate the installed neoxider wrapper before launching.' }
$SK = Join-Path $SK 'agent.ps1'

if (Test-Path -LiteralPath (Join-Path $ProjectPath 'Temp\UnityLockfile')) {
  throw 'Unity Editor holds the project lock (Temp/UnityLockfile). Close the Editor, then rerun.'
}

Write-Host 'Pre-flight: one cheap check per route (aborts the sweep on 403).'
& $SK openai-server check -m $Models
if ($LASTEXITCODE -ne 0) { throw "Pre-flight route check failed (exit $LASTEXITCODE). Fix OpenCode free-tier auth first." }

try {
  for ($i = 0; $i -lt $Models.Count; $i++) {
    Write-Host "Starting bridge $($Models[$i]) on port $($Ports[$i])"
    & $SK openai-server start --detach -e opencode -m $Models[$i] -p $Ports[$i]
    if ($LASTEXITCODE -ne 0) { throw "Bridge failed for $($Models[$i])" }
  }
  & $SK openai-server list

  $Targets = Get-Content -Raw -LiteralPath $TargetsFile
  $Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.14f1\Editor\Unity.exe'
  # WHY: free-tier quota is thin, so targets run 2-at-a-time; 0/all would hammer 9 routes at once.
  & $Unity -batchmode -projectPath $ProjectPath `
    -executeMethod CoreAI.Tests.EditMode.GameCreationBenchmarkLauncher.RunFromCli `
    -coreAiBenchmarkTargets $Targets `
    -coreAiBenchmarkParallel 2 `
    -coreAiBenchmarkGroups G1,G2,G3,G4,G5,G6,G7,G8 `
    -coreAiBenchmarkReps 1
  if ($LASTEXITCODE -ne 0) { throw "Benchmark run failed (exit $LASTEXITCODE)." }
}
finally {
  Write-Host 'Stopping all bridges.'
  & $SK openai-server stop all
}
