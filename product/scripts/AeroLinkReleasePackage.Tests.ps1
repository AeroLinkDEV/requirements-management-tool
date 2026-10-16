#Requires -Version 5.1
<#
    Contract coverage for the signed release package (#1614 item 1.6).

    The host installs only what this verifier accepts, so its refusals are the contract: a package altered after
    signing, a package whose contents disagree with its signed manifest, a package signed by a key the host does
    not trust, and a package with no signature are all refused, and a refused extraction leaves nothing behind.

    Each run makes its own ephemeral ECDSA P-256 key in memory. No certificate store, network, build, database or
    persistent AeroLink state is touched.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkReleasePackage.psm1') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem

$failures = [System.Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -match $Pattern) { return }
        $script:failures.Add("$Message (threw, but not matching '$Pattern': $($_.Exception.Message))")
        return
    }
    $script:failures.Add("$Message (nothing was thrown)")
}

function New-EphemeralSigningCertificate([string]$Name) {
    $key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest("CN=$Name", $key, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    return $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
}
function Export-PublicCertificate($Certificate, [string]$Path) {
    [System.IO.File]::WriteAllBytes($Path, $Certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    return $Path
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('aerolink-release-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $trusted = New-EphemeralSigningCertificate 'AeroLink Release Signing Test'
    $other = New-EphemeralSigningCertificate 'Somebody Else'
    $trustedCer = Export-PublicCertificate $trusted (Join-Path $root 'trusted.cer')
    $sha = 'a1b2c3d4e5f60718293a4b5c6d7e8f9012345678'

    function New-Package([string]$Name, $Signer, [string]$Posture = 'origin-main') {
        $staging = Join-Path $root "$Name-staging"
        New-Item -ItemType Directory -Path (Join-Path $staging 'app\wwwroot') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $staging 'scripts') -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $staging 'app\AeroLink.Api.dll'), 'api')
        [System.IO.File]::WriteAllText((Join-Path $staging 'app\wwwroot\index.html'), '<html></html>')
        [System.IO.File]::WriteAllText((Join-Path $staging 'scripts\Backup-AeroLink.ps1'), 'backup')
        return New-AeroLinkReleasePackage -StagingRoot $staging -OutputDirectory (Join-Path $root "$Name-out") `
            -Version '1.0.0' -SourceSha $sha -LatestMigration '20261005112545_NativeNotificationProcessIdentity' -SigningCertificate $Signer -SourcePosture $Posture
    }

    # Round trip: what the builder writes, the host accepts, and the installed tree is exactly the signed tree.
    $good = New-Package 'good' $trusted
    Assert-True ($good.Package -like '*AeroLink-1.0.0+a1b2c3d4e5f60718293a4b5c6d7e8f9012345678.zip') "The package is named AeroLink-<semver>+<sha>.zip (was $($good.Package))."
    $install = Join-Path $root 'installed'
    $verified = Test-AeroLinkReleasePackage -Package $good.Package -TrustedCertificate $trustedCer -Destination $install
    Assert-True ($verified.Version -eq '1.0.0' -and $verified.SourceSha -eq $sha) 'A verified package reports the signed version and source SHA.'
    Assert-True ($verified.LatestMigration -eq '20261005112545_NativeNotificationProcessIdentity') 'A verified package reports the signed latest migration.'
    Assert-True ($verified.FileCount -eq 3) "The manifest covers every shipped file (listed $($verified.FileCount))."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $install 'app\wwwroot\index.html')) -eq '<html></html>') 'The verified tree is extracted to the destination.'

    # A byte changed after signing: refused before anything is extracted.
    $bytes = [System.IO.File]::ReadAllBytes($good.Package)
    $bytes[[int]($bytes.Length / 2)] = $bytes[[int]($bytes.Length / 2)] -bxor 0xFF
    $tamperedZip = Join-Path $root 'tampered\AeroLink-1.0.0.zip'
    New-Item -ItemType Directory -Path (Split-Path $tamperedZip) | Out-Null
    [System.IO.File]::WriteAllBytes($tamperedZip, $bytes)
    Copy-Item -LiteralPath $good.Signature -Destination "$tamperedZip.sig"
    $tamperedTarget = Join-Path $root 'tampered-install'
    Assert-Throws { Test-AeroLinkReleasePackage -Package $tamperedZip -TrustedCertificate $trustedCer -Destination $tamperedTarget } 'signature does not verify' `
        'A package altered after signing must be refused.'
    Assert-True (-not (Test-Path -LiteralPath $tamperedTarget)) 'A package refused on its signature must extract nothing.'

    # A file changed against its signed manifest, re-signed by the trusted key: the signature passes and the
    # manifest hash is what refuses. This is the only check standing between a broken build and the host.
    $repack = Join-Path $root 'repack'
    [System.IO.Compression.ZipFile]::ExtractToDirectory($good.Package, $repack)
    [System.IO.File]::WriteAllText((Join-Path $repack 'app\AeroLink.Api.dll'), 'api, but changed')
    $changedZip = Join-Path $root 'changed\AeroLink-1.0.0.zip'
    New-Item -ItemType Directory -Path (Split-Path $changedZip) | Out-Null
    [System.IO.Compression.ZipFile]::CreateFromDirectory($repack, $changedZip)
    $signingKey = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($trusted)
    [System.IO.File]::WriteAllBytes("$changedZip.sig", $signingKey.SignData([System.IO.File]::ReadAllBytes($changedZip), [System.Security.Cryptography.HashAlgorithmName]::SHA256))
    $changedTarget = Join-Path $root 'changed-install'
    Assert-Throws { Test-AeroLinkReleasePackage -Package $changedZip -TrustedCertificate $trustedCer -Destination $changedTarget } "'app/AeroLink.Api.dll' does not match its manifest hash" `
        'A signed package whose file disagrees with its manifest must be refused.'
    Assert-True (-not (Test-Path -LiteralPath $changedTarget)) 'A package refused after extraction must leave no partial tree behind.'

    # Signed by a key the host does not trust.
    $foreign = New-Package 'foreign' $other
    Assert-Throws { Test-AeroLinkReleasePackage -Package $foreign.Package -TrustedCertificate $trustedCer } 'signature does not verify' `
        'A package signed by an untrusted key must be refused.'

    # Signed by the trusted key but built with -SkipPostureCheck: a signature proves who built it, not from what.
    $development = New-Package 'development' $trusted 'unverified-development'
    $developmentTarget = Join-Path $root 'development-install'
    Assert-Throws { Test-AeroLinkReleasePackage -Package $development.Package -TrustedCertificate $trustedCer -Destination $developmentTarget } "source posture is 'unverified-development'" `
        'A package not built from protected main must be refused by default.'
    Assert-True (-not (Test-Path -LiteralPath $developmentTarget)) 'A package refused on its posture must leave no tree behind.'
    $allowed = Test-AeroLinkReleasePackage -Package $development.Package -TrustedCertificate $trustedCer -AllowUnverifiedDevelopment
    Assert-True ($allowed.SourcePosture -eq 'unverified-development') 'A development installation may accept an unverified package only when it asks to.'

    # The builder's runtime guard: a publish that ships the vulnerable Pkcs (10.0.9's 10.0.926.x) is refused by
    # name, and a fixed one (10.0.13's 10.0.1326.x) passes with the bundled frameworks read for the manifest.
    function New-PublishedApp([string]$Name, [string]$PkcsFileVersion, [string]$NetCoreVersion = '10.0.13', [string]$AspNetVersion = '10.0.13') {
        $app = Join-Path $root "$Name-app"
        New-Item -ItemType Directory -Path $app | Out-Null
        $type = 'PkcsStand' + [Guid]::NewGuid().ToString('N')
        Add-Type -TypeDefinition "[assembly: System.Reflection.AssemblyFileVersion(`"$PkcsFileVersion`")] public class $type { }" `
            -OutputAssembly (Join-Path $app 'System.Security.Cryptography.Pkcs.dll') -OutputType Library
        [System.IO.File]::WriteAllText((Join-Path $app 'AeroLink.Api.runtimeconfig.json'),
            ('{ "runtimeOptions": { "tfm": "net10.0", "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "' + $NetCoreVersion + '" }, { "name": "Microsoft.AspNetCore.App", "version": "' + $AspNetVersion + '" } ] } }'))
        return $app
    }
    Assert-Throws { Assert-AeroLinkReleaseRuntime -AppRoot (New-PublishedApp 'vulnerable' '10.0.926.27113') } 'Pkcs\.dll is file version 10\.0\.926\.27113 \(servicing 10\.0\.9\)\. 10\.0\.13 or later is required' `
        'A publish that ships a vulnerable System.Security.Cryptography.Pkcs must be refused, naming the version found.'
    $fixedRuntime = Assert-AeroLinkReleaseRuntime -AppRoot (New-PublishedApp 'fixed' '10.0.1326.50504')
    Assert-True (($fixedRuntime.Frameworks -join ';') -eq 'Microsoft.NETCore.App 10.0.13;Microsoft.AspNetCore.App 10.0.13') "The bundled runtime is read from the publish (was '$($fixedRuntime.Frameworks -join ';')')."
    # A package pin can fix Pkcs while the self-contained runtime remains old. Both frameworks independently
    # have to meet the documented release minimum; the other framework remains current in each row.
    Assert-Throws { Assert-AeroLinkReleaseRuntime -AppRoot (New-PublishedApp 'old-netcore' '10.0.1326.50504' '10.0.9') } 'Microsoft.NETCore.App.*10\.0\.9.*10\.0\.13 or later is required' `
        'A patched Pkcs must not hide an outdated bundled .NET runtime.'
    Assert-Throws { Assert-AeroLinkReleaseRuntime -AppRoot (New-PublishedApp 'old-aspnet' '10.0.1326.50504' '10.0.13' '10.0.9') } 'Microsoft.AspNetCore.App.*10\.0\.9.*10\.0\.13 or later is required' `
        'A patched Pkcs must not hide an outdated bundled ASP.NET Core runtime.'

    # No signature at all.
    Remove-Item -LiteralPath $good.Signature
    Assert-Throws { Test-AeroLinkReleasePackage -Package $good.Package -TrustedCertificate $trustedCer } 'no detached signature' `
        'An unsigned package must be refused.'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "AeroLink release-package contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}
Write-Host 'AeroLink release-package contract passed.' -ForegroundColor Green
exit 0
