<#
.SYNOPSIS
    Publishes InputLanguagePopup as a self-contained, single-file x64 executable.

.DESCRIPTION
    Produces a single .exe that runs on Windows 10/11 x64 without a pre-installed
    .NET runtime. Native libraries are bundled and extracted on first run.

.PARAMETER Configuration
    Build configuration (default: Release).

.PARAMETER Output
    Output directory (default: .\publish).

.PARAMETER Version
    Optional version to stamp into the assembly (e.g. "1.2.3"). Used by the
    release workflow, which derives it from the git tag.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Output C:\Tools\InputLanguagePopup
    .\publish.ps1 -Version 1.2.3
#>
param(
    [string]$Configuration = "Release",
    [string]$Output = "$PSScriptRoot\publish",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\InputLanguagePopup\InputLanguagePopup.csproj"

Write-Host "Publishing $project ..." -ForegroundColor Cyan

$publishArgs = @(
    $project,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-o", $Output
)

if ($Version) {
    $publishArgs += "-p:Version=$Version"
}

dotnet publish @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $Output "InputLanguagePopup.exe"
Write-Host ""
Write-Host "Done. Executable:" -ForegroundColor Green
Write-Host "  $exe"
if (Test-Path $exe) {
    $sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "  Size: $sizeMb MB"
}
