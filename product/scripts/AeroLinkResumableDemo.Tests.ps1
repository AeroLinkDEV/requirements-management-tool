#Requires -Version 5.1
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkResumableDemo.psm1') -Force
$failures=[Collections.Generic.List[string]]::new();$roots=[Collections.Generic.List[string]]::new()
function Assert-True([bool]$ok,[string]$message){if(!$ok){$script:failures.Add($message)}}
function Assert-Code([scriptblock]$action,[string]$code,[string]$message){try{&$action|Out-Null;$script:failures.Add("$message (nothing threw)")}catch{Assert-True ($_.Exception.Data['Code'] -eq $code) "$message (code=$($_.Exception.Data['Code']) resp=$($_.Exception.Message))"}}
function New-Manifest { param([long]$SelectionVersion=1,[string]$SelectionEvent='44444444-4444-4444-4444-444444444444',[switch]$Initial,[switch]$Frozen)
    [ordered]@{formatVersion=1;projectId='11111111-1111-1111-1111-111111111111';releaseId='22222222-2222-2222-2222-222222222222';repository=[ordered]@{configurationId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';configurationVersion=6;origin='https://gitlab.com';remoteProjectId=86663796;pathWithNamespace='seanmccarthyns/aerolink-fms-trace-demo'};source=[ordered]@{reference=if($Initial){'main'}else{'cb2f1bd63d0f2c9a1cfd096b33e5f7cb5f409f28'};referenceKind=if($Initial){'Branch'}else{'Commit'};commitSha=('a'*40);expectedSelectionVersion=$SelectionVersion;expectedSelectionEventId=if($Initial){$null}else{$SelectionEvent}};relationships=@([ordered]@{kind='MergeRequest';mergeRequestIid=7;targetKind='RequirementRevision';targetId='66666666-6666-6666-6666-666666666666';meaning='Implements';sourceSnapshotId=$null;sourceSelectionEventId=$null},[ordered]@{kind='File';sourceSnapshotId='33333333-3333-3333-3333-333333333333';sourceSelectionEventId=if($Initial){$null}else{$SelectionEvent};commitSha=('a'*40);path='src/flight_plan.c';parentPath='src';cursor=$null;pageSize=50;startLine=1;endLine=8;mergeRequestIid=7;targetKind='RequirementRevision';targetId='77777777-7777-7777-7777-777777777777';meaning='Implements'})}
}
function New-FakeState { param([switch]$Initial,[switch]$Lost,[switch]$ConfigChanged,[switch]$SourceChanged,[switch]$SourceChangedDuringRelationship,[switch]$Frozen,[switch]$Withdrawn,[switch]$CrossProject,[switch]$ScanOverflow,[switch]$TreeWrong,[switch]$RootTree)
    $mrRow=@{id='88888888-8888-8888-8888-888888888888';relationshipKind='MergeRequest';projectId='11111111-1111-1111-1111-111111111111';releaseId='22222222-2222-2222-2222-222222222222';instanceBaseUrl='https://gitlab.com';remoteProjectId=86663796;isActive=$true;version=1;mergeRequestIid=7;targetKind='RequirementRevision';targetIdentityId='66666666-6666-6666-6666-666666666666';meaning='Implements';sourceSnapshotId=$null;sourceSelectionEventId=$null}
    $server=[hashtable]::Synchronized(@{Posts=0;SourcePosts=0;SourceReads=0;RelationshipPosts=0;TreeReads=0;Lost=[bool]$Lost;Initial=[bool]$Initial;ConfigChanged=[bool]$ConfigChanged;SourceChanged=[bool]$SourceChanged;SourceChangedDuringRelationship=[bool]$SourceChangedDuringRelationship;Frozen=[bool]$Frozen;Withdrawn=[bool]$Withdrawn;CrossProject=[bool]$CrossProject;ScanOverflow=[bool]$ScanOverflow;TreeWrong=[bool]$TreeWrong;RootTree=[bool]$RootTree;FileAdded=$false;ExpectedVersion=if($Initial){0}else{1};Event=if($Initial){$null}else{'44444444-4444-4444-4444-444444444444'};MrRow=$mrRow})
    $state=[pscustomobject]@{Responder=$null};$state.Responder={param($Method,$Path,$Body,$AllowTransportFailure)
        if($Method-eq'POST'){$server.Posts++;if($Path -match '/code/source$'){$server.SourcePosts++;$server.ExpectedVersion=0;$server.Initial=$false;$server.Event='55555555-5555-5555-5555-555555555555';if($server.Lost){return [pscustomobject]@{TransportFailure=$true;Status=$null;Body=$null}};return [pscustomobject]@{TransportFailure=$false;Status=200;Body=@{selectionEventId=$server.Event;snapshotId='33333333-3333-3333-3333-333333333333';version=1;commitSha=('a'*40)}}};$server.LastRelationshipBody=$Body|ConvertFrom-Json;$server.RelationshipPosts++;$new=@{id='99999999-9999-9999-9999-999999999999';relationshipKind='File';projectId='11111111-1111-1111-1111-111111111111';releaseId='22222222-2222-2222-2222-222222222222';instanceBaseUrl='https://gitlab.com';remoteProjectId=86663796;isActive=$true;version=1;sourceSnapshotId='33333333-3333-3333-3333-333333333333';sourceSelectionEventId=$server.LastRelationshipBody.sourceSelectionEventId;commitSha=('a'*40);path='src/flight_plan.c';startLine=1;endLine=8;mergeRequestIid=7;targetKind='RequirementRevision';targetIdentityId='77777777-7777-7777-7777-777777777777';meaning='Implements'};$server.FileAdded=$true;$server.FileRow=$new;if($server.Lost){return [pscustomobject]@{TransportFailure=$true;Status=$null;Body=$null}};return [pscustomobject]@{TransportFailure=$false;Status=200;Body=@{relationshipId=$new.id;version=1;isActive=$true}}}
        if($Path-match'/repository$'){$version=if($server.ConfigChanged){7}else{6};return [pscustomobject]@{TransportFailure=$false;Status=200;Body=@{repository=@{projectId='11111111-1111-1111-1111-111111111111';status='Verified';provider='GitLab';version=$version;endpoint='https://gitlab.com/seanmccarthyns/aerolink-fms-trace-demo';remoteProjectId=86663796;remotePath='seanmccarthyns/aerolink-fms-trace-demo'}}}}
        if($Path-match'/repository/commit\?'){$value=@{sha=('a'*40)};return [pscustomobject]@{TransportFailure=$false;Status=200;Body=@{configurationId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';configurationVersion=6;remoteProjectId=86663796;observation=@{code='ok';value=$value}}}}
        if($Path-match'/code/source/history\?'){$event=if($server.SourceChanged){'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'}else{$server.Event};$items=if($null-eq$event){@()}else{@(@{id=$event;expectedCurrentVersion=$server.ExpectedVersion;resultingVersion=1;snapshot=@{id='33333333-3333-3333-3333-333333333333';commitSha=('a'*40);instanceBaseUrl='https://gitlab.com';remoteProjectId=86663796;pathWithNamespace='seanmccarthyns/aerolink-fms-trace-demo'}})};return [pscustomobject]@{TransportFailure=$false;Status=200;Body=@{total=$items.Count;items=$items}}}
        if($Path-match'/code/source\?'){$server.SourceReads++;$version=if($server.Initial){0}else{1};$changed=$server.SourceChanged -or ($server.SourceChangedDuringRelationship -and $server.SourceReads -gt 1);$event=if($changed){'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'}else{$server.Event};$snap=if($server.Initial){$null}else{@{id='33333333-3333-3333-3333-333333333333';commitSha=('a'*40);instanceBaseUrl='https://gitlab.com';remoteProjectId=86663796;pathWithNamespace='seanmccarthyns/aerolink-fms-trace-demo'}};return [pscustomobject]@{TransportFailure=$false;Status=200;Body=@{version=$version;selectionEventId=$event;snapshot=$snap;capabilities=@{sourceSelectionFrozen=$server.Frozen}}}}
        if($Path-match'/code/relationships\?'){$items=@();if(!$server.ScanOverflow){$items+=,$server.MrRow;if($server.FileAdded){$items+=,$server.FileRow}};if($server.CrossProject){$items=@(@{id='88888888-8888-8888-8888-888888888888';relationshipKind='MergeRequest';projectId='11111111-1111-1111-1111-111111111111';releaseId='22222222-2222-2222-2222-222222222222';instanceBaseUrl='https://other.example';remoteProjectId=999;isActive=$true;version=1;mergeRequestIid=7;targetKind='RequirementRevision';targetIdentityId='66666666-6666-6666-6666-666666666666';meaning='Implements'})};if($server.Withdrawn){$items[0].isActive=$false};if($server.ScanOverflow){return [pscustomobject]@{TransportFailure=$false;Status=200;Body=@{total=10001;items=@()}}};return [pscustomobject]@{TransportFailure=$false;Status=200;Body=@{total=$items.Count;items=$items}}}
        if($Path-match'/tree\?'){$server.TreeReads++;$requested=if($server.RootTree){$null}else{'src'};$entryPath=if($server.RootTree){'flight_plan.c'}elseif($server.TreeWrong){'src/other.c'}else{'src/flight_plan.c'};$value=@{projectId=86663796;commitSha=('a'*40);requestedPath=$requested;entries=@(@{path=$entryPath;kind=if($server.TreeWrong){'Tree'}else{'Blob'}})};return [pscustomobject]@{TransportFailure=$false;Status=200;Body=@{configurationId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';configurationVersion=6;remoteProjectId=86663796;observation=@{code='ok';value=$value}}}}
        throw "Unexpected fake path $Path"
    }.GetNewClosure();return $state,$server
}
$root=Join-Path ([IO.Path]::GetTempPath())('aerolink-source-rel-'+[Guid]::NewGuid().ToString('N'));$roots.Add($root);New-Item -ItemType Directory $root -Force|Out-Null
try {
    # Reject remote cleartext before constructing a client or sending login credentials.
    $remoteRefused=$false
    try { $null=New-AeroLinkResumableClient -ApiBaseUrl 'http://example.invalid' -Login }
    catch { $remoteRefused=$_.Exception.Message -like '*remote APIs require HTTPS*' }
    Assert-True $remoteRefused 'Remote HTTP login must be rejected before credential handling or network access.'
    foreach($origin in @('http://localhost:5098','http://127.0.0.1:5098','http://[::1]:5098','https://example.invalid')) {
        $transportClient=New-AeroLinkResumableClient -ApiBaseUrl $origin
        Close-AeroLinkResumableClient $transportClient
    }
    $manifestPath=Join-Path $root 'manifest.json';$journalPath=Join-Path $root 'journal.json';(New-Manifest)|ConvertTo-Json -Depth 20|Set-Content $manifestPath -Encoding UTF8
    $state,$server=New-FakeState
    $preview=Invoke-AeroLinkResumableDemo $manifestPath $journalPath 'http://fake' -ApiClient $state
    Assert-True ($preview.Mode -eq 'Preview' -and !$preview.JournalWritten -and @($preview.Actions).Count -ge 2 -and $server.TreeReads -eq 1 -and !(Test-Path $journalPath)) 'Preview must perform live preflight without writing and return concrete actions.'
    $applied=Invoke-AeroLinkResumableDemo $manifestPath $journalPath 'http://fake' -Apply -ApiClient $state
    Assert-True ($server.RelationshipPosts -eq 1 -and (Get-Content $journalPath -Raw)-match 'Applied') 'Apply must author one missing relationship and journal Applied.'
    $again=Invoke-AeroLinkResumableDemo $manifestPath $journalPath 'http://fake' -Apply -ApiClient $state
    Assert-True ($server.RelationshipPosts -eq 1 -and @($again.Steps|Where-Object state -eq 'Applied').Count -eq 3) 'Repeated Apply must revalidate and avoid duplicate POSTs.'
    Assert-True ($server.LastRelationshipBody.mergeRequestIid -eq 7 -and $server.LastRelationshipBody.startLine -eq 1 -and $server.LastRelationshipBody.endLine -eq 8) 'File relationship POST must preserve MR context and exact line range.'
    $server.FileRow.version=2
    Assert-Code {Invoke-AeroLinkResumableDemo $manifestPath $journalPath 'http://fake' -Apply -ApiClient $state} 'relationship_changed' 'An Applied relationship version change must not be silently accepted.'

    $originMismatchJournal=Join-Path $root 'origin-mismatch.json';Copy-Item $journalPath $originMismatchJournal
    Assert-Code {Invoke-AeroLinkResumableDemo $manifestPath $originMismatchJournal 'https://fake' -Apply -ApiClient $state} 'manifest_changed' 'A journal must be bound to the canonical API origin.'

    $rootManifest=New-Manifest
    $rootManifest.relationships[1].parentPath=''
    $rootManifest.relationships[1].path='flight_plan.c'
    $rootManifestPath=Join-Path $root 'root-manifest.json'
    $rootJournalPath=Join-Path $root 'root-journal.json'
    $rootManifest|ConvertTo-Json -Depth 20|Set-Content $rootManifestPath -Encoding UTF8
    $rootState,$rootServer=New-FakeState -RootTree
    $rootPreview=Invoke-AeroLinkResumableDemo $rootManifestPath $rootJournalPath 'http://fake' -ApiClient $rootState
    Assert-True ($rootServer.TreeReads -eq 1 -and $rootPreview.Mode -eq 'Preview') 'A root tree page with null requestedPath must prove an empty parent path.'

    $lostPath=Join-Path $root 'lost.json';$lostState,$lostServer=New-FakeState -Lost
    Assert-Code {Invoke-AeroLinkResumableDemo $manifestPath $lostPath 'http://fake' -Apply -ApiClient $lostState} 'ambiguous' 'A lost relationship response must remain Pending.'
    $resumed=Invoke-AeroLinkResumableDemo $manifestPath $lostPath 'http://fake' -ResumePending -ApiClient $lostState
    Assert-True ($resumed.Steps[1].observation -eq 'ObservedExisting' -and $lostServer.RelationshipPosts -eq 1) 'ResumePending must reconcile the exact committed relationship.'

    $initialManifest=Join-Path $root 'initial.json';$initialJournal=Join-Path $root 'initial-journal.json';(New-Manifest -Initial -SelectionVersion 0)|ConvertTo-Json -Depth 20|Set-Content $initialManifest -Encoding UTF8
    $initialState,$initialServer=New-FakeState -Initial
    $initial=Invoke-AeroLinkResumableDemo $initialManifest $initialJournal 'http://fake' -Apply -ApiClient $initialState
    Assert-True ($initialServer.SourcePosts -eq 1 -and @($initial.Steps|Where-Object stepKey -like 'source*').Count -eq 1) 'Initial source selection must use exact CAS and readback.'

    $sourceLostPath=Join-Path $root 'source-lost.json';$sourceLostState,$sourceLostServer=New-FakeState -Initial -Lost;$sourceLostManifest=Join-Path $root 'source-lost-manifest.json';(New-Manifest -Initial -SelectionVersion 0)|ConvertTo-Json -Depth 20|Set-Content $sourceLostManifest -Encoding UTF8;Assert-Code {Invoke-AeroLinkResumableDemo $sourceLostManifest $sourceLostPath 'http://fake' -Apply -ApiClient $sourceLostState} 'ambiguous' 'A lost source response must remain Pending';$sourceResumed=Invoke-AeroLinkResumableDemo $sourceLostManifest $sourceLostPath 'http://fake' -ResumePending -ApiClient $sourceLostState;Assert-True ($sourceLostServer.SourcePosts -eq 1 -and @($sourceResumed.Steps|Where-Object stepKey -like 'source*'|Where-Object observation -eq 'ObservedExisting').Count -eq 1) 'ResumePending must reconcile an exact committed source transition.'

    foreach($case in @(
        @{Name='source ABA';Switch=@{SourceChanged=$true};Code='source_changed'},
        @{Name='source changed during relationship';Switch=@{SourceChangedDuringRelationship=$true};Code='source_changed'},
        @{Name='configuration';Switch=@{ConfigChanged=$true};Code='config_changed'},
        @{Name='frozen';Switch=@{Frozen=$true};Code='lifecycle_changed'},
        @{Name='withdrawn';Switch=@{Withdrawn=$true};Code='relationship_withdrawn'},
        @{Name='cross project';Switch=@{CrossProject=$true};Code='relationship_conflict'},
        @{Name='tree provider';Switch=@{TreeWrong=$true};Code='provider_changed'},
        @{Name='scan limit';Switch=@{ScanOverflow=$true};Code='scan_limit'}
    )){
        $s,$ss=New-FakeState -SourceChanged:([bool]$case.Switch.SourceChanged) -SourceChangedDuringRelationship:([bool]$case.Switch.SourceChangedDuringRelationship) -ConfigChanged:([bool]$case.Switch.ConfigChanged) -Frozen:([bool]$case.Switch.Frozen) -Withdrawn:([bool]$case.Switch.Withdrawn) -CrossProject:([bool]$case.Switch.CrossProject) -TreeWrong:([bool]$case.Switch.TreeWrong) -ScanOverflow:([bool]$case.Switch.ScanOverflow)
        Assert-Code {Invoke-AeroLinkResumableDemo $manifestPath (Join-Path $root ($case.Name+'.json')) 'http://fake' -Apply -ApiClient $s} $case.Code "The $($case.Name) invariant must stop.";Assert-True ($ss.Posts -eq 0) "The $($case.Name) refusal must not POST."
    }
    $malformedJournal=Join-Path $root 'malformed.json';'{}'|Set-Content $malformedJournal -Encoding UTF8;$malformedThrew=$false;try{Invoke-AeroLinkResumableDemo $manifestPath $malformedJournal 'http://fake' -ApiClient $state|Out-Null}catch{$malformedThrew=$true};Assert-True $malformedThrew 'Malformed journals must be rejected before any preflight.'
    $duplicateJournal=Join-Path $root 'duplicate.json';[ordered]@{formatVersion=1;manifestDigest=$preview.ManifestDigest;projectId='11111111-1111-1111-1111-111111111111';releaseId='22222222-2222-2222-2222-222222222222';apiOrigin='http://fake';steps=@([ordered]@{stepKey='duplicate';state='Pending'},[ordered]@{stepKey='duplicate';state='Applied'})}|ConvertTo-Json -Depth 10|Set-Content $duplicateJournal -Encoding UTF8;$duplicateThrew=$false;try{Invoke-AeroLinkResumableDemo $manifestPath $duplicateJournal 'http://fake' -ApiClient $state|Out-Null}catch{$duplicateThrew=$true};Assert-True $duplicateThrew 'Duplicate journal steps must be rejected before any preflight.'
    $secret='synthetic-token-not-recorded';$loginState=[pscustomobject]@{Responder={param($Method,$Path,$Body,$AllowTransportFailure);$server.LoginBody=$Body;return [pscustomobject]@{TransportFailure=$false;Status=200;Body=@{}}}.GetNewClosure()};$loginBody=@{userName='demo';password=$secret}|ConvertTo-Json -Compress;$null=Invoke-AeroLinkResumableRequest $loginState POST '/api/auth/login' $loginBody;$loginBody=$null;Assert-True ((Get-Content $journalPath -Raw)-notmatch [regex]::Escape($secret)) 'Credentials must never enter the journal.'
    Assert-True (@($preview.Actions | Where-Object Action -eq 'ProposeRelationship').Count -eq 1) 'Preview must enumerate the missing file write.'
    $initialAgain=Invoke-AeroLinkResumableDemo $initialManifest $initialJournal 'http://fake' -Apply -ApiClient $initialState
    Assert-True ($initialServer.SourcePosts -eq 1) 'A successfully selected initial source must remain resumable without another POST.'

    $module=Get-Module AeroLinkResumableDemo
    $plan=Convert-ManifestToPlan (New-Manifest)
    $otherTarget=$server.MrRow.Clone()
    $otherTarget.targetIdentityId='bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
    $found=& $module { param($records,$p,$row) Find-ExactRelationship $records $p $row } @($otherTarget) $plan $plan.Relationships[0]
    Assert-True ($null -eq $found) 'Another target on the same MR must allow a distinct many-to-many edge.'
    $caseRow=$server.FileRow.Clone()
    $caseRow.path='src/FLIGHT_PLAN.c'
    $matches=& $module { param($p,$row,$record) Test-RelationshipIdentity $p $row $record } $plan $plan.Relationships[1] $caseRow
    Assert-True (-not $matches) 'Git file paths must not match with different casing.'

    # A real predecessor event at SHA A must permit an explicit version1 -> version2 selection of SHA B.
    $advanceManifest=New-Manifest
    $advanceManifest.source.commitSha='b'*40
    $advanceManifest.source.reference='b'*40
    $advanceManifest.relationships=@()
    $advancePath=Join-Path $root 'advance.json'
    $advanceJournal=Join-Path $root 'advance-journal.json'
    $advanceManifest|ConvertTo-Json -Depth 20|Set-Content $advancePath
    $baseState,$baseServer=New-FakeState
    $baseResponder=$baseState.Responder
    $advanceState=@{Done=$false;Posts=0}
    $advanceClient=[pscustomobject]@{Responder={
        param($Method,$Path,$Body,$AllowTransportFailure)
        $oldSnapshot=@{id='33333333-3333-3333-3333-333333333333';commitSha=('a'*40);instanceBaseUrl='https://gitlab.com';remoteProjectId=86663796;pathWithNamespace='seanmccarthyns/aerolink-fms-trace-demo'}
        $newSnapshot=@{id='cccccccc-cccc-cccc-cccc-cccccccccccc';commitSha=('b'*40);instanceBaseUrl='https://gitlab.com';remoteProjectId=86663796;pathWithNamespace='seanmccarthyns/aerolink-fms-trace-demo'}
        $oldEvent='44444444-4444-4444-4444-444444444444'
        $newEvent='dddddddd-dddd-dddd-dddd-dddddddddddd'
        if($Path -match '/repository/commit\?') {
            $r=& $baseResponder $Method $Path $Body $AllowTransportFailure
            $r.Body.observation.value.sha='b'*40
            return $r
        }
        if($Method -eq 'POST' -and $Path -match '/code/source$') {
            $request=$Body|ConvertFrom-Json
            if($request.expectedSelectionVersion -ne 1 -or $request.previewSha -ne ('b'*40)){throw 'Incorrect advancement CAS.'}
            $advanceState.Done=$true;$advanceState.Posts++
            return [pscustomobject]@{TransportFailure=$false;Status=200;Body=@{selectionEventId=$newEvent;snapshotId=$newSnapshot.id;version=2;commitSha=('b'*40)}}
        }
        if($Path -match '/code/source/history\?') {
            $items=@(@{id=$oldEvent;expectedCurrentVersion=0;resultingVersion=1;snapshot=$oldSnapshot})
            if($advanceState.Done){$items+=@{id=$newEvent;expectedCurrentVersion=1;resultingVersion=2;snapshot=$newSnapshot}}
            return [pscustomobject]@{Status=200;Body=@{items=$items;total=$items.Count}}
        }
        if($Path -match '/code/source\?') {
            return [pscustomobject]@{Status=200;Body=@{version=if($advanceState.Done){2}else{1};selectionEventId=if($advanceState.Done){$newEvent}else{$oldEvent};snapshot=if($advanceState.Done){$newSnapshot}else{$oldSnapshot};capabilities=@{sourceSelectionFrozen=$false}}}
        }
        return & $baseResponder $Method $Path $Body $AllowTransportFailure
    }.GetNewClosure()}
    $null=Invoke-AeroLinkResumableDemo $advancePath $advanceJournal 'http://fake' -Apply -ApiClient $advanceClient
    $null=Invoke-AeroLinkResumableDemo $advancePath $advanceJournal 'http://fake' -Apply -ApiClient $advanceClient
    Assert-True ($advanceState.Posts -eq 1) 'Different-SHA source advancement must succeed once and revalidate its result on repeat.'
} catch { $failures.Add("Unexpected test failure: $($_.Exception.Message) [$($_.ScriptStackTrace)]") }
finally {foreach($p in $roots){$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath());$resolved=[IO.Path]::GetFullPath($p);if(!$resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)-or $resolved.Length -le $tempRoot.Length+8){throw 'Refusing to remove a test path outside the unique temporary test directory.'};if(Test-Path $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue}}}
if($failures.Count){$failures|%{Write-Error $_};throw "Source/relationship utility tests failed: $($failures.Count)"};Write-Host 'AeroLink source/relationship utility tests passed.' -ForegroundColor Green
exit 0
