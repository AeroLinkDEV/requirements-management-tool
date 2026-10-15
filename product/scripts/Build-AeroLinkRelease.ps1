#Requires -Version 5.1
<#
    .SYNOPSIS
        Builds the signed AeroLink release package from protected main (#1614 item 1.6).

    .DESCRIPTION
        Produces, in -OutputDirectory:

            AeroLink-<semver>+<sha>.zip       manifest.json, app\ (self-contained win-x64 API + client wwwroot), scripts\
            AeroLink-<semver>+<sha>.zip.sig   ECDSA P-256 / SHA-256 signature of the zip, IEEE P1363

        The semver is product/release/VERSION. The source SHA is HEAD, and a release is built only when HEAD
        equals the freshly fetched origin/main on a clean tree with no untracked files: an untracked .cs or .ts
        file can enter a build, so "clean" includes them. The posture is checked again after the build, so a step
        that rewrote a tracked file (npm ci changing package-lock.json, for example) refuses the package rather than
        shipping a tree that differs from the commit it names.

        Run it from a dedicated clone. `npm ci` replaces product/client/node_modules, which would disturb a dev
        server running from the same checkout.

        The signing key is a non-exportable CNG key in the builder's CurrentUser\My, created once with
        New-AeroLinkReleaseSigningCertificate (AeroLinkReleasePackage.psm1). The package is verified against that
        certificate's public half before the script reports success.

    .PARAMETER SkipPostureCheck
        DEVELOPMENT ONLY. Builds from whatever HEAD and working tree are present, and records
        sourcePosture 'unverified-development' in the signed manifest so the package can never pass for a release.
        Refused when a CI environment variable is set.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SigningCertificateThumbprint,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$SkipPostureCheck
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).ProviderPath
$productRoot = Join-Path $repositoryRoot 'product'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBootstrap.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkReleasePackage.psm1') -Force

function Invoke-ReleaseStep {
    param([string]$Name, [string]$FilePath, [string[]]$ArgumentList, [string]$WorkingDirectory)
    Write-Host "==> $Name"
    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $FilePath @ArgumentList
        if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }
}

function Assert-ReleasePosture {
    param($Posture, [string]$When)
    if (-not $Posture.IsGitRepository) { throw "Release refused ($When): $repositoryRoot is not a Git working tree." }
    if (-not $Posture.RemoteMainSha) { throw "Release refused ($When): origin/main is unknown in this clone." }
    if ($Posture.Relationship -ne 'Equal') {
        throw "Release refused ($When): HEAD $($Posture.ShortSha) is $($Posture.Relationship) relative to origin/main $($Posture.ShortRemoteMainSha). A release is built from protected main only."
    }
    if ($Posture.HasTrackedChanges) { throw "Release refused ($When): the working tree has tracked changes." }
    if ($Posture.UntrackedFileCount -gt 0) { throw "Release refused ($When): the working tree has $($Posture.UntrackedFileCount) untracked file(s), which a build could include." }
}

# Output never goes inside the repository: a package there would be an untracked file that refuses the next build.
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($output.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release refused: -OutputDirectory must be outside the repository ($repositoryRoot)."
}

if ($SkipPostureCheck) {
    foreach ($name in @('CI', 'GITHUB_ACTIONS', 'GITLAB_CI', 'TF_BUILD')) {
        if ([Environment]::GetEnvironmentVariable($name)) { throw "-SkipPostureCheck is development-only and is refused in CI ($name is set)." }
    }
    Write-Warning 'Posture check SKIPPED: this package is marked unverified-development and is not a release.'
    $posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $repositoryRoot
    if (-not $posture.IsGitRepository) { throw "Release refused: $repositoryRoot is not a Git working tree." }
    $sourcePosture = 'unverified-development'
}
else {
    if (-not (Sync-AeroLinkRemoteRefs -RepositoryRoot $repositoryRoot)) { throw 'Release refused: origin could not be fetched, so HEAD cannot be proved equal to the current protected main.' }
    $posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $repositoryRoot
    Assert-ReleasePosture -Posture $posture -When 'before build'
    $sourcePosture = 'origin-main'
}
$sourceSha = $posture.HeadSha

$version = ([IO.File]::ReadAllText((Join-Path $productRoot 'release\VERSION'))).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') { throw "product/release/VERSION holds '$version', which is not semantic versioning." }

$migrations = @(Get-ChildItem -LiteralPath (Join-Path $productRoot 'src\AeroLink.Infrastructure\Persistence\Migrations') -File -Filter '*.cs' |
    Where-Object { $_.Name -match '^\d{14}_\w+\.cs$' } | ForEach-Object { $_.BaseName } | Sort-Object)
if ($migrations.Count -eq 0) { throw 'No schema migrations were found; the manifest cannot name the schema this build expects.' }
$latestMigration = $migrations[-1]

