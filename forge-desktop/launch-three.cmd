@echo off
setlocal

cd /d "%~dp0"

start "Mana Table - Host" cmd /k "set MANA_ALLOW_MULTI_INSTANCE=1&& set MANA_USER_DATA_DIR=%~dp0.data-host&& set FORGE_USER_DATA=%~dp0.data-host&& npm start"
timeout /t 2 /nobreak >nul
start "Mana Table - Player 2" cmd /k "set MANA_ALLOW_MULTI_INSTANCE=1&& set MANA_USER_DATA_DIR=%~dp0.data-second&& set FORGE_USER_DATA=%~dp0.data-second&& npm start"
timeout /t 2 /nobreak >nul
start "Mana Table - Player 3" cmd /k "set MANA_ALLOW_MULTI_INSTANCE=1&& set MANA_USER_DATA_DIR=%~dp0.data-third&& set FORGE_USER_DATA=%~dp0.data-third&& npm start"

endlocal
