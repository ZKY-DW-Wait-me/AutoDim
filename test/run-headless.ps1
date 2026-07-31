param(
    [string]$InputDwg,
    [string]$Script,
    [string]$Log,
    [int]$TimeoutSec = 90
)

# NOTE: launching accoreconsole directly from Git Bash (MSYS) hangs before reading the script,
# so it must be started via PowerShell.
$acad = "D:\Program Files\AutoCAD 2025\accoreconsole.exe"
$p = Start-Process -FilePath $acad `
    -ArgumentList @('/i', $InputDwg, '/s', $Script, '/l', 'en-US') `
    -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput $Log `
    -RedirectStandardError ($Log + '.err')

if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    $p.Kill()
    Write-Output "TIMEOUT_${TimeoutSec}s"
    exit 124
}
Write-Output "EXIT=$($p.ExitCode)"
exit $p.ExitCode
