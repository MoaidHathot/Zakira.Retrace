#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Packs and (optionally) pushes the Zakira.Retrace NuGet package to nuget.org.

.DESCRIPTION
    Builds and packs every packable project in Zakira.Retrace.slnx in the requested
    configuration (default: Release), placing the resulting .nupkg / .snupkg files into
    the output directory (default: ./artifacts).

    Only src/Zakira.Retrace is packable; every other project in the repository ships
    inside that single dotnet-tool package.

    When -Push is specified, every produced .nupkg is uploaded to nuget.org. The matching
    .snupkg (symbol package) is auto-pushed by `dotnet nuget push` when present alongside
    the .nupkg.

    The API key is taken from -ApiKey first, and from the NUGET_API_KEY environment
    variable when -ApiKey is omitted. The key is never printed.

.PARAMETER ApiKey
    NuGet.org API key. Falls back to $env:NUGET_API_KEY when not provided.
    Only required when -Push is set.

.PARAMETER Push
    Push every produced package to nuget.org after a successful pack.

.PARAMETER Configuration
    MSBuild configuration. Default: Release.

.PARAMETER Output
    Output directory for the produced packages, relative to the script root.
    Default: artifacts.

.PARAMETER Source
    NuGet source to push to. Defaults to nuget.org's package endpoint rather than its
    v3 service index, because `api.nuget.org` is blocked at the TLS layer on some
    networks this is run from while `www.nuget.org` is reachable. The endpoint is a
    valid push target everywhere, so this costs nothing on an unrestricted network.

.PARAMETER SkipDuplicate
    Pass --skip-duplicate to `dotnet nuget push`. Default: $true (re-runs do not fail
    when the version is already on nuget.org).

.PARAMETER LocalFeed
    Publish the produced packages into the machine-wide private folder feed after packing,
    so `dotnet tool install -g Zakira.Retrace` resolves them without a nuget.org round
    trip. Uses `dotnet nuget push` against the folder, which lays the package out as
    <id>/<version>/ with its .nuspec and .sha512, matching the other packages in the feed.
    Independent of -Push.

.PARAMETER LocalFeedPath
    Folder feed to copy into when -LocalFeed is set. Default: C:\nuget\local-feed,
    which is the `local` source registered in the user-level NuGet.Config.

.EXAMPLE
    ./pack.ps1

.EXAMPLE
    ./pack.ps1 -LocalFeed

.EXAMPLE
    ./pack.ps1 -Push
#>
[CmdletBinding()]
param(
    [string] $ApiKey,
    [switch] $Push,
    [string] $Configuration = 'Release',
    [string] $Output = 'artifacts',
    [string] $Source = 'https://www.nuget.org/api/v2/package',
    [bool]   $SkipDuplicate = $true,
    [switch] $LocalFeed,
    [string] $LocalFeedPath = 'C:\nuget\local-feed'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$solution = Join-Path $root 'Zakira.Retrace.slnx'
$outputPath = Join-Path $root $Output

if (-not (Test-Path -LiteralPath $solution)) {
    throw "Solution not found: $solution"
}

if ($Push) {
    if (-not $ApiKey) { $ApiKey = $env:NUGET_API_KEY }
    if (-not $ApiKey) {
        throw 'An API key is required to push. Pass -ApiKey or set the NUGET_API_KEY environment variable.'
    }
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $outputPath | Out-Null

Write-Host "==> Restoring $solution" -ForegroundColor Cyan
& dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

Write-Host "==> Building ($Configuration)" -ForegroundColor Cyan
& dotnet build $solution --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

Write-Host "==> Testing ($Configuration)" -ForegroundColor Cyan
& dotnet test $solution --configuration $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }

Write-Host "==> Packing to $outputPath" -ForegroundColor Cyan
& dotnet pack $solution --configuration $Configuration --no-build --output $outputPath
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed with exit code $LASTEXITCODE." }

$packages = @(Get-ChildItem -LiteralPath $outputPath -Filter '*.nupkg' -File)
if ($packages.Count -eq 0) {
    throw "No packages were produced in $outputPath."
}

Write-Host ''
Write-Host 'Produced packages:' -ForegroundColor Green
foreach ($package in $packages) {
    Write-Host ("  {0}  ({1:N1} MB)" -f $package.Name, ($package.Length / 1MB))
}

if ($LocalFeed) {
    Write-Host ''
    Write-Host "==> Publishing to local feed $LocalFeedPath" -ForegroundColor Cyan

    if (-not (Test-Path -LiteralPath $LocalFeedPath)) {
        New-Item -ItemType Directory -Path $LocalFeedPath -Force | Out-Null
    }

    foreach ($package in $packages) {
        # Push rather than copy. The feed uses the hierarchical layout NuGet expands a push
        # into — <id>/<version>/<id>.<version>.nupkg plus the extracted .nuspec and a .sha512 —
        # which is what every other package there looks like and what the client resolves
        # fastest. A bare file dropped in the root is a different, flat feed format; mixing
        # the two makes restore see one and not the other.
        #
        # --skip-duplicate keeps a re-run of the same version from failing; a folder feed has
        # no notion of overwriting, so bump the version to publish a changed build.
        # --no-symbols because the folder has nowhere meaningful to put a .snupkg.
        & dotnet nuget push $package.FullName --source $LocalFeedPath --skip-duplicate --no-symbols
        if ($LASTEXITCODE -ne 0) { throw "dotnet nuget push to the local feed failed with exit code $LASTEXITCODE." }
        Write-Host "  $($package.Name)"
    }

    Write-Host ''
    Write-Host "Install with: dotnet tool install -g Zakira.Retrace --source $LocalFeedPath" -ForegroundColor Green
}

if (-not $Push) {
    Write-Host ''
    Write-Host 'Pack complete. Re-run with -Push to publish.' -ForegroundColor Yellow
    return
}

foreach ($package in $packages) {
    Write-Host ''
    Write-Host "==> Pushing $($package.Name)" -ForegroundColor Cyan
    $pushArgs = @('nuget', 'push', $package.FullName, '--source', $Source, '--api-key', $ApiKey)
    if ($SkipDuplicate) { $pushArgs += '--skip-duplicate' }
    & dotnet @pushArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet nuget push failed with exit code $LASTEXITCODE." }
}

Write-Host ''
Write-Host 'Push complete.' -ForegroundColor Green
