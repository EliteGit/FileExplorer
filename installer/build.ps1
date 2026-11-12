# Builds installer/Fathom-1.1.0-x64.msi (installer instead of single-file; Windows 10+, self-contained so no .NET install needed).
# The tool manifest in this folder pins WiX 5.0.2 (restored with `dotnet tool restore`).
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $here
try {
    if (Test-Path publish) { Remove-Item publish -Recurse -Force }
    dotnet publish ..\FileExplorer.App -c Release -r win-x64 --self-contained -o publish
    if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
    dotnet tool restore
    dotnet wix build Package.wxs -arch x64 -o Fathom-1.1.0-x64.msi
    if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }
    Write-Host "Built $here\Fathom-1.1.0-x64.msi"
} finally { Pop-Location }
