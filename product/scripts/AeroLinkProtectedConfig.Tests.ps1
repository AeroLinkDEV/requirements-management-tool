#Requires -Version 5.1
<##
    Disposable qualification for the protected GitLab connector store.
    This suite writes only below a newly-created temporary directory and never reads or changes HOME.
##>
$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { $script:failures.Add($Message) } }
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $threw = $false
    try { & $Action } catch { $threw = $true }
    Assert-True $threw $Message
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-protected-gitlab-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'installation'
$protectedRoot = Join-Path $root 'programdata'
$modulePath = Join-Path $PSScriptRoot 'AeroLinkProtectedConfig.psm1'
New-Item -ItemType Directory -Path $install -Force | Out-Null
Import-Module $modulePath -Force

try {
    $projectId = [guid]::NewGuid().ToString('D')
    $programId = [guid]::NewGuid().ToString('D')
    $projectScopeId = [guid]::NewGuid().ToString('D')
    $releaseId = [guid]::NewGuid().ToString('D')
    $baselineId = [guid]::NewGuid().ToString('D')
    $campaignId = [guid]::NewGuid().ToString('D')
    $token = 'test-only-token-never-emitted'
    $secure = ConvertTo-SecureString $token -AsPlainText -Force

    $emptySecure = New-Object Security.SecureString
    Assert-Throws { Set-AeroLinkProtectedGitLabConfig -InstallationRoot $install -BaseUrl 'https://gitlab.com' -ReadAccessToken $emptySecure -RootOverride $protectedRoot } 'Empty SecureString token was accepted.'
    $whitespaceSecure = ConvertTo-SecureString '   ' -AsPlainText -Force
    Assert-Throws { Set-AeroLinkProtectedGitLabConfig -InstallationRoot $install -BaseUrl 'https://gitlab.com' -ReadAccessToken $whitespaceSecure -RootOverride $protectedRoot } 'Whitespace SecureString token was accepted.'

    $saved = Set-AeroLinkProtectedGitLabConfig -InstallationRoot $install -BaseUrl 'https://gitlab.com' `
        -ReadAccessToken $secure -SyntheticDemoProjectId $projectId -SyntheticDemoRemoteProjectId '86663796' `
        -ProgramId $programId -ProjectId $projectScopeId -ReleaseId $releaseId -BaselineId $baselineId -CampaignId $campaignId `
        -RootOverride $protectedRoot
    Assert-True (Test-Path -LiteralPath $saved.Path -PathType Leaf) 'Protected config was not published.'
    Assert-True ($saved.Path -notlike ((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path + '*')) 'Protected config was written inside the repository.'
    $raw = Get-Content -LiteralPath $saved.Path -Raw
    Assert-True ($raw -notlike "*$token*") 'Protected token appeared in the persisted JSON.'
    $fileAcl = Get-Acl -LiteralPath $saved.Path
    $directoryAcl = Get-Acl -LiteralPath (Split-Path -Parent $saved.Path)
    Assert-True $fileAcl.AreAccessRulesProtected 'Protected config file inherits ACL entries.'
    Assert-True $directoryAcl.AreAccessRulesProtected 'Protected config directory inherits ACL entries.'
    $requiredSids = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')
    foreach ($requiredSid in $requiredSids) {
        $fileSids = @($fileAcl.Access | ForEach-Object { try { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { $null } })
        $directorySids = @($directoryAcl.Access | ForEach-Object { try { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { $null } })
        Assert-True ($fileSids -contains $requiredSid) "Protected config file is missing required principal $requiredSid."
        Assert-True ($directorySids -contains $requiredSid) "Protected config directory is missing required principal $requiredSid."
    }

    $descriptor = Get-AeroLinkProtectedGitLabDescriptor -InstallationRoot $install -RootOverride $protectedRoot
    Assert-True $descriptor.Configured 'Protected config descriptor did not report configured.'
    Assert-True ($descriptor.Fingerprint -match '^[0-9a-f]{64}$') 'Descriptor fingerprint is not a non-secret SHA-256 digest.'
    $runtime = Get-AeroLinkProtectedGitLabRuntimeEnvironment -InstallationRoot $install -ConfigPath $descriptor.Path
    Assert-True ($runtime.Environment['ProjectGitLab__ReadAccessToken'] -eq $token) 'Runtime environment did not resolve the DPAPI token.'
    Assert-True ($runtime.Environment['ProjectGitLab__ReleasedSyntheticSourceSupplementScope__ProgramId'] -eq $programId) 'Five-value supplement scope did not map to runtime configuration.'

    # A separate Windows PowerShell process proves that the LocalMachine DPAPI record is usable by the
    # launcher account after the creating process exits. Its only output is a boolean-style verdict.
    $child = Join-Path $root 'fresh-process.ps1'
    @'
param([string]$ModulePath, [string]$InstallationRoot, [string]$ConfigPath)
$ErrorActionPreference = 'Stop'
Import-Module $ModulePath -Force
$runtime = Get-AeroLinkProtectedGitLabRuntimeEnvironment -InstallationRoot $InstallationRoot -ConfigPath $ConfigPath
if ($runtime.Environment['ProjectGitLab__ReadAccessToken'] -ne 'test-only-token-never-emitted') { throw 'fresh process token mismatch' }
Write-Output 'FRESH_PROCESS_PROTECTED_CONFIG_OK'
'@ | Set-Content -LiteralPath $child -Encoding UTF8
    $freshOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $child -ModulePath $modulePath -InstallationRoot $install -ConfigPath $descriptor.Path)
    Assert-True (($freshOutput -join "`n") -match 'FRESH_PROCESS_PROTECTED_CONFIG_OK') 'Fresh process could not load the DPAPI config.'
    Assert-True (($freshOutput -join "`n") -notlike "*$token*") 'Fresh process emitted the token.'

    # Metadata validation never decrypts. A syntactically valid but undecryptable ciphertext is accepted by
    # the descriptor and fails only when the API launch environment is materialized.
    $original = Get-Content -LiteralPath $saved.Path -Raw
    $undecryptable = Get-Content -LiteralPath $saved.Path -Raw | ConvertFrom-Json
    $undecryptable.protectedToken = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('not-dpapi-ciphertext'))
    [IO.File]::WriteAllText($saved.Path, ($undecryptable | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    $lazyDescriptor = Get-AeroLinkProtectedGitLabDescriptor -InstallationRoot $install -RootOverride $protectedRoot
    Assert-True $lazyDescriptor.Configured 'Metadata descriptor rejected a valid-Base64 ciphertext before decryption.'
    Assert-Throws { Get-AeroLinkProtectedGitLabRuntimeEnvironment -InstallationRoot $install -ConfigPath $lazyDescriptor.Path } 'Runtime materialization accepted undecryptable ciphertext.'
    [IO.File]::WriteAllText($saved.Path, $original, (New-Object Text.UTF8Encoding($false)))

    # The transition broker receives only the non-secret marker. It resolves the ciphertext in memory for the
    # API role and rejects the same marker for every other launch role.
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionAuthority.psm1') -Force
    $marker = [ordered]@{ AEROLINK_PROTECTED_GITLAB_CONFIG_PATH = $saved.Path; AEROLINK_PROTECTED_GITLAB_INSTALLATION_ROOT = $install }
    $requestJson = $marker | ConvertTo-Json -Compress
    Assert-True ($requestJson -notlike "*$token*") 'Transition request JSON contains the token sentinel.'
    $authority = Get-Module AeroLinkTransitionAuthority
    $spool = Join-Path $root 'transition-spool'
    $requestResult = & $authority {
        param($spoolPath, $environment)
        Request-AeroLinkServiceLaunch -Handoff ([pscustomobject]@{ spool = $spoolPath; attemptId = 'protected-config-test' }) `
            -Role api -FilePath 'C:\Windows\System32\cmd.exe' -WorkingDirectory $env:TEMP -Environment $environment `
            -Readiness @{ kind = 'none' } -AcceptTimeoutSeconds 0 -SettleSeconds 0
    } $spool $marker
    $requestFile = Get-ChildItem -LiteralPath $spool -Filter '*.request.json' -File | Select-Object -First 1
    $requestBody = Get-Content -LiteralPath $requestFile.FullName -Raw
    Assert-True ($requestBody -notlike "*$token*") 'Actual transition request spool contains the token sentinel.'
    Assert-True ($requestBody -like "*$($saved.Path.Replace('\','\\'))*") 'Actual transition request spool lost the protected marker path.'
    $markerForAuthority = ($requestBody | ConvertFrom-Json).launch.environment
    $resolvedChildEnvironment = & $authority { param($Overrides); New-AeroLinkServiceEnvironment -Overrides $Overrides -Role api } $markerForAuthority
    Assert-True ($resolvedChildEnvironment['ProjectGitLab__ReadAccessToken'] -eq $token) 'Transition authority did not materialize token for API child.'
    Assert-True (-not $resolvedChildEnvironment.ContainsKey('AEROLINK_PROTECTED_GITLAB_CONFIG_PATH')) 'Protected marker leaked into API child environment.'
    Assert-Throws { & $authority { param($Overrides); New-AeroLinkServiceEnvironment -Overrides $Overrides -Role tunnel } $markerForAuthority } 'Transition authority allowed a protected marker for a non-API role.'

    # Direct launch must restore both an originally absent and an originally populated process variable after
    # the child is created, including when readiness fails. The child is an immediate-exit disposable process.
    . (Join-Path $PSScriptRoot 'AeroLinkLaunch.ps1')
    $absentName = 'AEROLINK_1023_PROTECTED_ABSENT'
    $presentName = 'AEROLINK_1023_PROTECTED_PRESENT'
    Remove-Item -LiteralPath "Env:\$absentName" -ErrorAction SilentlyContinue
    [Environment]::SetEnvironmentVariable($presentName, 'original-value', 'Process')
    $directFailure = $false
    try {
        try {
            Start-AeroLinkService -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') `
                -ArgumentList @('-NoProfile', '-Command', 'exit 0') -WorkingDirectory $env:TEMP `
                -StandardOutput (Join-Path $root 'direct.stdout.log') -StandardError (Join-Path $root 'direct.stderr.log') `
                -ReadyUri 'http://127.0.0.1:1/never-ready' -ServiceName 'protected-config direct restore probe' `
                -TimeoutSeconds 1 -Environment @{ $absentName = 'temporary-value'; $presentName = 'temporary-value' }
        }
        catch { $directFailure = $true }
        Assert-True $directFailure 'Direct launch restoration probe unexpectedly reported success.'
        Assert-True ($null -eq [Environment]::GetEnvironmentVariable($absentName, 'Process')) 'Direct launch left an originally absent variable present.'
        Assert-True ([Environment]::GetEnvironmentVariable($presentName, 'Process') -eq 'original-value') 'Direct launch failed to restore an originally populated variable.'
    }
    finally {
        Remove-Item -LiteralPath "Env:\$absentName" -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath "Env:\$presentName" -ErrorAction SilentlyContinue
    }

    $firstFingerprint = $descriptor.Fingerprint
    $secure2 = ConvertTo-SecureString 'rotated-token-never-emitted' -AsPlainText -Force
    $rotated = Set-AeroLinkProtectedGitLabConfig -InstallationRoot $install -BaseUrl 'https://gitlab.com' -ReadAccessToken $secure2 `
        -SyntheticDemoProjectId $projectId -SyntheticDemoRemoteProjectId '86663796' -ProgramId $programId -ProjectId $projectScopeId `
        -ReleaseId $releaseId -BaselineId $baselineId -CampaignId $campaignId -RootOverride $protectedRoot
    Assert-True ($rotated.Fingerprint -ne $firstFingerprint) 'Token rotation retained the connector fingerprint.'

    $original = Get-Content -LiteralPath $saved.Path -Raw
    Set-Content -LiteralPath $saved.Path -Value '{ malformed' -Encoding UTF8
    Assert-Throws { Get-AeroLinkProtectedGitLabDescriptor -InstallationRoot $install -RootOverride $protectedRoot } 'Malformed protected config was accepted.'
    Assert-Throws { Set-AeroLinkProtectedGitLabConfig -InstallationRoot $install -BaseUrl 'https://gitlab.com' -ReadAccessToken $secure2 -RootOverride $protectedRoot } 'Set silently repaired a malformed existing config.'
    [IO.File]::WriteAllText($saved.Path, $original, (New-Object Text.UTF8Encoding($false)))

    $record = Get-Content -LiteralPath $saved.Path -Raw | ConvertFrom-Json
    $record.protectedToken = '%%%'
    [IO.File]::WriteAllText($saved.Path, ($record | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    Assert-Throws { Get-AeroLinkProtectedGitLabDescriptor -InstallationRoot $install -RootOverride $protectedRoot } 'Invalid Base64 ciphertext was accepted.'
    [IO.File]::WriteAllText($saved.Path, $original, (New-Object Text.UTF8Encoding($false)))

    $record = Get-Content -LiteralPath $saved.Path -Raw | ConvertFrom-Json
    $record.ownerSid = 'S-1-5-21-0-0-0-9999'
    [IO.File]::WriteAllText($saved.Path, ($record | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    Assert-Throws { Get-AeroLinkProtectedGitLabDescriptor -InstallationRoot $install -RootOverride $protectedRoot } 'Owner SID mismatch was accepted.'
    [IO.File]::WriteAllText($saved.Path, $original, (New-Object Text.UTF8Encoding($false)))

    # A fresh default directory inherits the workstation ACL. It is a separate disposable negative case and
    # needs no privilege-changing ACL mutation, while proving the parent-directory inheritance guard fires first.
    $unprotectedRoot = Join-Path $root 'unprotected-programdata'
    $unprotectedPath = Get-AeroLinkProtectedConfigPath -InstallationRoot $install -RootOverride $unprotectedRoot
    New-Item -ItemType Directory -Path (Split-Path -Parent $unprotectedPath) -Force | Out-Null
    New-Item -ItemType File -Path $unprotectedPath -Force | Out-Null
    Assert-Throws { Get-AeroLinkProtectedGitLabDescriptor -InstallationRoot $install -RootOverride $unprotectedRoot } 'Inherited parent directory ACL was accepted.'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    throw "Protected GitLab configuration tests failed ($($failures.Count))."
}
Write-Host 'Protected GitLab configuration tests passed (DPAPI roundtrip, fresh process, ACL, ownership, malformed record, and rotation).' -ForegroundColor Green
