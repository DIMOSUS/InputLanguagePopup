<#
.SYNOPSIS
    Publishes InputLanguagePopup as a Native AOT x64 executable.

.DESCRIPTION
    Produces a single native .exe (~3 MB) that runs on Windows 10/11 x64 without a
    pre-installed .NET runtime. Native AOT requires the MSVC linker, i.e. Visual
    Studio (or the Build Tools) with the "Desktop development with C++" workload.

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

# The AOT toolchain locates the MSVC linker with vswhere.exe; make sure it is
# reachable even when the shell's PATH does not include the VS Installer folder.
$vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path (Join-Path $vsInstaller "vswhere.exe")) -and ($env:PATH -notlike "*$vsInstaller*")) {
    $env:PATH = "$vsInstaller;$env:PATH"
}

Write-Host "Publishing (Native AOT) $project ..." -ForegroundColor Cyan

$publishArgs = @(
    $project,
    "-c", $Configuration,
    "-r", "win-x64",
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
    Write-Host ""
    Write-Host "Verify the build with:  $exe --selftest" -ForegroundColor Cyan
    Write-Host "  (writes %LocalAppData%\InputLanguagePopup\selftest.log)"
}
