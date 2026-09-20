#Requires -Version 5.1
<#$
  Bounded, resumable source-selection and Code-relationship authoring for the synthetic #1023 demo.
  The only writes are the reviewed source confirmation and relationship commands. This module never writes
  GitLab, repository configuration, evidence/NoCode records, or credentials.
#>
Set-StrictMode -Version Latest
$script:MaxBodyBytes = 4MB
$script:PageSize = 100
$script:ShaPattern = '^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$'

function New-AeroLinkResumableClient {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ApiBaseUrl,
        [PSCredential]$Credential,
        [switch]$Login,
        [int]$TimeoutSeconds = 20
    )

    Add-Type -AssemblyName System.Net.Http
    $base = [Uri]$ApiBaseUrl
    if (-not $base.IsAbsoluteUri -or $base.Scheme -notin @('http', 'https')) {
        throw 'The API base URL must be absolute HTTP(S).'
    }
    if ($base.Scheme -eq 'http' -and -not $base.IsLoopback) {
        throw 'Plain HTTP is permitted only for a loopback API; remote APIs require HTTPS.'
    }
    if ($Login -and $null -eq $Credential) {
        throw 'A credential is required for login.'
    }

    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $true
    $handler.CookieContainer = [Net.CookieContainer]::new()

    $client = [Net.Http.HttpClient]::new($handler)
    $client.BaseAddress = [Uri]($base.AbsoluteUri.TrimEnd('/') + '/')
    $client.Timeout = [TimeSpan]::FromSeconds([Math]::Max(1, $TimeoutSeconds))

    $state = [pscustomobject]@{
        Client = $client
        Handler = $handler
        Base = $client.BaseAddress
        TimeoutSeconds = [Math]::Max(1, $TimeoutSeconds)
    }

    if ($Login) {
        $network = $Credential.GetNetworkCredential()
        $body = @{
            userName = $network.UserName
            password = $network.Password
        } | ConvertTo-Json -Compress

        try {
            $loginResult = Invoke-AeroLinkResumableRequest $state POST '/api/auth/login' $body
            if ($loginResult.Status -lt 200 -or $loginResult.Status -ge 300) {
                throw "API login refused with HTTP $($loginResult.Status)."
            }
        }
        catch {
            Close-AeroLinkResumableClient $state
            throw
        }
        finally {
            $network = $null
            $body = $null
        }
    }

    return $state
}
function Close-AeroLinkResumableClient {
    param($State)

    if ($State.Client) {
        $State.Client.Dispose()
    }
    if ($State.Handler) {
        $State.Handler.Dispose()
    }
}
function Get-Prop {
    param($Object, [string]$Name)

    if ($null -eq $Object) {
        return $null
    }

    if ($Object -is [Collections.IDictionary]) {
        return $Object[$Name]
    }

    $p = $Object.PSObject.Properties[$Name]

    if ($null -eq $p) {
        return $null
    }

    return $p.Value
}
function Has-Prop {
    param($Object, [string]$Name)

    return ($null -ne $Object -and
        (($Object -is [Collections.IDictionary] -and $Object.Contains($Name)) -or
            $null -ne $Object.PSObject.Properties[$Name]))
}
function Conflict {
    param([string]$Code, [string]$Message)

    $e = [Exception]::new("$Code`: $Message")
    $e.Data['Code'] = $Code
    throw $e
}
function Read-BoundedBody {
    param(
        [Parameter(Mandatory)]$Response,
        [Parameter(Mandatory)][Threading.CancellationToken]$CancellationToken
    )

    $length = $Response.Content.Headers.ContentLength
    if ($null -ne $length -and $length -gt $script:MaxBodyBytes) {
        throw 'The API response exceeded the bounded body limit.'
    }

    $stream = $null
    $memory = [IO.MemoryStream]::new()
    $buffer = New-Object byte[] 65536
    try {
        $stream = $Response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        while ($true) {
            $read = $stream.ReadAsync($buffer, 0, $buffer.Length, $CancellationToken).GetAwaiter().GetResult()
            if ($read -le 0) {
                break
            }
            $memory.Write($buffer, 0, $read)
            if ($memory.Length -gt $script:MaxBodyBytes) {
                throw 'The API response exceeded the bounded body limit.'
            }
        }
        return [Text.Encoding]::UTF8.GetString($memory.ToArray())
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        $memory.Dispose()
    }
}

