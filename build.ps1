# Builds dist\BetterGhub.exe: one self-contained file that runs on any 64-bit Windows 10/11 PC
# without installing .NET. Give that file to users; on first run it offers to install itself.
#   .\build.ps1              self-contained (larger, no prerequisites)
#   .\build.ps1 -Small       framework-dependent (~2 MB, needs the .NET 8 Desktop Runtime)
param([switch]$Small)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$out = Join-Path $PSScriptRoot 'dist'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }

$publish = @('publish', 'src\BetterGhub\BetterGhub.csproj', '-c', 'Release', '-r', 'win-x64', '-o', $out,
    '-p:PublishSingleFile=true', '-p:DebugType=none', '-nologo')
if ($Small) {
    $publish += @('--self-contained', 'false')
} else {
    $publish += @('--self-contained', 'true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
}
dotnet @publish
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$exe = Join-Path $out 'BetterGhub.exe'
Write-Host ("Built {0} ({1:N1} MB)" -f $exe, ((Get-Item -LiteralPath $exe).Length / 1MB))