# Fail on a missing or unusable key now, not after a ten-minute build.
$certificate = Get-AeroLinkReleaseSigningCertificate -Thumbprint $SigningCertificateThumbprint

New-Item -ItemType Directory -Path $output -Force | Out-Null
$staging = Join-Path $output ('.staging-' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $staging 'app'
$scripts = Join-Path $staging 'scripts'
$publicCertificate = Join-Path $output ('.verify-' + [Guid]::NewGuid().ToString('N') + '.cer')
$package = $null
try {
    Write-Host "AeroLink $version from $sourceSha ($sourcePosture), schema $latestMigration"

    # 1. The client, built exactly as the production launcher builds it.
    $client = Join-Path $productRoot 'client'
    Invoke-ReleaseStep -Name 'npm ci' -FilePath 'npm' -ArgumentList @('ci') -WorkingDirectory $client
    Invoke-ReleaseStep -Name 'npm run build' -FilePath 'npm' -ArgumentList @('run', 'build') -WorkingDirectory $client

    # 2. The API: self-contained, so the host needs no .NET runtime; not single-file, so wwwroot and the
    #    settings files stay ordinary files beside the executable.
    Invoke-ReleaseStep -Name 'dotnet publish' -FilePath 'dotnet' -WorkingDirectory $productRoot -ArgumentList @(
        'publish', (Join-Path $productRoot 'src\AeroLink.Api\AeroLink.Api.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false',
        "-p:Version=$version", "-p:SourceRevisionId=$sourceSha", '-o', $app, '--nologo')
    if (-not (Test-Path -LiteralPath (Join-Path $app 'AeroLink.Api.exe'))) { throw 'dotnet publish produced no AeroLink.Api.exe.' }
    # A Production package carries no Development settings (demo data, insecure cookies and webhooks).
    Remove-Item -LiteralPath (Join-Path $app 'appsettings.Development.json') -ErrorAction SilentlyContinue

    $wwwroot = Join-Path $app 'wwwroot'
    if (Test-Path -LiteralPath $wwwroot) { throw 'dotnet publish produced a wwwroot of its own; the package would mix two clients.' }
    Copy-Item -LiteralPath (Join-Path $client 'dist') -Destination $wwwroot -Recurse
    if (-not (Test-Path -LiteralPath (Join-Path $wwwroot 'index.html'))) { throw 'The client build produced no index.html.' }

    # The revision these binaries were built from, read by the API at startup (Runtime:SourceSha).
    $identity = '{ "Runtime": { "SourceSha": "' + $sourceSha + '" } }'
    [IO.File]::WriteAllText((Join-Path $app 'release-identity.json'), $identity, (New-Object Text.UTF8Encoding($false)))

    # 3. The server scripts: operator modules and commands, not their test suites and not this builder.
    New-Item -ItemType Directory -Path $scripts | Out-Null
    Get-ChildItem -LiteralPath $PSScriptRoot -File |
        Where-Object { $_.Extension -in @('.ps1', '.psm1', '.psd1', '.sql') -and $_.Name -notlike '*.Tests.ps1' -and $_.Name -notlike '*.Acceptance.ps1' -and $_.Name -ne 'Build-AeroLinkRelease.ps1' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $scripts }

    # 4-6. Manifest, zip, detached signature.
    Write-Host '==> manifest, zip and signature'
    $package = New-AeroLinkReleasePackage -StagingRoot $staging -OutputDirectory $output -Version $version -SourceSha $sourceSha `
        -LatestMigration $latestMigration -SigningCertificate $certificate -SourcePosture $sourcePosture

    # The package is not finished until the host's own verifier accepts it.
    [IO.File]::WriteAllBytes($publicCertificate, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    $verified = Test-AeroLinkReleasePackage -Package $package.Package -TrustedCertificate $publicCertificate

    $after = Get-AeroLinkRepositoryPosture -RepositoryRoot $repositoryRoot
    if ($after.HeadSha -ne $sourceSha) { throw "Release refused: HEAD moved from $sourceSha to $($after.HeadSha) during the build." }
    if (-not $SkipPostureCheck) { Assert-ReleasePosture -Posture $after -When 'after build' }

    $zip = Get-Item -LiteralPath $package.Package
    [pscustomobject]@{
        Package         = $zip.FullName
        Signature       = $package.Signature
        Version         = $verified.Version
        SourceSha       = $verified.SourceSha
        SourcePosture   = $verified.SourcePosture
        LatestMigration = $verified.LatestMigration
        Files           = $verified.FileCount
        SizeMB          = [Math]::Round($zip.Length / 1MB, 1)
        Signer          = $verified.Signer
    }
    $package = $null
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $publicCertificate -Force -ErrorAction SilentlyContinue
    # A package that was written but not proved is not left where it could be promoted.
    if ($package) {
        Remove-Item -LiteralPath $package.Package -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $package.Signature -Force -ErrorAction SilentlyContinue
    }
}
