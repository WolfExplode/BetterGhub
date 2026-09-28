$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$app = Join-Path $PSScriptRoot 'src\BetterGhub\bin\Release\net8.0-windows\BetterGhub.exe'
if (-not (Test-Path -LiteralPath $app)) { throw 'Run .\setup.ps1 first.' }
Start-Process -FilePath $app
