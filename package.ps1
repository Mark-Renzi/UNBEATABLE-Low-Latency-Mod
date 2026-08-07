<#
.SYNOPSIS
    Builds LowLatencyMod and produces a release zip in ./dist that you can
    extract directly into the game folder (merges into BepInEx/plugins/).
#>

$ErrorActionPreference = "Stop"

# PowerShell 5.1's Compress-Archive writes zip entries with backslash path
# separators, which breaks extraction on non-Windows tools. Build the zip by
# hand with forward slashes instead.
function New-ZipFromDirectory {
    param(
        [Parameter(Mandatory)] [string]$SourceDir,
        [Parameter(Mandatory)] [string]$DestinationZip
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path -LiteralPath $DestinationZip) {
        Remove-Item -LiteralPath $DestinationZip -Force
    }

    $fs = [System.IO.File]::Open($DestinationZip, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $files = Get-ChildItem -LiteralPath $SourceDir -Recurse -File
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($SourceDir.Length).TrimStart('\', '/')
                $entryName = $relative -replace '\\', '/'
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $file.FullName, $entryName,
                    [System.IO.Compression.CompressionLevel]::Optimal
                ) | Out-Null
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $fs.Dispose()
    }
}

$root = $PSScriptRoot
$csproj = Join-Path $root "LowLatencyMod.csproj"
$csFile = Join-Path $root "LowLatencyMod.cs"

# Pull the version straight out of the [BepInPlugin(...)] attribute so the
# packaged zip always matches what the DLL reports at runtime.
$csContent = Get-Content -Raw -LiteralPath $csFile
if ($csContent -notmatch '\[BepInPlugin\(\s*"[^"]+"\s*,\s*"[^"]+"\s*,\s*"([^"]+)"\s*\)\]') {
    throw "Could not find version number in $csFile"
}
$version = $Matches[1]
Write-Host "Packaging LowLatencyMod v$version"

Write-Host "Building Release..."
dotnet build $csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$builtDll = Join-Path $root "bin\Release\netstandard2.1\LowLatencyMod.dll"
if (-not (Test-Path -LiteralPath $builtDll)) {
    throw "Built DLL not found at $builtDll"
}

$dist = Join-Path $root "dist"
$stageManual = Join-Path $dist "stage-manual"

if (Test-Path -LiteralPath $stageManual) { Remove-Item -LiteralPath $stageManual -Recurse -Force }

# BepInEx/plugins/LowLatencyMod/LowLatencyMod.dll
$manualPluginDir = Join-Path $stageManual "BepInEx\plugins\LowLatencyMod"
New-Item -ItemType Directory -Path $manualPluginDir -Force | Out-Null
Copy-Item -LiteralPath $builtDll -Destination $manualPluginDir -Force

$manualZip = Join-Path $dist "UNBEATABLE-Low-Latency-Mod-v$version-manual.zip"
New-ZipFromDirectory -SourceDir $stageManual -DestinationZip $manualZip

Write-Host ""
Write-Host "Done: $manualZip"
