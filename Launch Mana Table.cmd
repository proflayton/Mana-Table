@echo off
setlocal
set ELECTRON_RUN_AS_NODE=
cd /d "%~dp0"
powershell.exe -NoProfile -Command "$ErrorActionPreference = 'Stop'; $manifest = if (Test-Path -LiteralPath './dist/latest-native.json') { './dist/latest-native.json' } else { './dist/latest-beta.json' }; $build = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json; Start-Process -FilePath (Join-Path $build.directory $build.executable)"
