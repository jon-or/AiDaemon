<#
.SYNOPSIS
    Publishes AiDaemon as a single-file self-contained Windows x64 binary.

.DESCRIPTION
    Produces a redistributable folder under <repo>\publish\ containing:
      AiDaemon.exe                  -- the daemon binary (single-file, self-contained)
      appsettings.json              -- committed config
      appsettings.Local.json        -- local-only config (ntfy topic etc.); only if it exists

    appsettings.Development.json is deliberately excluded from publish/ -- under the
    Windows Service DOTNET_ENVIRONMENT is never set, so its 10s poll interval and Debug
    logging would never apply, and shipping it risks accidental dev settings on a prod box.

    Self-contained means the .NET 10 runtime is bundled, so the target machine doesn't
    need a separate `dotnet` install. PublishReadyToRun pre-JITs the code to trim
    cold-start latency on service restart.

    Kept ASCII-only by design -- see install.ps1's NOTES section for why.

.PARAMETER OutDir
    Where to place the published artefacts. Defaults to <repo>\publish.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.EXAMPLE
    .\scripts\publish.ps1
    # produces .\publish\AiDaemon.exe + config files

.EXAMPLE
    .\scripts\publish.ps1 -OutDir C:\Tools\AiDaemon
    # publishes directly into the install location (stop the service first)
#>
[CmdletBinding()]
param(
    [string] $OutDir = (Join-Path $PSScriptRoot '..\publish'),
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $repoRoot 'src\AiDaemon\AiDaemon.csproj'

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

# Resolve to an absolute path so dotnet publish's -o lands where the operator expects
# even if they ran the script from outside the repo.
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

Write-Host "Publishing AiDaemon -> $OutDir ($Configuration, win-x64, self-contained)" -ForegroundColor Cyan

# /p:PublishSingleFile=true bundles the managed assemblies into AiDaemon.exe.
# /p:DebugType=embedded keeps stack-trace line numbers inside the binary so production
# crashes are useful without a separate .pdb deploy.
& dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:PublishReadyToRun=true `
    /p:DebugType=embedded `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit $LASTEXITCODE"
}

Write-Host ""
Write-Host "Published artefacts:" -ForegroundColor Green
Get-ChildItem $OutDir -File | Format-Table Name, Length, LastWriteTime

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. (first install) .\scripts\install.ps1 -BinDir '$OutDir'"
Write-Host "  2. (subsequent)    sc.exe stop AiDaemon; copy publish\* to install dir; sc.exe start AiDaemon"
