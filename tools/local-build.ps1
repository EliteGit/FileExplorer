# Local test build: runs the tests, publishes a quick (framework-dependent) build to .\local\Fathom-local.exe, and starts it.
# It does not touch the installed copy. (It shares the same per-user data folder, so tabs/tags/settings are shared.)
# Usage: powershell -File tools\local-build.ps1 [-NoTests] [-NoRun]
param([switch]$NoTests, [switch]$NoRun)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $NoTests) {
        $sln = if (Test-Path 'FileExplorer.Local.slnx') { 'FileExplorer.Local.slnx' } else { 'FileExplorer.slnx' }
        dotnet test $sln --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed - not building.' }
    }
    Get-Process 'Fathom-local' -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path local) { Remove-Item local -Recurse -Force }
    dotnet publish FileExplorer.App -c Release -r win-x64 --self-contained false -o local --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Rename-Item local\Fathom.exe Fathom-local.exe
    Write-Host "Built $root\local\Fathom-local.exe"
    if (-not $NoRun) { Start-Process "$root\local\Fathom-local.exe" }
} finally { Pop-Location }
