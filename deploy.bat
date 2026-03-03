@echo off
setlocal enabledelayedexpansion

echo ========================================
echo Deploying TerrariaModder to Terraria
echo ========================================
echo.

REM Check setup has been run
if not exist "%~dp0Terraria\Terraria.exe" (
    echo ERROR: Terraria not linked. Run setup.bat first.
    exit /b 1
)

REM Check build has been run
if not exist "build\core\TerrariaModder.Core.dll" (
    echo ERROR: Core not built. Run build.bat first.
    exit /b 1
)

set TERRARIA_PATH=%~dp0Terraria
set MODDER_PATH=%TERRARIA_PATH%\TerrariaModder

REM Create TerrariaModder directories
if not exist "%MODDER_PATH%\core" mkdir "%MODDER_PATH%\core"
if not exist "%MODDER_PATH%\core\deps" mkdir "%MODDER_PATH%\core\deps"
if not exist "%MODDER_PATH%\core\logs" mkdir "%MODDER_PATH%\core\logs"
if not exist "%MODDER_PATH%\core\Docs" mkdir "%MODDER_PATH%\core\Docs"
if not exist "%MODDER_PATH%\core\assets" mkdir "%MODDER_PATH%\core\assets"
if not exist "%MODDER_PATH%\mods" mkdir "%MODDER_PATH%\mods"

REM Deploy config.json if not exists (read by Core)
if not exist "%MODDER_PATH%\core\config.json" (
    echo Creating config.json...
    (
        echo {
        echo     "rootFolder": "TerrariaModder",
        echo     "coreFolder": "core",
        echo     "depsFolder": "core/deps",
        echo     "modsFolder": "mods",
        echo     "logsFolder": "core/logs",
        echo     "logLevel": "info"
        echo }
    ) > "%MODDER_PATH%\core\config.json"
)

REM Deploy config.ini if not exists (read by injector)
if not exist "%MODDER_PATH%\core\config.ini" (
    echo Creating config.ini...
    (
        echo rootFolder=TerrariaModder
        echo coreFolder=core
        echo depsFolder=core/deps
        echo modsFolder=mods
        echo logsFolder=core/logs
    ) > "%MODDER_PATH%\core\config.ini"
)

REM Deploy Core
echo Deploying Core...
copy /Y "build\core\TerrariaModder.Core.dll" "%MODDER_PATH%\core\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy Core DLL!
    exit /b 1
)
copy /Y "src\Core\README.md" "%MODDER_PATH%\core\Docs\" >nul 2>nul
copy /Y "src\Core\THIRD-PARTY-NOTICES.md" "%MODDER_PATH%\core\Docs\" >nul 2>nul
copy /Y "src\Core\Assets\icon.png" "%MODDER_PATH%\core\assets\" >nul 2>nul

REM Deploy Harmony
if exist "build\core\0Harmony.dll" (
    copy /Y "build\core\0Harmony.dll" "%MODDER_PATH%\core\deps\" >nul
)

REM Deploy all mods found in src/
set COUNT=0
for /d %%d in (src\*) do (
    set "MOD_NAME=%%~nxd"
    if /i not "!MOD_NAME!"=="Core" (
        if exist "src\!MOD_NAME!\manifest.json" (
            REM Read mod-id from manifest.json
            set "MOD_ID="
            for /f "tokens=2 delims=:," %%a in ('findstr /C:"\"id\"" "src\!MOD_NAME!\manifest.json"') do (
                set "MOD_ID=%%~a"
                REM Strip quotes and spaces
                set "MOD_ID=!MOD_ID: =!"
                set "MOD_ID=!MOD_ID:"=!"
            )

            if not "!MOD_ID!"=="" (
                if exist "build\plugins\!MOD_NAME!.dll" (
                    set /a COUNT+=1
                    if not exist "%MODDER_PATH%\mods\!MOD_ID!" mkdir "%MODDER_PATH%\mods\!MOD_ID!"
                    echo Deploying !MOD_ID!...
                    copy /Y "build\plugins\!MOD_NAME!.dll" "%MODDER_PATH%\mods\!MOD_ID!\" >nul
                    copy /Y "src\!MOD_NAME!\manifest.json" "%MODDER_PATH%\mods\!MOD_ID!\" >nul
                    copy /Y "src\!MOD_NAME!\README.md" "%MODDER_PATH%\mods\!MOD_ID!\" >nul 2>nul
                    copy /Y "src\!MOD_NAME!\icon.png" "%MODDER_PATH%\mods\!MOD_ID!\" >nul 2>nul
                    REM Copy assets directory if it exists
                    if exist "src\!MOD_NAME!\assets" (
                        xcopy /Y /Q /E /I "src\!MOD_NAME!\assets" "%MODDER_PATH%\mods\!MOD_ID!\assets" >nul 2>nul
                    )
                ) else (
                    echo WARNING: !MOD_NAME!.dll not found in build\plugins\, skipping
                )
            )
        )
    )
)

echo.
echo ========================================
echo Deployed: Core + !COUNT! mods
echo ========================================
echo.
echo Next: Run Terraria\TerrariaInjector.exe to launch with mods.
echo.
