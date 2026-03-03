@echo off
setlocal enabledelayedexpansion

echo ========================================
echo Building TerrariaModder
echo ========================================
echo.

REM Check setup has been run
if not exist "%~dp0Terraria\Terraria.exe" (
    echo ERROR: Terraria not linked. Run setup.bat first.
    exit /b 1
)

REM Create build directories
if not exist "build\core" mkdir "build\core"
if not exist "build\plugins" mkdir "build\plugins"

REM Build Core first (always required)
echo Building Core...
dotnet build src/Core/TerrariaModder.Core.csproj -c Release --nologo -v q
if errorlevel 1 (
    echo ERROR: Core build failed!
    exit /b 1
)
echo       Core OK

REM Build all mods found in src/ (skip Core, it's already built)
set COUNT=0
set FAIL=0
for /d %%d in (src\*) do (
    set "MOD_NAME=%%~nxd"
    if /i not "!MOD_NAME!"=="Core" (
        if exist "src\!MOD_NAME!\!MOD_NAME!.csproj" (
            set /a COUNT+=1
            echo Building !MOD_NAME!...
            dotnet build "src\!MOD_NAME!\!MOD_NAME!.csproj" -c Release --nologo -v q
            if errorlevel 1 (
                echo       ERROR: !MOD_NAME! build failed!
                set /a FAIL+=1
            ) else (
                echo       !MOD_NAME! OK
            )
        )
    )
)

echo.
if !FAIL! gtr 0 (
    echo ========================================
    echo Build finished with !FAIL! failure^(s^) out of !COUNT! mods
    echo ========================================
    exit /b 1
) else (
    echo ========================================
    echo Build complete: Core + !COUNT! mods
    echo ========================================
)
echo.
echo To deploy, run: deploy.bat
echo.
