[CmdletBinding()]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ReleaseArguments = @())
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outerRoot = Split-Path $repoRoot -Parent
if (Test-Path -LiteralPath (Join-Path $outerRoot 'Open-Workspace.ps1')) {
    . (Join-Path $outerRoot 'Open-Workspace.ps1') 6>$null
}
foreach ($folder in @('tools/uv', 'tools/git/cmd')) {
    $candidate = Join-Path $outerRoot $folder
    if (Test-Path -LiteralPath $candidate -PathType Container) { $env:PATH = "$candidate;$env:PATH" }
}
$uv = (Get-Command uv -CommandType Application -ErrorAction Stop).Source
Push-Location $repoRoot
try {
    if ($ReleaseArguments.Count -eq 0) {
        # Interactive prompts need the caller's real terminal.
        & $uv run --script tools/release.py
    } else {
        . (Join-Path $PSScriptRoot 'Invoke-QuietProcess.ps1')
        Invoke-QuietProcess -FilePath $uv -Arguments (@('run', '--gui-script', 'tools/release.py') + $ReleaseArguments) -PreserveStreams:($ReleaseArguments -contains '--json')
    }
    $result = $LASTEXITCODE
} finally { Pop-Location }
exit $result
