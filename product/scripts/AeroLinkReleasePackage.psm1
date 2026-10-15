#Requires -Version 5.1
<#
    The signed AeroLink release package (#1614 item 1.6).

    A release is one zip, AeroLink-<semver>+<sha>.zip, holding:

        manifest.json   version, source SHA, latest schema migration, and the SHA-256 of every other file
        app\            the self-contained `dotnet publish` of AeroLink.Api, with the built client in app\wwwroot
        scripts\        the server scripts

    and a detached signature beside it, <zip>.sig: ECDSA P-256 over SHA-256 of the exact zip bytes, in IEEE P1363
    form (r || s, 64 bytes), which is what .NET's ECDsa.SignData produces on every runtime.

    The order of verification is the security property:

      1. The signature is checked over the zip BEFORE anything is extracted. An unsigned, re-signed or altered
         package is refused while it is still an opaque file.
      2. The bytes that were verified are the bytes that are extracted. The package is read into memory once,
         so a file swapped on disk between the check and the extraction cannot be what gets installed.
      3. After extraction every file is hashed against the manifest, and a file the manifest does not name is a
         refusal too, so the installed tree is exactly the signed tree.

    The host trusts only the certificate path its installer gives it. A certificate inside the package proves
    nothing (anyone can sign a package with a key they made and ship that key's certificate beside it), so the
    package never carries one and verification never looks for one.

    This module must run under Windows PowerShell 5.1 on a host with no SDK. It is ASCII-only on purpose.
#>

Set-StrictMode -Version Latest

$script:AeroLinkReleaseManifestName = 'manifest.json'
$script:AeroLinkReleaseManifestFormat = 1
$script:AeroLinkReleaseSemVer = '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-AeroLinkReleaseFileSha256 {
    param([Parameter(Mandatory)][string]$LiteralPath)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($LiteralPath)
    try { $hash = $sha.ComputeHash($stream) }
    finally { $stream.Dispose(); $sha.Dispose() }
    return (($hash | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Get-AeroLinkReleaseRelativeFiles {
    # Every file under Root as a forward-slash relative path, in ordinal order so the manifest is stable.
    param([Parameter(Mandatory)][string]$Root)
    $full = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | ForEach-Object {
        $_.FullName.Substring($full.Length).Replace('\', '/')
    })
    [System.Array]::Sort($files, [System.StringComparer]::Ordinal)
    return ,$files
}

function Get-AeroLinkReleaseSigningKey {
    # The ECDSA P-256 key of a certificate, public or private. Anything else is refused by name.
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [switch]$Private
    )
    $key = if ($Private) {
        if (-not $Certificate.HasPrivateKey) { throw "The signing certificate $($Certificate.Thumbprint) has no private key in this store." }
        [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($Certificate)
    } else {
        [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPublicKey($Certificate)
    }
    if ($null -eq $key) { throw "Certificate $($Certificate.Thumbprint) does not carry an ECDSA key. AeroLink releases are signed with ECDSA P-256 only." }
    if ($key.KeySize -ne 256) { throw "Certificate $($Certificate.Thumbprint) carries a $($key.KeySize)-bit ECDSA key. AeroLink releases are signed with ECDSA P-256 only." }
    return $key
}

function New-AeroLinkReleaseSigningCertificate {
    <#
        .SYNOPSIS Creates the release signing key once: a non-exportable CNG ECDSA P-256 key behind a self-signed
        certificate in CurrentUser\My, and exports its public certificate.
        .DESCRIPTION
            The private key never leaves the builder's key store; only the .cer is distributed to hosts. Run it
            once per signing key. It refuses when a certificate with the same subject already exists in the store,
            so a second key is never created by accident beside the one hosts already trust.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CertificateOutputPath,
        [string]$Subject = 'CN=AeroLink Release Signing',
        [int]$ValidYears = 10
    )
    # Resolved against the PowerShell location ($PWD), as the operator typed it, never the process directory.
    $certificatePath = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($CertificateOutputPath)
    if (Test-Path -LiteralPath $certificatePath) { throw "Refusing to overwrite $certificatePath." }
    # Checked before the key exists: a key created and then not exported is stranded in the store.
    $certificateFolder = Split-Path -Parent $certificatePath
    if (-not (Test-Path -LiteralPath $certificateFolder -PathType Container)) { throw "The folder for $certificatePath does not exist. No key was created." }
    $existing =@(Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My' | Where-Object { $_.Subject -eq $Subject })
    if ($existing.Count -gt 0) {
        throw "CurrentUser\My already holds '$Subject' ($(($existing | ForEach-Object { $_.Thumbprint }) -join ', ')). Use that key, or remove it deliberately first."
    }
    $certificate = New-SelfSignedCertificate -Subject $Subject -FriendlyName 'AeroLink Release Signing' `
        -KeyAlgorithm ECDSA_nistP256 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
        -Provider 'Microsoft Software Key Storage Provider' -KeyUsage DigitalSignature -Type CodeSigningCert `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears($ValidYears)
    [System.IO.File]::WriteAllBytes($certificatePath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    return [pscustomobject]@{ Thumbprint = $certificate.Thumbprint; Subject = $certificate.Subject; CertificatePath = $certificatePath; NotAfter = $certificate.NotAfter }
}

function Get-AeroLinkReleaseSigningCertificate {
    <# .SYNOPSIS Finds the signing certificate, with its private key, in CurrentUser\My by thumbprint. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Thumbprint)
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'CurrentUser')
    $store.Open('ReadOnly')
    try {
        $found = $store.Certificates.Find('FindByThumbprint', ($Thumbprint -replace '\s', ''), $false)
        if ($found.Count -ne 1) { throw "No certificate with thumbprint $Thumbprint is in CurrentUser\My." }
        $certificate = $found[0]
    }
    finally { $store.Close() }
    $null = Get-AeroLinkReleaseSigningKey -Certificate $certificate -Private
    return $certificate
}

function Assert-AeroLinkReleaseRuntime {
    <#
        .SYNOPSIS Reads the runtime a self-contained publish bundles, and refuses one that ships a known-vulnerable
        System.Security.Cryptography.Pkcs.
        .DESCRIPTION
            A self-contained publish ships whichever Pkcs copy wins conflict resolution: the pinned 10.0.13 package
            (#1637) or the shared framework's copy from the builder's SDK. Versions 10.0.0 to 10.0.12 are affected by
            GHSA-846f-f764-76mx and GHSA-2fmv-rv2c-59x7, so a release is refused unless the file actually published
            is 10.0.13 or later. The file version encodes the servicing patch as build / 100 (10.0.926.x is 10.0.9,
            10.0.1326.x is 10.0.13). A missing file is a refusal too: the ASP.NET Core framework always carries it,
            so its absence means the guard is no longer looking at what ships.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$AppRoot)
    $AppRoot = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($AppRoot)
    $pkcs = Join-Path $AppRoot 'System.Security.Cryptography.Pkcs.dll'
    if (-not (Test-Path -LiteralPath $pkcs -PathType Leaf)) { throw "Release refused: $pkcs was not published, so the System.Security.Cryptography.Pkcs version cannot be checked." }
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($pkcs)
    $found = New-Object System.Version($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
    $servicing = "$($info.FileMajorPart).$($info.FileMinorPart).$([Math]::Floor($info.FileBuildPart / 100))"
    if ($found -lt [System.Version]'10.0.1300.0') {
        throw "Release refused: the published System.Security.Cryptography.Pkcs.dll is file version $found (servicing $servicing). 10.0.13 or later is required (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7)."
    }

    $frameworks = @()
    $runtimeConfig = @(Get-ChildItem -LiteralPath $AppRoot -File -Filter '*.runtimeconfig.json')
    if ($runtimeConfig.Count -ne 1) { throw "Release refused: expected one runtimeconfig.json in $AppRoot, found $($runtimeConfig.Count)." }
    $options = ([System.IO.File]::ReadAllText($runtimeConfig[0].FullName) | ConvertFrom-Json).runtimeOptions
    if ($options.PSObject.Properties['includedFrameworks']) {
        $frameworks = @($options.includedFrameworks | ForEach-Object { "$($_.name) $($_.version)" })
    }
    if ($frameworks.Count -eq 0) { throw "Release refused: $($runtimeConfig[0].Name) lists no bundled frameworks; the publish is not self-contained." }
    return [pscustomobject]@{ Frameworks = $frameworks; PkcsFileVersion = $found.ToString() }
}

function New-AeroLinkReleasePackage {
    <#
        .SYNOPSIS Writes manifest.json into a staged release tree, zips it, and writes the detached signature.
        .DESCRIPTION
            StagingRoot must hold exactly the files to ship (app\, scripts\) and no manifest yet. Returns the zip
            and signature paths. The zip is named AeroLink-<Version>+<SourceSha>.zip.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StagingRoot,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$SourceSha,
        [Parameter(Mandatory)][string]$LatestMigration,
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$SigningCertificate,
        # Recorded, never inferred: 'origin-main' only when the builder proved HEAD equals the fetched origin/main
        # on a clean tree. A development build says so in its own signed manifest.
        [ValidateSet('origin-main', 'unverified-development')][string]$SourcePosture = 'origin-main',
        # The .NET frameworks a self-contained publish bundles, e.g. 'Microsoft.AspNetCore.App 10.0.9'
        # (from Assert-AeroLinkReleaseRuntime), recorded so a host can see which runtime it is installing.
        [string[]]$BundledRuntime = @()
    )
    if ($Version -notmatch $script:AeroLinkReleaseSemVer) { throw "Release version '$Version' is not semantic versioning (major.minor.patch[-prerelease])." }
    if ($SourceSha -notmatch '^[0-9a-f]{40}$') { throw "Source SHA '$SourceSha' is not a full lower-case commit SHA." }
    $StagingRoot = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($StagingRoot)
    $OutputDirectory = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    $manifestPath = Join-Path $StagingRoot $script:AeroLinkReleaseManifestName
    if (Test-Path -LiteralPath $manifestPath) { throw "The staging tree already holds $($script:AeroLinkReleaseManifestName)." }
    $signingKey = Get-AeroLinkReleaseSigningKey -Certificate $SigningCertificate -Private

    $relative = Get-AeroLinkReleaseRelativeFiles -Root $StagingRoot
    if ($relative.Count -eq 0) { throw "The staging tree $StagingRoot is empty." }
    $entries = foreach ($path in $relative) {
        $file = Join-Path $StagingRoot ($path.Replace('/', '\'))
        [ordered]@{ path = $path; sha256 = (Get-AeroLinkReleaseFileSha256 -LiteralPath $file); length = (Get-Item -LiteralPath $file).Length }
    }
    $manifest = [ordered]@{
        formatVersion   = $script:AeroLinkReleaseManifestFormat
        product         = 'AeroLink'
        version         = $Version
        sourceSha       = $SourceSha
        sourcePosture   = $SourcePosture
        latestMigration = $LatestMigration
        bundledRuntime  = @($BundledRuntime)
        builtAtUtc     = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        files           = @($entries)
    }
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $zipPath = Join-Path $OutputDirectory "AeroLink-$Version+$SourceSha.zip"
    $signaturePath = "$zipPath.sig"
    foreach ($existing in @($zipPath, $signaturePath)) {
        if (Test-Path -LiteralPath $existing) { throw "Refusing to overwrite $existing." }
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($StagingRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $stream = [System.IO.File]::OpenRead($zipPath)
    try { $signature = $signingKey.SignData($stream, [System.Security.Cryptography.HashAlgorithmName]::SHA256) }
    finally { $stream.Dispose() }
    if ($signature.Length -ne 64) { throw "The signing key produced a $($signature.Length)-byte signature; IEEE P1363 P-256 is 64 bytes." }
    [System.IO.File]::WriteAllBytes($signaturePath, $signature)
    return [pscustomobject]@{ Package = $zipPath; Signature = $signaturePath; FileCount = $relative.Count; Version = $Version; SourceSha = $SourceSha }
}

function Test-AeroLinkReleasePackage {
    <#
        .SYNOPSIS Verifies a release package against the certificate the installer trusts, then extracts it and
        verifies every file against the signed manifest.
        .DESCRIPTION
            -TrustedCertificate is a path the installer provides (the committed release-signing.cer). Nothing in
            the package is trusted until its signature verifies against that certificate.

            With -Destination the verified tree is left there for installation; the directory must not exist yet,
            and it is removed again on any refusal. Without it the package is extracted to a temporary directory
            that is always removed, which makes this a pure check. Any refusal throws.

            A package whose signed manifest does not record sourcePosture 'origin-main' (a -SkipPostureCheck build)
            is refused unless -AllowUnverifiedDevelopment is passed. Signing proves who built a package, not what it
            was built from, so the posture is checked here rather than trusted to the file name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Package,
        [Parameter(Mandatory)][string]$TrustedCertificate,
        [string]$Destination,
        [switch]$AllowUnverifiedDevelopment
    )
    $packagePath = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($Package)
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw "Release package not found: $packagePath" }
    $signaturePath = "$packagePath.sig"
    if (-not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) { throw "Release package refused: no detached signature at $signaturePath. Unsigned packages are never installed." }
    $certificatePath = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($TrustedCertificate)
    if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) { throw "Trusted release certificate not found: $certificatePath" }

    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(, [System.IO.File]::ReadAllBytes($certificatePath))
    $publicKey = Get-AeroLinkReleaseSigningKey -Certificate $certificate
    $signature = [System.IO.File]::ReadAllBytes($signaturePath)
    if ($signature.Length -ne 64) { throw "Release package refused: the signature is $($signature.Length) bytes, not a 64-byte ECDSA P-256 IEEE P1363 signature." }

    # One read. The verified bytes are the extracted bytes (see the module header).
    $bytes = [System.IO.File]::ReadAllBytes($packagePath)
    if (-not $publicKey.VerifyData($bytes, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) {
        throw "Release package refused: the signature does not verify against the trusted certificate $($certificate.Thumbprint). The package was altered or signed by another key."
    }

    $temporary = [string]::IsNullOrWhiteSpace($Destination)
    $target = if ($temporary) { Join-Path ([System.IO.Path]::GetTempPath()) ('aerolink-release-' + [Guid]::NewGuid().ToString('N')) } else { $PSCmdlet.GetUnresolvedProviderPathFromPSPath($Destination) }
    if (Test-Path -LiteralPath $target) { throw "Refusing to extract into existing path $target. A release is verified into an empty, new directory." }
    New-Item -ItemType Directory -Path $target | Out-Null
    $succeeded = $false
    try {
        $memory = New-Object System.IO.MemoryStream(, $bytes)
        $archive = New-Object System.IO.Compression.ZipArchive($memory, [System.IO.Compression.ZipArchiveMode]::Read)
        try { [System.IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $target) }
        finally { $archive.Dispose(); $memory.Dispose() }

        $manifestPath = Join-Path $target $script:AeroLinkReleaseManifestName
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Release package refused: it holds no $($script:AeroLinkReleaseManifestName)." }
        $manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
        if ($manifest.formatVersion -ne $script:AeroLinkReleaseManifestFormat) { throw "Release package refused: manifest format $($manifest.formatVersion) is not supported." }
        if ([string]$manifest.version -notmatch $script:AeroLinkReleaseSemVer) { throw "Release package refused: manifest version '$($manifest.version)' is not semantic versioning." }
        if ([string]$manifest.sourceSha -notmatch '^[0-9a-f]{40}$') { throw 'Release package refused: the manifest carries no full source SHA.' }
        $posture = [string]$manifest.sourcePosture
        if ($posture -ne 'origin-main' -and -not $AllowUnverifiedDevelopment) {
            throw "Release package refused: its source posture is '$posture', not 'origin-main'. It was not built from protected main; pass -AllowUnverifiedDevelopment only for a development installation."
        }
        $entries = @($manifest.files)
        if ($entries.Count -eq 0) { throw 'Release package refused: the manifest lists no files.' }

        $listed = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $entries) {
            $path = [string]$entry.path
            if ([string]::IsNullOrWhiteSpace($path) -or $path.StartsWith('/') -or $path -match '(^|/)\.\.(/|$)' -or $path -match '[:\\]') {
                throw "Release package refused: manifest path '$path' is not a plain relative path."
            }
            if ($path -eq $script:AeroLinkReleaseManifestName) { throw 'Release package refused: the manifest lists itself.' }
            if (-not $listed.Add($path)) { throw "Release package refused: manifest lists '$path' twice." }
            $file = Join-Path $target ($path.Replace('/', '\'))
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Release package refused: manifest file '$path' is missing from the package." }
            $actual = Get-AeroLinkReleaseFileSha256 -LiteralPath $file
            if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Release package refused: '$path' does not match its manifest hash." }
        }
        foreach ($present in (Get-AeroLinkReleaseRelativeFiles -Root $target)) {
            if ($present -ne $script:AeroLinkReleaseManifestName -and -not $listed.Contains($present)) {
                throw "Release package refused: '$present' is in the package but not in the manifest."
            }
        }
        $succeeded = $true
        return [pscustomobject]@{
            Package         = $packagePath
            Version         = [string]$manifest.version
            SourceSha       = [string]$manifest.sourceSha
            SourcePosture   = [string]$manifest.sourcePosture
            LatestMigration = [string]$manifest.latestMigration
            BundledRuntime  = if ($manifest.PSObject.Properties['bundledRuntime']) { @($manifest.bundledRuntime) } else { @() }
            FileCount       = $entries.Count
            Signer          = $certificate.Thumbprint
            Destination     = if ($temporary) { $null } else { $target }
        }
    }
    finally {
        if ($temporary -or -not $succeeded) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Export-ModuleMember -Function @(
    'New-AeroLinkReleaseSigningCertificate',
    'Get-AeroLinkReleaseSigningCertificate',
    'Assert-AeroLinkReleaseRuntime',
    'New-AeroLinkReleasePackage',
    'Test-AeroLinkReleasePackage'
)
