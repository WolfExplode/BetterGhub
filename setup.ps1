$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

if (-not (Test-Path -LiteralPath '.venv\Scripts\python.exe')) {
    python -m venv .venv
}
& '.venv\Scripts\python.exe' -m pip install -r requirements.txt
if ($LASTEXITCODE -ne 0) { throw 'Python dependency setup failed.' }

dotnet build '.\src\BetterGhub\BetterGhub.csproj' -c Release
if ($LASTEXITCODE -ne 0) { throw 'BetterGhub build failed.' }

Write-Host 'Ready. Run .\run.ps1 or open src\BetterGhub\bin\Release\net8.0-windows\BetterGhub.exe'
