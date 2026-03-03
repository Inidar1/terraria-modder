@echo off
setlocal enabledelayedexpansion

echo ========================================
echo TerrariaModder Developer Setup
echo ========================================
echo.

REM ----------------------------------------
REM Check .NET SDK
REM ----------------------------------------
where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET SDK not found.
    echo Install it from: https://dotnet.microsoft.com/download
    echo Or install Visual Studio 2022 which includes it.
    exit /b 1
)
echo [OK] .NET SDK found

REM ----------------------------------------
REM Check .NET Framework 4.8 targeting pack
REM ----------------------------------------
set "FW48_PATH=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
if not exist "%FW48_PATH%" (
    echo ERROR: .NET Framework 4.8 Developer Pack not found.
    echo Install it from: https://dotnet.microsoft.com/download/dotnet-framework/net48
    echo Or install Visual Studio 2022 which includes it.
    exit /b 1
)
echo [OK] .NET Framework 4.8 targeting pack found

REM ----------------------------------------
REM Find Terraria installation
REM ----------------------------------------
if exist "%~dp0Terraria\Terraria.exe" (
    echo [OK] Terraria already linked at: %~dp0Terraria
    goto :build_test
)

REM Check common Steam locations
set "STEAM_TERRARIA="
set "STEAM_PATH_1=C:\Program Files (x86)\Steam\steamapps\common\Terraria"
set "STEAM_PATH_2=D:\Steam\steamapps\common\Terraria"
set "STEAM_PATH_3=D:\SteamLibrary\steamapps\common\Terraria"
set "STEAM_PATH_4=E:\Steam\steamapps\common\Terraria"
set "STEAM_PATH_5=E:\SteamLibrary\steamapps\common\Terraria"

for %%P in (
    "%STEAM_PATH_1%"
    "%STEAM_PATH_2%"
    "%STEAM_PATH_3%"
    "%STEAM_PATH_4%"
    "%STEAM_PATH_5%"
) do (
    if exist "%%~P\Terraria.exe" (
        set "STEAM_TERRARIA=%%~P"
        goto :found_steam
    )
)

REM Try reading Steam's libraryfolders.vdf for custom library locations
if exist "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf" (
    for /f "tokens=2 delims=	" %%a in ('findstr /C:"path" "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"') do (
        set "LIB_PATH=%%~a"
        set "LIB_PATH=!LIB_PATH:"=!"
        if exist "!LIB_PATH!\steamapps\common\Terraria\Terraria.exe" (
            set "STEAM_TERRARIA=!LIB_PATH!\steamapps\common\Terraria"
            goto :found_steam
        )
    )
)

goto :ask_path

:found_steam
echo Found Terraria at: %STEAM_TERRARIA%
set /p USE_FOUND="Use this location? [Y/n]: "
if /i "!USE_FOUND!"=="n" goto :ask_path
set "TERRARIA_DIR=%STEAM_TERRARIA%"
goto :create_link

:ask_path
echo.
echo Could not auto-detect Terraria installation.
echo Please enter the full path to your Terraria folder
echo (the folder containing Terraria.exe):
echo.
set /p TERRARIA_DIR="Path: "

REM Strip surrounding quotes if present
set "TERRARIA_DIR=!TERRARIA_DIR:"=!"

if not exist "!TERRARIA_DIR!\Terraria.exe" (
    echo ERROR: Terraria.exe not found at: !TERRARIA_DIR!
    exit /b 1
)

:create_link
echo.
echo Creating link: %~dp0Terraria -^> !TERRARIA_DIR!
mklink /J "%~dp0Terraria" "!TERRARIA_DIR!"
if errorlevel 1 (
    echo ERROR: Failed to create directory junction.
    echo Try running this script as Administrator.
    exit /b 1
)
echo [OK] Terraria linked successfully

:build_test
echo.
echo ----------------------------------------
echo Running test build...
echo ----------------------------------------
echo.

REM Build Core to verify everything works
dotnet build src/Core/TerrariaModder.Core.csproj -c Release --nologo -v q
if errorlevel 1 (
    echo.
    echo ERROR: Test build failed. Check the errors above.
    echo Common fixes:
    echo   - Make sure Terraria.exe is version 1.4.5
    echo   - Make sure .NET Framework 4.8 Developer Pack is installed
    exit /b 1
)

echo.
echo ========================================
echo Setup complete!
echo ========================================
echo.
echo Next steps:
echo   1. Run build.bat to build all mods
echo   2. Run deploy.bat to deploy to your Terraria folder
echo   3. Run Terraria/TerrariaInjector.exe to launch with mods
echo.
echo For contribution workflow, see CONTRIBUTING.md
echo.
