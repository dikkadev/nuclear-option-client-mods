# Native build/check children must not allocate a console on Windows.
function Invoke-QuietProcess {
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$Arguments = @(), [switch]$PreserveStreams)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.WorkingDirectory = (Get-Location).Path
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($start.PSObject.Properties['ArgumentList']) {
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    } else {
        # Windows PowerShell 5 uses a command-line string; quote every argument for the native parser.
        $quoted = foreach ($argument in $Arguments) {
            '"' + [regex]::Replace([regex]::Replace($argument, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
        }
        $start.Arguments = $quoted -join ' '
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        # Read both pipes concurrently: a full stderr pipe must never block the stdout reader.
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $outputTask.GetAwaiter().GetResult()
        $errors = $errorTask.GetAwaiter().GetResult()
        if ($PreserveStreams) {
            if ($output) { Write-Output -NoEnumerate $output.TrimEnd("`r", "`n") }
            if ($errors) { [Console]::Error.Write($errors) }
        } else {
            if ($output) { Write-Host $output -NoNewline }
            if ($errors) { Write-Host $errors -NoNewline }
        }
        $global:LASTEXITCODE = $process.ExitCode
    } finally { $process.Dispose() }
}
