<#
.SYNOPSIS
    Publish LuaTools Amethyst as a self-contained single-file win-x64 executable.

.DESCRIPTION
    Replaces the hand-pasted `dotnet publish` invocation. Same switches, but the repository root is
    resolved from this script's own location instead of being hardcoded to one machine's drive letter, and
    the resulting artifact's path is printed rather than left for the caller to reconstruct.

    This produces the BINARY only. Packaging it into an installer is a separate `vpk pack` step and is
    deliberately not done here — see README, "Building".

.PARAMETER RepoRoot
    Repository root. Defaults to this script's parent directory, so the script works from any working
    directory and on any checkout location.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER Runtime
    Runtime identifier. Defaults to win-x64 (the only RID the app ships for; it is Windows-only WPF).

.EXAMPLE
    .\scripts\build-release.ps1

.EXAMPLE
    .\scripts\build-release.ps1 -RepoRoot D:\src\LuaTools_Amethyst
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64'
)

# Any failure below should stop the script and surface as a non-zero exit code, not scroll past.
$ErrorActionPreference = 'Stop'

function Fail([string] $Message) {
    Write-Error $Message
    exit 1
}

if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    Fail "Repository root not found: $RepoRoot"
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

$project = Join-Path $RepoRoot 'src\LuaToolsGui\LuaToolsGui.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    Fail "Project not found: $project (is -RepoRoot pointing at the repository root?)"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'The .NET SDK is not on PATH. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0'
}

# Read the TFM out of the project rather than hardcoding it, so the published path stays correct if the
# target framework is ever moved. Falls back to a literal only if the property cannot be read.
$targetFramework = 'net8.0-windows'
try {
    $projectXml = [xml](Get-Content -LiteralPath $project -Raw)
    $declared = $projectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1
    if ($declared) { $targetFramework = $declared }
} catch {
    Write-Warning "Could not read TargetFramework from the project; assuming $targetFramework."
}

Write-Host "Repository : $RepoRoot"
Write-Host "Project    : $project"
Write-Host "Config     : $Configuration / $Runtime / $targetFramework"
Write-Host ''

# Exactly the switches the manual command used.
& dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishDir = Join-Path $RepoRoot "src\LuaToolsGui\bin\$Configuration\$targetFramework\$Runtime\publish"
# AssemblyName is LuaTools, not LuaToolsGui — the technical identity is deliberately not renamed
# (see LuaToolsGui.csproj), so the produced executable is LuaTools.exe.
$artifact = Join-Path $publishDir 'LuaTools.exe'

if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
    Fail "Publish reported success but $artifact is missing. Check the output above."
}

$sizeMb = [math]::Round((Get-Item -LiteralPath $artifact).Length / 1MB, 1)

Write-Host ''
Write-Host 'Publish succeeded.' -ForegroundColor Green
Write-Host "Artifact : $artifact"
Write-Host "Size     : $sizeMb MB"
Write-Host "Folder   : $publishDir"
exit 0
