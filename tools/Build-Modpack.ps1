[CmdletBinding()]
param([string[]]$PackArguments = @())
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$workspaceSetup = Join-Path (Split-Path $repoRoot -Parent) 'Open-Workspace.ps1'
if (Test-Path -LiteralPath $workspaceSetup) { . $workspaceSetup }
. (Join-Path $PSScriptRoot 'Invoke-QuietProcess.ps1')
$uv = (Get-Command uv -CommandType Application -ErrorAction Stop).Source
Push-Location $repoRoot
try {
    # pythonw avoids a Python console grandchild; redirected pipes still retain all logs.
    Invoke-QuietProcess -FilePath $uv -Arguments (@('run', '--gui-script', 'tools/build-modpack.py') + $PackArguments)
    if ($LASTEXITCODE -ne 0) { throw "Modpack build failed (exit $LASTEXITCODE)." }
} finally { Pop-Location }
