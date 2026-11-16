@echo off
setlocal
cd /d "%~dp0"

if exist "dist" rmdir /s /q "dist"
if exist "dist" (
    echo Could not remove dist. Close BetterGhub and try again.
    exit /b 1
)

if /i "%~1"=="-Small" goto small
if not "%~1"=="" (
    echo Usage: build.cmd [-Small]
    exit /b 1
)

dotnet publish src\BetterGhub\BetterGhub.csproj -c Release -r win-x64 -o dist -p:PublishSingleFile=true -p:DebugType=none -nologo -p:OutputPath=obj\publish-bin\ --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
goto result

:small
dotnet publish src\BetterGhub\BetterGhub.csproj -c Release -r win-x64 -o dist -p:PublishSingleFile=true -p:DebugType=none -nologo -p:OutputPath=obj\publish-bin\ --self-contained false

:result
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Built %CD%\dist\BetterGhub.exe