function Invoke-AeroLinkResumableRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][ValidateSet('GET', 'POST')][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [string]$Body,
        [switch]$AllowTransportFailure
    )

    if (-not $Path.StartsWith('/') -or $Path.Contains('..') -or $Path.Contains('//')) {
        throw 'Unsafe API path.'
    }
    if ($null -ne $State.PSObject.Properties['Responder']) {
        return & $State.Responder $Method $Path $Body $AllowTransportFailure
    }

    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::$Method, $Path)
    $response = $null
    $timeoutSeconds = 20
    if ($null -ne $State.PSObject.Properties['TimeoutSeconds']) {
        $timeoutSeconds = [Math]::Max(1, [int]$State.TimeoutSeconds)
    }
    $cancellation = [Threading.CancellationTokenSource]::new(
        [TimeSpan]::FromSeconds($timeoutSeconds))
    try {
        if ($PSBoundParameters.ContainsKey('Body')) {
            $request.Content = [Net.Http.StringContent]::new(
                $Body, [Text.Encoding]::UTF8, 'application/json')
        }

        try {
            $response = $State.Client.SendAsync(
                $request,
                [Net.Http.HttpCompletionOption]::ResponseHeadersRead,
                $cancellation.Token).GetAwaiter().GetResult()
            $text = Read-BoundedBody $response $cancellation.Token
            $status = [int]$response.StatusCode
            if ($status -ge 300 -and $status -lt 400) {
                throw 'The API returned a redirect; redirected requests are refused.'
            }

            $json = $null
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                try {
                    $json = $text | ConvertFrom-Json
                }
                catch {
                    throw "The API returned malformed JSON for $Path."
                }
            }

            return [pscustomobject]@{
                TransportFailure = $false
                Status = $status
                Body = $json
                Text = $text
            }
        }
        catch {
            if ($AllowTransportFailure) {
                return [pscustomobject]@{
                    TransportFailure = $true
                    Status = $null
                    Body = $null
                }
            }
            throw 'The API request did not complete.'
        }
    }
    finally {
        $cancellation.Dispose()
        if ($null -ne $response) {
            $response.Dispose()
        }
        $request.Dispose()
    }
}
function Get-Json {
     param($State,[string]$Path) $r=Invoke-AeroLinkResumableRequest $State GET $Path;

    if($r.Status -lt 200 -or $r.Status -ge 300){
        throw "API GET refused with HTTP $($r.Status) at $Path."
    };

    return $r.Body
}
function Normalize-Origin {
     param([string]$Value) try{
        $u=[Uri]$Value
    }catch{
        throw 'An origin must be absolute HTTP(S).'
    };

    if(!$u.IsAbsoluteUri -or $u.Scheme -notin @('http','https') -or $u.Query -or $u.Fragment -or $u.UserInfo){
        throw 'An origin must be canonical HTTP(S).'
    };

    return ($u.GetLeftPart([UriPartial]::Authority).TrimEnd('/')+$u.AbsolutePath.TrimEnd('/'))
}
function Require-Guid {
     param($Value,[string]$Name) $g=[Guid]::Empty;

    if(![Guid]::TryParse([string]$Value,[ref]$g)-or $g -eq [Guid]::Empty){
        throw "$Name must be a non-empty GUID."
    };

    return $g.ToString()
}
function Require-Long {
     param($Value,[string]$Name,[switch]$AllowZero) $v=0L;

    if(![Int64]::TryParse([string]$Value,[ref]$v)-or ($AllowZero -and $v -lt 0)-or (!$AllowZero -and $v -le 0)){
        throw "$Name must be $([string]$(if($AllowZero){'non-negative'}else{'positive'}))."
    };

    return $v
}
function Require-Sha {
     param([string]$Value,[string]$Name) if([string]::IsNullOrWhiteSpace($Value)-or $Value -notmatch $script:ShaPattern){
        throw "$Name must be a full 40 or 64 character SHA."
    };

    return $Value.ToLowerInvariant()
}
function Safe-Path {
    param(
        [string]$Value,
        [string]$Name,
        [switch]$AllowEmpty
    )

    if ($null -eq $Value) {
        $Value = ''
    }
    $path = $Value.Trim().Trim('/')
    if (-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($path)) {
        throw "$Name is required."
    }

    $parts = @()
    if ($path.Length -gt 0) {
        $parts = @($path.Split('/'))
    }
    if ($path.Length -gt 2048 -or $path.Contains('\') -or
        $path.Contains('//') -or
        @($parts | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
        throw "$Name is unsafe."
    }
    return $path
}
function Canonical-Value {
     param($Value) if($null -eq $Value){
        return $null
    };

    if($Value -is [Collections.IDictionary]){
        $o=[ordered]@{

        };

        foreach($k in ($Value.Keys|Sort-Object{
            [string]$_
        })){
            $o[[string]$k]=Canonical-Value $Value[$k]
        };

        return [pscustomobject]$o
    };

    if($Value -is [Collections.IEnumerable] -and $Value -isnot [string]){
        $a=@();

        foreach($x in $Value){
            $a+=,(Canonical-Value $x)
        };

        return $a
    };

    if($Value -is [Management.Automation.PSCustomObject]){
        $o=[ordered]@{

        };

        foreach($p in ($Value.PSObject.Properties|Sort-Object Name)){
            $o[$p.Name]=Canonical-Value $p.Value
        };

        return [pscustomobject]$o
    };

    return $Value
}
function Get-ManifestDigest {
     param($Manifest) $bytes=[Text.Encoding]::UTF8.GetBytes((Canonical-Value $Manifest|ConvertTo-Json -Depth 50 -Compress));

    $h=[Security.Cryptography.SHA256]::Create().ComputeHash($bytes);

    return ([BitConverter]::ToString($h)-replace '-','').ToLowerInvariant()
}

function Convert-ManifestToPlan {

        param($Manifest)
        if((Get-Prop $Manifest formatVersion)-ne 1){
        throw 'Manifest formatVersion must be 1.'
    }
        $project=Require-Guid (Get-Prop $Manifest projectId) projectId;

    $release=Require-Guid (Get-Prop $Manifest releaseId) releaseId
        $repo=Get-Prop $Manifest repository;

    if($null -eq $repo){
        throw 'Manifest repository identity is required.'
    }
        $repoPlan=[pscustomobject]@{
        ConfigurationId=Require-Guid (Get-Prop $repo configurationId) repository.configurationId;

        ConfigurationVersion=Require-Long (Get-Prop $repo configurationVersion) repository.configurationVersion;

        Origin=Normalize-Origin ([string](Get-Prop $repo origin));

        RemoteProjectId=Require-Long (Get-Prop $repo remoteProjectId) repository.remoteProjectId;

        Path=Safe-Path ([string](Get-Prop $repo pathWithNamespace) ) repository.pathWithNamespace
    }
        $source=Get-Prop $Manifest source;

    if($null -eq $source){
        throw 'Manifest source is required.'
    }
        $sourcePlan=[pscustomobject]@{
        Reference=[string](Get-Prop $source reference);

        ReferenceKind=[string](Get-Prop $source referenceKind);

        CommitSha=Require-Sha ([string](Get-Prop $source commitSha)) source.commitSha;

        ExpectedSelectionVersion=Require-Long (Get-Prop $source expectedSelectionVersion) source.expectedSelectionVersion -AllowZero;

        ExpectedSelectionEventId=$null
    }
        $event=Get-Prop $source expectedSelectionEventId;

    if($null -ne $event){
        $sourcePlan.ExpectedSelectionEventId=Require-Guid $event source.expectedSelectionEventId
    };

    if($sourcePlan.ExpectedSelectionVersion -eq 0 -and $null -ne $sourcePlan.ExpectedSelectionEventId){
        throw 'source.expectedSelectionEventId must be empty when expectedSelectionVersion is zero.'
    };

    if($sourcePlan.ExpectedSelectionVersion -gt 0 -and $null -eq $sourcePlan.ExpectedSelectionEventId){
        throw 'source.expectedSelectionEventId is required for an existing selection version.'
    }
        if([string]::IsNullOrWhiteSpace($sourcePlan.Reference) -or $sourcePlan.Reference.Length -gt 256){
        throw 'source.reference is required and bounded.'
    }
        if($sourcePlan.ReferenceKind -notin @('Auto','Commit','Branch','Tag')){
        throw 'source.referenceKind is unsupported.'
    }
        $raw=@(Get-Prop $Manifest relationships);

    if($raw.Count -gt 100){
        throw 'Manifest relationships are bounded at 100.'
    };

    $items=[Collections.Generic.List[object]]::new();

    $keys=[Collections.Generic.Dictionary[string,bool]]::new([StringComparer]::Ordinal)
        foreach($item in $raw){
        if($null -eq $item){
            continue
        };

        $kind=[string](Get-Prop $item kind);

        if($kind -notin @('MergeRequest','File')){
            throw 'Relationship kind must be MergeRequest or File.'
        };

        $releaseItem=Get-Prop $item releaseId;

        if($null -ne $releaseItem -and (Require-Guid $releaseItem relationship.releaseId)-ne $release){
            throw 'Relationship release must match the manifest release.'
        };

        $targetKind=[string](Get-Prop $item targetKind);

        $targetId=Require-Guid (Get-Prop $item targetId) relationship.targetId;

        $meaning=[string](Get-Prop $item meaning);

        if([string]::IsNullOrWhiteSpace($targetKind)-or [string]::IsNullOrWhiteSpace($meaning)){
            throw 'Typed targetKind and meaning are required.'
        }
                $row=[ordered]@{
            Kind=$kind;

            ReleaseId=$release;

            TargetKind=$targetKind;

            TargetId=$targetId;

            Meaning=$meaning;

            SourceSnapshotId=$null;

            SourceSelectionEventId=$null;

            MergeRequestIid=$null;

            CommitSha=$null;

            Path=$null;

            ParentPath=$null;

            Cursor=$null;

            PageSize=$null;

            StartLine=$null;

            EndLine=$null;

            MergeRequestIidContext=$null
        }
                if($kind -eq 'MergeRequest'){
            $iid=Require-Long (Get-Prop $item mergeRequestIid) relationship.mergeRequestIid;

            if($iid -gt 2147483647){
                throw 'mergeRequestIid is out of range.'
            };

            $row.MergeRequestIid=[int]$iid;

            $row.SourceSnapshotId=if($null -eq (Get-Prop $item sourceSnapshotId)){
                $null
            }else{
                Require-Guid (Get-Prop $item sourceSnapshotId) relationship.sourceSnapshotId
            };

            $row.SourceSelectionEventId=if($null -eq (Get-Prop $item sourceSelectionEventId)){
                $null
            }else{
                Require-Guid (Get-Prop $item sourceSelectionEventId) relationship.sourceSelectionEventId
            }
        }
                else{
            $row.SourceSnapshotId=Require-Guid (Get-Prop $item sourceSnapshotId) relationship.sourceSnapshotId;

            $row.SourceSelectionEventId=if($null -eq (Get-Prop $item sourceSelectionEventId)){
                $null
            }else{
                Require-Guid (Get-Prop $item sourceSelectionEventId) relationship.sourceSelectionEventId
            };

            $row.CommitSha=Require-Sha ([string](Get-Prop $item commitSha)) relationship.commitSha;

            $row.Path=Safe-Path ([string](Get-Prop $item path)) relationship.path;

            $row.ParentPath=Safe-Path ([string](Get-Prop $item parentPath) ) relationship.parentPath -AllowEmpty;

            $relative=if($row.ParentPath){
                if(!$row.Path.StartsWith($row.ParentPath+'/',[StringComparison]::Ordinal)){
                    $null
                }else{
                    $row.Path.Substring($row.ParentPath.Length+1)
                }
            }else{
                $row.Path
            };

            if(!$relative -or $relative.Contains('/')){
                throw 'A file must be an immediate child of parentPath.'
            };

            $row.Cursor=if($null -eq (Get-Prop $item cursor)){
                $null
            }else{
                [string](Get-Prop $item cursor)
            };

            $row.PageSize=[int](Get-Prop $item pageSize);

            if($row.PageSize -lt 1 -or $row.PageSize -gt 100){
                throw 'File pageSize must be between 1 and 100.'
            };

            foreach($n in @('startLine','endLine','mergeRequestIid')){
                $v=Get-Prop $item $n;

                if($null -ne $v){
                    $number = Require-Long $v "relationship.$n"
                    if ([Int64]$number -gt 2147483647) {
                        throw "relationship.$n is out of range."
                    }
                    if($n -eq 'mergeRequestIid'){$row.MergeRequestIidContext=[int]$number}else{$row.$([char]::ToUpperInvariant($n[0])+$n.Substring(1))=[int]$number}
                }
            }

            if (($null -eq $row.StartLine) -xor ($null -eq $row.EndLine)) {
                throw 'relationship.startLine and relationship.endLine must be supplied together.'
            }
            if ($null -ne $row.StartLine -and ($row.StartLine -lt 1 -or $row.EndLine -lt $row.StartLine)) {
                throw 'relationship line range is invalid.'
            }

        }
                $key="$kind|$($row.SourceSnapshotId)|$($row.SourceSelectionEventId)|$($row.MergeRequestIid)|$($row.MergeRequestIidContext)|$($row.CommitSha)|$($row.Path)|$($row.StartLine)|$($row.EndLine)|$targetKind|$targetId|$meaning";

        if($keys.ContainsKey($key)){
            throw 'Manifest relationship identities must be unique.'
        };

        $keys[$key]=$true;

        [void]$items.Add([pscustomobject]$row)

    }
        return [pscustomobject]@{
        FormatVersion=1;

        ProjectId=$project;

        ReleaseId=$release;

        Repository=$repoPlan;

        Source=$sourcePlan;

        Relationships=@($items);

        Digest=Get-ManifestDigest $Manifest
    }

}
function Write-Atomic {
     param([string]$Path,$Value) $parent=Split-Path -Parent $Path;

    if(!(Test-Path $parent)){
        New-Item -ItemType Directory -Path $parent -Force|Out-Null
    };

    $tmp="$Path.$([Guid]::NewGuid().ToString('N')).tmp";

    try{
        $Value|ConvertTo-Json -Depth 50|Set-Content $tmp -Encoding UTF8;

        Move-Item $tmp $Path -Force
    }finally{
        if(Test-Path $tmp){
            Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        }
    }
}
function Read-Journal {
    param([string]$Path)

    if (-not (Test-Path $Path -PathType Leaf)) {
        return $null
    }

    try {
        $journal = Get-Content $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw 'The journal is malformed.'
    }

    if ((Get-Prop $journal formatVersion) -ne 1 -or
        -not (Has-Prop $journal manifestDigest) -or
        -not (Has-Prop $journal projectId) -or
        -not (Has-Prop $journal releaseId) -or
        -not (Has-Prop $journal apiOrigin) -or
        -not (Has-Prop $journal steps)) {
        throw 'The journal is malformed.'
    }

    $steps = @(Get-Prop $journal steps)
    $keys = [Collections.Generic.Dictionary[string,bool]]::new([StringComparer]::Ordinal)
    foreach ($step in $steps) {
        $key = [string](Get-Prop $step stepKey)
        $state = [string](Get-Prop $step state)

        if ([string]::IsNullOrWhiteSpace($key) -or
            $state -notin @('Pending', 'Applied') -or
            $keys.ContainsKey($key)) {
            throw 'The journal contains malformed or duplicate steps.'
        }

        $keys[$key] = $true
    }

    return $journal
}
function Assert-Repo {
    param($State, $Plan)

    $response = Get-Json $State "/api/projects/$($Plan.ProjectId)/repository"
    $repository = Get-Prop $response repository
    if ($null -eq $repository -or
        (Get-Prop $repository status) -ne 'Verified' -or
        (Get-Prop $repository provider) -ne 'GitLab') {
        Conflict repository_changed 'The repository is not a verified GitLab configuration.'
    }

    try {
        $endpoint = [Uri](Get-Prop $repository endpoint)
    }
    catch {
        Conflict config_changed 'The verified repository endpoint is invalid.'
    }
    $expectedOrigin = [Uri]$Plan.Repository.Origin
    $expectedPath = $expectedOrigin.AbsolutePath.TrimEnd('/') + '/' + $Plan.Repository.Path
    $actualPath = [Uri]::UnescapeDataString($endpoint.AbsolutePath).TrimEnd('/')
    if ($actualPath.EndsWith('.git', [StringComparison]::OrdinalIgnoreCase)) {
        $actualPath = $actualPath.Substring(0, $actualPath.Length - 4)
    }

    if ($endpoint.Scheme -ne $expectedOrigin.Scheme -or
        $endpoint.Host -ne $expectedOrigin.Host -or
        $endpoint.Port -ne $expectedOrigin.Port -or
        $actualPath -cne $expectedPath -or
        [Int64](Get-Prop $repository remoteProjectId) -ne $Plan.Repository.RemoteProjectId -or
        (Get-Prop $repository remotePath) -cne $Plan.Repository.Path -or
        [Int64](Get-Prop $repository version) -ne $Plan.Repository.ConfigurationVersion) {
        Conflict config_changed 'The verified repository identity no longer matches the manifest.'
    }

    $reference = [Uri]::EscapeDataString($Plan.Source.Reference)
    $referenceKind = [Uri]::EscapeDataString($Plan.Source.ReferenceKind)
    $metadataPath = "/api/projects/$($Plan.ProjectId)/repository/commit?reference=$reference&referenceKind=$referenceKind"
    $metadata = Get-Json $State $metadataPath
    $observation = Get-Prop $metadata observation
    if ($null -eq $observation -or [string](Get-Prop $observation code) -ne 'ok') {
        Conflict repository_changed 'The verified repository did not return an exact metadata observation.'
    }
    if ([string](Get-Prop (Get-Prop $observation value) sha) -ne $Plan.Source.CommitSha) {
        Conflict source_changed 'The reference no longer resolves to the reviewed full commit.'
    }

    $configurationId = Require-Guid (Get-Prop $metadata configurationId) metadata.configurationId
    $configurationVersion = [Int64](Get-Prop $metadata configurationVersion)
    $remoteProjectId = [Int64](Get-Prop $metadata remoteProjectId)
    if ($configurationId -ne $Plan.Repository.ConfigurationId -or
        $configurationVersion -ne $Plan.Repository.ConfigurationVersion -or
        $remoteProjectId -ne $Plan.Repository.RemoteProjectId) {
        Conflict config_changed 'The metadata observation no longer matches the manifest configuration.'
    }

    return [pscustomobject]@{
        ConfigurationId = $configurationId
        ConfigurationVersion = $configurationVersion
        Origin = $Plan.Repository.Origin
        RemoteProjectId = $remoteProjectId
        Path = $Plan.Repository.Path
    }
}
function Read-SourceState {
     param($State,$Plan) $current=Get-Json $State "/api/projects/$($Plan.ProjectId)/code/source?releaseId=$($Plan.ReleaseId)";

    $snapshot=Get-Prop $current snapshot;

    return [pscustomobject]@{
        Current=$current;

        Snapshot=$snapshot
    }
}
function Read-SourceHistoryExact {
     param($State,$Plan,[long]$Version,[string]$EventId,[string]$CommitSha,[int]$ScanLimit) $page=1;

    $found=$null;

    $total=$null;

    while($true){
        $h=Get-Json $State "/api/projects/$($Plan.ProjectId)/code/source/history?releaseId=$($Plan.ReleaseId)&page=$page&pageSize=$script:PageSize";

        $rows=@(Get-Prop $h items);

        $total=[Int64](Get-Prop $h total);

        foreach($r in $rows){
            if([string](Get-Prop $r id)-eq $EventId){
                if($null-ne $found){
                    Conflict source_ambiguous 'The source event appeared more than once.'
                };

                $found=$r
            }
        };

        if($page*$script:PageSize -ge $total -or !$rows){
            break
        };

        $page++;

        if($page*$script:PageSize -gt $ScanLimit){
            Conflict scan_limit 'Source history exceeded the scan limit.'
        }
    };

    $snap=Get-Prop $found snapshot;

    if($null -eq $found -or [Int64](Get-Prop $found resultingVersion)-ne $Version -or ($CommitSha -and [string](Get-Prop $snap commitSha)-ne $CommitSha) -or [string](Get-Prop $snap instanceBaseUrl)-ne $Plan.Repository.Origin -or [Int64](Get-Prop $snap remoteProjectId)-ne $Plan.Repository.RemoteProjectId -or [string](Get-Prop $snap pathWithNamespace)-cne $Plan.Repository.Path){
        Conflict source_changed 'The exact source transition was not observed.'
    };

    return $found
}
function Read-SourceTransition {

        param($State, $Plan, [long]$ExpectedVersion, [string]$CommitSha, [int]$ScanLimit)
        $page = 1
        $matches = [Collections.Generic.List[object]]::new()
        $total = 0L
        while ($true) {

                $history = Get-Json $State "/api/projects/$($Plan.ProjectId)/code/source/history?releaseId=$($Plan.ReleaseId)&page=$page&pageSize=$script:PageSize"
                $items = @(Get-Prop $history items)
                $total = [Int64](Get-Prop $history total)
                foreach ($item in $items) {

                        $snapshot = Get-Prop $item snapshot
                        if ([Int64](Get-Prop $item expectedCurrentVersion) -eq $ExpectedVersion -and
                            [Int64](Get-Prop $item resultingVersion) -eq $ExpectedVersion + 1 -and
                            [string](Get-Prop $snapshot commitSha) -eq $CommitSha -and
                            [string](Get-Prop $snapshot instanceBaseUrl) -eq $Plan.Repository.Origin -and
                            [Int64](Get-Prop $snapshot remoteProjectId) -eq $Plan.Repository.RemoteProjectId -and
                            [string](Get-Prop $snapshot pathWithNamespace) -ceq $Plan.Repository.Path) {

                                [void]$matches.Add($item)

            }

        }
                if ($page * $script:PageSize -ge $total -or $items.Count -eq 0) {

                        break

        }
                $page++
                if ($page * $script:PageSize -gt $ScanLimit) {

                        Conflict scan_limit 'Source history exceeded the scan limit.'

        }

    }
        if ($matches.Count -ne 1) {

                Conflict source_ambiguous 'The pending source request does not have one exact resulting transition.'

    }
        return $matches[0]

}
function Read-AllRelationships {
     param($State,$Plan,[int]$ScanLimit) $all=[Collections.Generic.List[object]]::new();

    $ids=@{

    };

    $page=1;

    $total=$null;

    while($true){
        $r=Get-Json $State "/api/projects/$($Plan.ProjectId)/code/relationships?releaseId=$($Plan.ReleaseId)&relationshipKind=all&includeWithdrawn=true&page=$page&pageSize=$script:PageSize";

        $items=@(Get-Prop $r items);

        $t=[Int64](Get-Prop $r total);

        if($null-eq$total){
            $total=$t;

            if($total -gt $ScanLimit){
                Conflict scan_limit 'Relationship scan exceeded the configured bound.'
            }
        }elseif($total-ne$t){
            Conflict invalid_response 'Relationship total changed during the scan.'
        };

        foreach($x in $items){
            $key="$(Get-Prop $x relationshipKind)|$(Get-Prop $x id)";

            if($ids.ContainsKey($key)){
                Conflict invalid_response 'Relationship pages repeated an identity.'
            };

            $ids[$key]=$true;

            $all.Add($x)
        };

        if($all.Count -eq $total){
            return @($all)
        };

        if(!$items -or $all.Count -gt $total){
            Conflict invalid_response 'Relationship pagination did not prove completeness.'
        };

        $page++;

        if($page*$script:PageSize -gt $ScanLimit){
            Conflict scan_limit 'Relationship pagination exceeded the scan limit.'
        }
    }
}
function Read-TreeProof {
     param($State,$Plan,$Row) $path="/api/projects/$($Plan.ProjectId)/code/source/$($Row.SourceSnapshotId)/tree?commit=$([Uri]::EscapeDataString($Row.CommitSha))&path=$([Uri]::EscapeDataString($Row.ParentPath))&pageSize=$($Row.PageSize)";

    if($null-ne$Row.Cursor){
        $path+="&cursor=$([Uri]::EscapeDataString($Row.Cursor))"
    };

    $outer=Get-Json $State $path;

    $observation=Get-Prop $outer observation;

    $result=Get-Prop $observation value;
    $requestedPath = [string](Get-Prop $result requestedPath)
    if ($null -eq (Get-Prop $result requestedPath)) {
        $requestedPath = ''
    }

    if($null -eq $result -or
        [string](Get-Prop $observation code) -ne 'ok' -or
        [string](Get-Prop $outer configurationId) -ne [string]$Plan.Repository.ConfigurationId -or
        [Int64](Get-Prop $outer configurationVersion) -ne $Plan.Repository.ConfigurationVersion -or
        [Int64](Get-Prop $outer remoteProjectId) -ne $Plan.Repository.RemoteProjectId -or
        [Int64](Get-Prop $result projectId)-ne$Plan.Repository.RemoteProjectId -or
        [string](Get-Prop $result commitSha)-ne$Row.CommitSha -or
        $requestedPath -cne [string]$Row.ParentPath){
        Conflict provider_changed 'The source-bound tree observation did not match the manifest.'
    };

    $entries=@(Get-Prop $result entries|Where-Object{
        [string](Get-Prop $_ path)-ceq $Row.Path
    });

    if($entries.Count-ne 1 -or [string](Get-Prop $entries[0] kind)-ne 'Blob'){
        Conflict provider_changed 'The bounded tree page did not prove one exact regular blob.'
    };

    return $result
}
function Test-RelationshipIdentity {
    param($Plan, $Row, $Record)

    if ([string](Get-Prop $Record relationshipKind) -ne $Row.Kind -or
        [string](Get-Prop $Record releaseId) -ne $Plan.ReleaseId -or
        [Int64](Get-Prop $Record remoteProjectId) -ne $Plan.Repository.RemoteProjectId -or
        (Normalize-Origin ([string](Get-Prop $Record instanceBaseUrl))) -ne $Plan.Repository.Origin) {
        return $false
    }

    $sourceSnapshotEqual = [string](Get-Prop $Record sourceSnapshotId) -eq [string]$Row.SourceSnapshotId
    $sourceEventEqual = [string](Get-Prop $Record sourceSelectionEventId) -eq [string]$Row.SourceSelectionEventId
    $targetEqual = [string](Get-Prop $Record targetKind) -eq $Row.TargetKind -and
        [string](Get-Prop $Record targetIdentityId) -eq $Row.TargetId -and
        [string](Get-Prop $Record meaning) -eq $Row.Meaning

    if ($Row.Kind -eq 'MergeRequest') {
        return ([int](Get-Prop $Record mergeRequestIid) -eq $Row.MergeRequestIid -and
            $sourceSnapshotEqual -and $sourceEventEqual -and $targetEqual)
    }

    return ($sourceSnapshotEqual -and $sourceEventEqual -and
        [string](Get-Prop $Record commitSha) -eq $Row.CommitSha -and
        [string](Get-Prop $Record path) -ceq $Row.Path -and
        [Nullable[int]](Get-Prop $Record startLine) -eq $Row.StartLine -and
        [Nullable[int]](Get-Prop $Record endLine) -eq $Row.EndLine -and
        [Nullable[int]](Get-Prop $Record mergeRequestIid) -eq $Row.MergeRequestIidContext -and
        $targetEqual)
}

function Find-ExactRelationship {
    param($Rows, $Plan, $Row)

    $identityCandidates = @($Rows | Where-Object {
        if ([string](Get-Prop $_ relationshipKind) -ne $Row.Kind -or
            [string](Get-Prop $_ releaseId) -ne $Plan.ReleaseId) {
            return $false
        }
        if ($Row.Kind -eq 'MergeRequest') {
            return [int](Get-Prop $_ mergeRequestIid) -eq $Row.MergeRequestIid
        }
        return ([string](Get-Prop $_ sourceSnapshotId) -eq $Row.SourceSnapshotId -and
            [string](Get-Prop $_ commitSha) -eq $Row.CommitSha -and
            [string](Get-Prop $_ path) -ceq $Row.Path)
    })

    $foreign = @($identityCandidates | Where-Object {
        [Int64](Get-Prop $_ remoteProjectId) -ne $Plan.Repository.RemoteProjectId -or
            (Normalize-Origin ([string](Get-Prop $_ instanceBaseUrl))) -ne $Plan.Repository.Origin
    })
    if ($foreign.Count -gt 0) {
        Conflict relationship_conflict 'The provider relationship identity belongs to another configured repository.'
    }

    $matching = @($identityCandidates | Where-Object {
        Test-RelationshipIdentity $Plan $Row $_
    })
    if (@($matching | Where-Object { [bool](Get-Prop $_ isActive) }).Count -gt 1) {
        Conflict relationship_ambiguous 'More than one active exact relationship matched.'
    }
    if (@($matching | Where-Object { -not [bool](Get-Prop $_ isActive) }).Count -gt 0) {
        Conflict relationship_withdrawn 'The exact relationship is withdrawn; re-add is never automatic.'
    }
    # Other typed targets or meanings are separate valid many-to-many edges.
    return $matching | Select-Object -First 1
}
function Get-RelationshipBody {
     param($Plan,$Row) if($Row.Kind-eq'MergeRequest'){
        return [pscustomobject][ordered]@{
            releaseId=$Plan.ReleaseId;

            mergeRequestIid=$Row.MergeRequestIid;

            targetKind=$Row.TargetKind;

            targetId=$Row.TargetId;

            meaning=$Row.Meaning;

            expectedConfigurationVersion=$Plan.Repository.ConfigurationVersion;

            sourceSnapshotId=$Row.SourceSnapshotId;

            sourceSelectionEventId=$Row.SourceSelectionEventId
        }
    };

    return [pscustomobject][ordered]@{
        releaseId=$Plan.ReleaseId;

        sourceSnapshotId=$Row.SourceSnapshotId;

        sourceSelectionEventId=$Row.SourceSelectionEventId;

        commitSha=$Row.CommitSha;

        path=$Row.Path;

        parentPath=$Row.ParentPath;

        cursor=$Row.Cursor;

        pageSize=$Row.PageSize;

        startLine=$Row.StartLine;

        endLine=$Row.EndLine;

        mergeRequestIid=$Row.MergeRequestIidContext;

        targetKind=$Row.TargetKind;

        targetId=$Row.TargetId;

        meaning=$Row.Meaning;

        expectedConfigurationVersion=$Plan.Repository.ConfigurationVersion
    }
}
function New-Journal {
    param($Plan, [string]$ApiOrigin)

    return [pscustomobject]@{
        formatVersion = 1
        manifestDigest = $Plan.Digest
        projectId = $Plan.ProjectId
        releaseId = $Plan.ReleaseId
        apiOrigin = $ApiOrigin
        steps = @()
    }
}

function Assert-SourceExact {
    param($Plan, $SourceState)

    $current = $SourceState.Current
    $snapshot = $SourceState.Snapshot
    if ([Int64](Get-Prop $current version) -ne $Plan.Source.ExpectedSelectionVersion) {
        return $false
    }
    if ($null -ne $Plan.Source.ExpectedSelectionEventId) {
        if ([string](Get-Prop $current selectionEventId) -ne $Plan.Source.ExpectedSelectionEventId) {
            return $false
        }
    }
    elseif ($null -ne (Get-Prop $current selectionEventId)) {
        return $false
    }
    if ($null -eq $snapshot -or
        [string](Get-Prop $snapshot commitSha) -ne $Plan.Source.CommitSha -or
        [string](Get-Prop $snapshot instanceBaseUrl) -ne $Plan.Repository.Origin -or
        [Int64](Get-Prop $snapshot remoteProjectId) -ne $Plan.Repository.RemoteProjectId -or
        [string](Get-Prop $snapshot pathWithNamespace) -cne $Plan.Repository.Path) {
        return $false
    }
    return $true
}

function Assert-SourceResultExact {
    param($Plan, $SourceState, $Entry)

    $result = Get-Prop $Entry result
    $snapshot = $SourceState.Snapshot
    if ($null -eq $result -or $null -eq $snapshot) {
        return $false
    }
    return ([Int64](Get-Prop $SourceState.Current version) -eq [Int64](Get-Prop $result version) -and
        [string](Get-Prop $SourceState.Current selectionEventId) -eq [string](Get-Prop $result selectionEventId) -and
        [string](Get-Prop $snapshot id) -eq [string](Get-Prop $result snapshotId) -and
        [string](Get-Prop $snapshot commitSha) -eq [string](Get-Prop $result commitSha) -and
        [string](Get-Prop $snapshot commitSha) -eq $Plan.Source.CommitSha -and
        [string](Get-Prop $snapshot instanceBaseUrl) -eq $Plan.Repository.Origin -and
        [Int64](Get-Prop $snapshot remoteProjectId) -eq $Plan.Repository.RemoteProjectId -and
        [string](Get-Prop $snapshot pathWithNamespace) -ceq $Plan.Repository.Path)
}

function Get-SourceAnchor {
    param($SourceState)

    if ($null -eq $SourceState.Snapshot) {
        Conflict source_changed 'A current source snapshot is required before relationship authoring.'
    }
    return [pscustomobject]@{
        Version = [Int64](Get-Prop $SourceState.Current version)
        SelectionEventId = [string](Get-Prop $SourceState.Current selectionEventId)
        SnapshotId = [string](Get-Prop $SourceState.Snapshot id)
        CommitSha = [string](Get-Prop $SourceState.Snapshot commitSha)
        InstanceBaseUrl = [string](Get-Prop $SourceState.Snapshot instanceBaseUrl)
        RemoteProjectId = [Int64](Get-Prop $SourceState.Snapshot remoteProjectId)
        Path = [string](Get-Prop $SourceState.Snapshot pathWithNamespace)
    }
}

function Assert-SourceAnchor {
    param($Anchor, $SourceState, $Plan)

    $current = Get-SourceAnchor $SourceState
    if ($current.Version -ne $Anchor.Version -or
        $current.SelectionEventId -ne $Anchor.SelectionEventId -or
        $current.SnapshotId -ne $Anchor.SnapshotId -or
        $current.CommitSha -ne $Anchor.CommitSha -or
        $current.InstanceBaseUrl -ne $Plan.Repository.Origin -or
        $current.RemoteProjectId -ne $Plan.Repository.RemoteProjectId -or
        $current.Path -cne $Plan.Repository.Path) {
        Conflict source_changed 'The selected source changed before the relationship write.'
    }
    return $current
}
function Invoke-AeroLinkResumableDemo {

        [CmdletBinding()] param([Parameter(Mandatory)][string]$ManifestPath,[Parameter(Mandatory)][string]$JournalPath,[Parameter(Mandatory)][string]$ApiBaseUrl,[switch]$Apply,[switch]$ResumePending,[int]$ScanLimit=10000,$ApiClient)
        if($Apply -and $ResumePending){throw 'Apply and ResumePending cannot be used together.'}
        $apiOrigin=Normalize-Origin $ApiBaseUrl
        if ($null -ne $ApiClient -and (Has-Prop $ApiClient Base) -and
            (Normalize-Origin ([string]$ApiClient.Base)) -cne $apiOrigin) {
            Conflict environment_changed 'The supplied API client belongs to a different origin.'
        }
        if($ScanLimit -lt 100){
        throw 'ScanLimit must be at least 100.'
    };

    try{
        $manifest=Get-Content $ManifestPath -Raw|ConvertFrom-Json
    }catch{
        throw 'The manifest is missing or malformed.'
    };

    $plan=Convert-ManifestToPlan $manifest;

    $journal=Read-Journal $JournalPath;

    if($null-ne$journal -and ([string]$journal.manifestDigest -ne $plan.Digest -or (Require-Guid $journal.projectId journal.projectId)-ne$plan.ProjectId -or (Require-Guid $journal.releaseId journal.releaseId)-ne$plan.ReleaseId -or (Normalize-Origin ([string]$journal.apiOrigin))-ne$apiOrigin)){
        Conflict manifest_changed 'The journal belongs to another manifest.'
    }
        if($null -eq $ApiClient){
        $ApiClient=New-AeroLinkResumableClient $ApiBaseUrl -Login
    };

    try{

                $repo=Assert-Repo $ApiClient $plan;

        $sourceState=Read-SourceState $ApiClient $plan;

        $sourceCapabilities=Get-Prop $sourceState.Current capabilities;

        if($null -ne $sourceCapabilities -and [bool](Get-Prop $sourceCapabilities sourceSelectionFrozen)){
            Conflict lifecycle_changed 'The selected release is frozen or released.'
        };

        $historyBefore=$null;

        if($null -ne $plan.Source.ExpectedSelectionEventId){
            # The predecessor event may intentionally point to a different SHA from the requested result.
            $historyBefore=Read-SourceHistoryExact $ApiClient $plan $plan.Source.ExpectedSelectionVersion $plan.Source.ExpectedSelectionEventId '' $ScanLimit
        }
                $rows=Read-AllRelationships $ApiClient $plan $ScanLimit
                if($null -eq $journal){
            $journal=New-Journal $plan $apiOrigin
        }
                $sourceKey="source|$($plan.ReleaseId)";

        $sourceEntry=@($journal.steps|Where-Object{
            [string]$_.stepKey-eq$sourceKey
        })|Select-Object -First 1
                $sourceExact=Assert-SourceExact $plan $sourceState
                if($sourceEntry -and [string]$sourceEntry.state-eq'Applied'){

                        if(-not (Assert-SourceResultExact $plan $sourceState $sourceEntry)){
                Conflict source_changed 'An Applied source step no longer matches its recorded result.'
            }

        } elseif($sourceExact){

                        if(!$Apply -and !$ResumePending){
                $sourceAction='ObservedExisting'
            }else{
                $sourceAction='ObservedExisting';

                if($null-eq$sourceEntry){
                    $sourceEntry=[pscustomobject]@{
                        stepKey=$sourceKey;

                        state='Applied';

                        observation=$sourceAction;

                        request=$null;

                        result=[pscustomobject]@{
                            version=[Int64](Get-Prop $sourceState.Current version);

                            selectionEventId=[string](Get-Prop $sourceState.Current selectionEventId);

                            snapshotId=[string](Get-Prop $sourceState.Snapshot id);

                            commitSha=$plan.Source.CommitSha
                        }
                    };

                    $journal.steps=@($journal.steps)+@($sourceEntry)
                };

                 if($Apply -or $ResumePending){
                    Write-Atomic $JournalPath $journal
                }
            }

        } elseif($ResumePending -or ($sourceEntry -and [string]$sourceEntry.state-eq'Pending')){
            if ($null -eq $sourceEntry) {
                Conflict journal_missing 'Source reconciliation requires the original Pending journal entry.'
            }

                        $transition=Read-SourceTransition $ApiClient $plan $plan.Source.ExpectedSelectionVersion $plan.Source.CommitSha $ScanLimit
                        $after=Read-SourceState $ApiClient $plan
                        $transitionEvent=[string](Get-Prop $transition id)
                        $transitionSnapshot=Get-Prop $transition snapshot
                        if([Int64](Get-Prop $after.Current version) -ne $plan.Source.ExpectedSelectionVersion+1 -or
                            [string](Get-Prop $after.Current selectionEventId) -ne $transitionEvent -or
                            [string](Get-Prop $after.Snapshot id) -ne [string](Get-Prop $transitionSnapshot id)){

                                Conflict source_changed 'The pending source transition does not match current source state.'

            }
                        if($ResumePending){

                                $sourceState = $after

                                if($null -eq $sourceEntry){
                    $sourceEntry=[pscustomobject]@{
                        stepKey=$sourceKey;
                        state='Applied';
                        observation='ObservedExisting';
                        request=$null;
                        result=$null
                    }
                }
                                else{
                    $sourceEntry.state='Applied';
                    $sourceEntry.observation='ObservedExisting'
                }
                                $sourceEntry.result=[pscustomobject]@{
                    version=$plan.Source.ExpectedSelectionVersion+1;
                    selectionEventId=$transitionEvent;
                    snapshotId=[string](Get-Prop $transitionSnapshot id);
                    commitSha=$plan.Source.CommitSha
                }
                                Write-Atomic $JournalPath $journal

            }

        }
                elseif($Apply){
            $body=[pscustomobject][ordered]@{
                releaseId=$plan.ReleaseId;

                reference=$plan.Source.Reference;

                referenceKind=$plan.Source.ReferenceKind;

                previewSha=$plan.Source.CommitSha;

                expectedConfigurationVersion=$plan.Repository.ConfigurationVersion;

                expectedSelectionVersion=$plan.Source.ExpectedSelectionVersion
            };

            if($null-eq$sourceEntry){
                $sourceEntry=[pscustomobject]@{
                    stepKey=$sourceKey;

                    state='Pending';

                    observation=$null;

                    request=$body;

                    result=$null;

                    preparedAt=[DateTimeOffset]::UtcNow.ToString('o');

                    appliedAt=$null
                };

                $journal.steps=@($journal.steps)+@($sourceEntry)
            }else{
                $sourceEntry.state='Pending';

                $sourceEntry.request=$body
            };

            Write-Atomic $JournalPath $journal;

            $post=Invoke-AeroLinkResumableRequest $ApiClient POST "/api/projects/$($plan.ProjectId)/code/source" ($body|ConvertTo-Json -Depth 20 -Compress) -AllowTransportFailure;

            if($post.TransportFailure){
                Conflict ambiguous 'The source POST response was lost;
 use ResumePending.'
            };

            if($post.Status -lt 200 -or $post.Status -ge 300){
                Conflict source_conflict 'The source POST was refused;
 review the Pending journal before retrying.'
            };

            $after=Read-SourceState $ApiClient $plan;

            $returnedEvent=[string](Get-Prop $post.Body selectionEventId);

            $returnedSnapshot=[string](Get-Prop $post.Body snapshotId);

            $returnedVersion=[Int64](Get-Prop $post.Body version);

            if($returnedVersion -ne $plan.Source.ExpectedSelectionVersion+1 -or !$returnedEvent -or !$returnedSnapshot){
                Conflict source_readback 'The source POST did not return a complete identity.'
            };

            if ([Int64](Get-Prop $after.Current version) -ne $returnedVersion -or
                [string](Get-Prop $after.Current selectionEventId) -ne $returnedEvent -or
                [string](Get-Prop $after.Snapshot id) -ne $returnedSnapshot -or
                [string](Get-Prop $after.Snapshot commitSha) -ne $plan.Source.CommitSha) {
                Conflict source_changed 'The source POST readback no longer matches the returned identity.'
            }

            $null=Read-SourceHistoryExact $ApiClient $plan $returnedVersion $returnedEvent $plan.Source.CommitSha $ScanLimit;

            $sourceState = $after

            $sourceEntry.state='Applied';

            $sourceEntry.observation='Applied';

            $sourceEntry.result=[pscustomobject]@{
                version=$returnedVersion;

                selectionEventId=$returnedEvent;

                snapshotId=$returnedSnapshot;

                commitSha=$plan.Source.CommitSha
            };

            $sourceEntry.appliedAt=[DateTimeOffset]::UtcNow.ToString('o');

            Write-Atomic $JournalPath $journal
        }
        $sourceAnchor = $null
        if ($null -ne $sourceState.Snapshot) {
            $sourceAnchor = Get-SourceAnchor $sourceState
        }

        $actions = [Collections.Generic.List[object]]::new()
        if ($sourceEntry -and [string]$sourceEntry.state -eq 'Applied') {
            $actions.Add([pscustomobject]@{ Kind='Source'; Action='AlreadyApplied'; Version=(Get-Prop $sourceEntry.result version); SelectionEventId=(Get-Prop $sourceEntry.result selectionEventId); SnapshotId=(Get-Prop $sourceEntry.result snapshotId) })
        }
        elseif ($sourceExact) {
            $actions.Add([pscustomobject]@{ Kind='Source'; Action='ObservedExisting'; Version=(Get-Prop $sourceState.Current version); SelectionEventId=(Get-Prop $sourceState.Current selectionEventId); SnapshotId=(Get-Prop $sourceState.Snapshot id) })
        }
        else {
            $actions.Add([pscustomobject]@{ Kind='Source'; Action=if($Apply){'ApplySourceSelection'}else{'ProposeSourceSelection'}; CommitSha=$plan.Source.CommitSha })
        }
        foreach($row in $plan.Relationships){
            $null=Assert-Repo $ApiClient $plan;
            $freshSource=Read-SourceState $ApiClient $plan;
            if ($null -eq $sourceAnchor) {
                if ($Apply -or $ResumePending) {
                    Conflict source_changed 'A current source snapshot is required before relationship authoring.'
                }
                $actions.Add([pscustomobject]@{ Kind='Relationship'; Action='ProposeAfterSourceSelection'; Intent=$row })
                continue
            }
            $null = Assert-SourceAnchor $sourceAnchor $freshSource $plan
            $freshCapabilities=Get-Prop $freshSource.Current capabilities;
            if($null -ne $freshCapabilities -and [bool](Get-Prop $freshCapabilities sourceSelectionFrozen)){
                Conflict lifecycle_changed 'The selected release is frozen or released.'
            };
            $rows=Read-AllRelationships $ApiClient $plan $ScanLimit;
            $key=@('relationship',$row.Kind,$plan.ReleaseId,$row.SourceSnapshotId,$row.SourceSelectionEventId,
                $row.MergeRequestIid,$row.MergeRequestIidContext,$row.CommitSha,$row.Path,$row.StartLine,$row.EndLine,
                $row.TargetKind,$row.TargetId,$row.Meaning) -join '|';

            $entry=@($journal.steps|Where-Object{
                [string]$_.stepKey-ceq$key
            })|Select-Object -First 1;

            $existing=Find-ExactRelationship $rows $plan $row;

            if($row.Kind-eq'File'){
                $null=Read-TreeProof $ApiClient $plan $row
            };

            if($entry -and [string]$entry.state-eq'Applied'){
                if($null-eq$existing -or
                    [string](Get-Prop $existing id) -ne [string](Get-Prop $entry.result relationshipId) -or
                    [Int64](Get-Prop $existing version) -ne [Int64](Get-Prop $entry.result version)){
                    Conflict relationship_changed 'An Applied relationship no longer exists exactly.'
                };

                $actions.Add([pscustomobject]@{ Kind='Relationship'; Key=$key; Action='AlreadyApplied' })

                continue
            };

             if($null-ne$existing){
                if($Apply -or $ResumePending){
                    if($null-eq$entry){
                        $entry=[pscustomobject]@{
                            stepKey=$key;

                            state='Applied';

                            observation='ObservedExisting';

                            request=$null;

                            result=[pscustomobject]@{
                                relationshipId=[string](Get-Prop $existing id);

                                version=[Int64](Get-Prop $existing version)
                            }
                        };

                        $journal.steps=@($journal.steps)+@($entry)
                    }else{
                        $entry.state='Applied';

                        $entry.observation='ObservedExisting';

                        $entry.result=[pscustomobject]@{
                            relationshipId=[string](Get-Prop $existing id);

                            version=[Int64](Get-Prop $existing version)
                        }
                    };

                    Write-Atomic $JournalPath $journal
                };

                $actions.Add([pscustomobject]@{ Kind='Relationship'; Key=$key; Action='ObservedExisting' })

                continue
            };

             if($entry-and[string]$entry.state-eq'Pending'){
                Conflict ambiguous 'The relationship POST may have committed;
 exact identity was not observed, so no repost is attempted.'
            };

            if(!$Apply){
                $actions.Add([pscustomobject]@{ Kind='Relationship'; Key=$key; Action=if($ResumePending){'NotApplied'}else{'ProposeRelationship'}; Intent=$row })
                continue
            };

            $beforePostSource = Read-SourceState $ApiClient $plan
            $null = Assert-SourceAnchor $sourceAnchor $beforePostSource $plan
            $body=Get-RelationshipBody $plan $row;

            if($null-eq$entry){
                $entry=[pscustomobject]@{
                    stepKey=$key;

                    state='Pending';

                    observation=$null;

                    request=$body;

                    result=$null;

                    preparedAt=[DateTimeOffset]::UtcNow.ToString('o');

                    appliedAt=$null
                };

                $journal.steps=@($journal.steps)+@($entry)
            }else{
                $entry.state='Pending';

                $entry.request=$body
            };

            Write-Atomic $JournalPath $journal;

            $endpoint=if($row.Kind-eq'MergeRequest'){
                'merge-requests'
            }else{
                'files'
            };

            $post=Invoke-AeroLinkResumableRequest $ApiClient POST "/api/projects/$($plan.ProjectId)/code/relationships/$endpoint" ($body|ConvertTo-Json -Depth 20 -Compress) -AllowTransportFailure;

            if($post.TransportFailure){
                Conflict ambiguous 'The relationship POST response was lost;
 use ResumePending.'
            };

            if($post.Status -lt 200 -or $post.Status -ge 300){
                Conflict relationship_conflict 'The relationship POST was refused;
 review the Pending journal before retrying.'
            };

            $afterPostSource = Read-SourceState $ApiClient $plan
            $null = Assert-SourceAnchor $sourceAnchor $afterPostSource $plan
            $rows=Read-AllRelationships $ApiClient $plan $ScanLimit;

            $existing=Find-ExactRelationship $rows $plan $row;

            if($null-eq$existing){
                Conflict relationship_readback 'The relationship POST did not produce the exact expected identity.'
            };

            $entry.state='Applied';

            $entry.observation='Applied';

            $entry.result=[pscustomobject]@{
                relationshipId=[string](Get-Prop $existing id);

                version=[Int64](Get-Prop $existing version)
            };

            $entry.appliedAt=[DateTimeOffset]::UtcNow.ToString('o');

            Write-Atomic $JournalPath $journal
            $actions.Add([pscustomobject]@{ Kind='Relationship'; Key=$key; Action='Applied'; RelationshipId=$entry.result.relationshipId })
        }
                return [pscustomobject]@{
            Mode=if($ResumePending){
                'ResumePending'
            }elseif($Apply){
                'Apply'
            }else{
                'Preview'
            };

            ManifestDigest=$plan.Digest;

            Steps=@($journal.steps);

            Actions=@($actions)

            JournalWritten=[bool]($Apply -or $ResumePending)
        }

    }finally{
        if($null -eq $PSBoundParameters['ApiClient']){
            Close-AeroLinkResumableClient $ApiClient
        }
    }

}
Export-ModuleMember -Function New-AeroLinkResumableClient,Close-AeroLinkResumableClient,Invoke-AeroLinkResumableRequest,Invoke-AeroLinkResumableDemo,Convert-ManifestToPlan,Get-ManifestDigest

