param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDir,

    [Parameter(Mandatory = $true)]
    [string]$ProjectName,

    [Parameter(Mandatory = $true)]
    [string]$TargetPath,

    [Parameter(Mandatory = $true)]
    [string]$TargetFileName,

    [Parameter(Mandatory = $true)]
    [string]$TerrariaModderRoot,

    [Parameter(Mandatory = $false)]
    [switch]$IsCore
)

$ErrorActionPreference = "Stop"

function Copy-FileIfExists {
    param(
        [string]$SourcePath,
        [string]$DestinationPath
    )

    if (-not (Test-Path $SourcePath)) {
        return
    }

    $destinationDir = Split-Path -Parent $DestinationPath
    if (-not [string]::IsNullOrWhiteSpace($destinationDir)) {
        New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
    }

    try {
        Copy-Item -Path $SourcePath -Destination $DestinationPath -Force
    }
    catch {
        Write-Host "[Deploy] Warning: Failed to copy '$SourcePath' -> '$DestinationPath' ($($_.Exception.Message))"
    }
}

if (-not (Test-Path $TerrariaModderRoot)) {
    New-Item -ItemType Directory -Path $TerrariaModderRoot -Force | Out-Null
}

$targetDir = Split-Path -Parent $TargetPath

if ($IsCore.IsPresent) {
    $coreDir = Join-Path $TerrariaModderRoot "core"
    New-Item -ItemType Directory -Path $coreDir -Force | Out-Null

    $coreFiles = Get-ChildItem -Path $targetDir -File |
        Where-Object { $_.Extension -in @(".dll", ".pdb", ".xml") }

    foreach ($file in $coreFiles) {
        try {
            Copy-Item -Path $file.FullName -Destination (Join-Path $coreDir $file.Name) -Force
        }
        catch {
            Write-Host "[Deploy] Warning: Failed to copy '$($file.FullName)' ($($_.Exception.Message))"
        }
    }

    Write-Host "[Deploy] Core -> $coreDir"
    exit 0
}

$manifestPath = Join-Path $ProjectDir "manifest.json"
if (-not (Test-Path $manifestPath)) {
    Write-Host "[Deploy] Skipped $ProjectName (manifest.json not found)"
    exit 0
}

$manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
$modId = if ($manifest.id) { [string]$manifest.id } else { $ProjectName }
$entryDll = if ($manifest.entry_dll) { [string]$manifest.entry_dll } else { $TargetFileName }

$modsRoot = Join-Path $TerrariaModderRoot "mods"
$modDir = Join-Path $modsRoot $modId
New-Item -ItemType Directory -Path $modDir -Force | Out-Null

Copy-FileIfExists -SourcePath $TargetPath -DestinationPath (Join-Path $modDir $entryDll)
Copy-FileIfExists -SourcePath $manifestPath -DestinationPath (Join-Path $modDir "manifest.json")

$pdbPath = [System.IO.Path]::ChangeExtension($TargetPath, ".pdb")
Copy-FileIfExists -SourcePath $pdbPath -DestinationPath (Join-Path $modDir ([System.IO.Path]::GetFileName($pdbPath)))

$assetsPath = Join-Path $ProjectDir "assets"
if (Test-Path $assetsPath) {
    $assetsDestination = Join-Path $modDir "assets"
    New-Item -ItemType Directory -Path $assetsDestination -Force | Out-Null

    Copy-Item -Path (Join-Path $assetsPath "*") -Destination $assetsDestination -Recurse -Force
}

Write-Host "[Deploy] $ProjectName -> $modDir"
