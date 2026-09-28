# Agent instructions

## Rebuild `dist\` after every change

The user launches the app from `dist\BetterGhub.exe`, so after every code change (once it compiles), republish it:

```powershell
.\build.ps1
```

- `build.ps1` wipes and recreates `dist\`. If that fails because `dist\BetterGhub.exe` is running, stop it (`Stop-Process -Name BetterGhub`), rerun the build, and tell the user to relaunch it.
- A plain `dotnet build` only updates `src\BetterGhub\bin\` and does not count.
- Report the build result; if it fails, fix it or say so rather than leaving a stale `dist\`.
