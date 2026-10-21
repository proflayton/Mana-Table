@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -Command "$ErrorActionPreference = 'Stop'; $manifest = './dist/latest-native.json'; if (-not (Test-Path -LiteralPath $manifest)) { throw 'No native package was found. Build mana-native/package.ps1 first, or download Mana Table from the releases page.' }; $build = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json; Start-Process -FilePath (Join-Path $build.directory $build.executable) -WorkingDirectory $build.directory -WindowStyle Normal"
