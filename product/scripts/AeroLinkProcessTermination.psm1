#Requires -Version 5.1
<#
    #1055 canonical process identity and identity-bound termination.

    Astra review 2ec778d5 (F801-1): the previous implementation stored the CIM creation time as a formatted string
    (which loses fractional seconds, so two different lifetimes inside one second compared equal) and terminated
    through a handle that had never been asked for the expected identity. This module owns the one rule:

      * the canonical identity is the native creation FILETIME (100 ns resolution), read with GetProcessTimes;
      * termination opens ONE handle with query + terminate + synchronize rights, reads the creation FILETIME
        THROUGH that handle, compares it with the expected identity, and only then terminates and waits through
        the SAME handle;
      * a mismatch preserves the process; an unreadable identity is Unknown; there is no bare-pid fallback.

    Nothing here decides policy - callers decide what a mismatch means. The module only guarantees that the
    process it terminates is the process it was asked to terminate.
#>

$script:AeroLinkTerminationNativeLoaded = $false
try {
    if (-not ('AeroLink.ProcessControl.NativeTermination' -as [type])) {
        Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace AeroLink.ProcessControl
{
    public static class NativeTermination
    {
        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint WAIT_TIMEOUT = 0x00000102;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr handle, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static long _lastActualCreationFileTime;
        private static int _lastExitCode;
        private static string _lastDetail = string.Empty;

        public static long LastActualCreationFileTime { get { return _lastActualCreationFileTime; } }
        public static int LastExitCode { get { return _lastExitCode; } }
        public static string LastDetail { get { return _lastDetail; } }

        // Status codes: 0 Terminated, 1 AlreadyGone, 2 IdentityMismatch, 3 Unknown, 4 WaitTimeout, 5 TerminateFailed.
        public static int TerminateByIdentity(int pid, long expectedCreationFileTime, int waitMilliseconds)
        {
            _lastActualCreationFileTime = 0;
            _lastExitCode = 0;
            _lastDetail = string.Empty;
            if (pid <= 0) { _lastDetail = "no process id"; return 3; }
            if (expectedCreationFileTime <= 0) { _lastDetail = "no expected creation identity"; return 3; }

            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE, false, pid);
            if (handle == IntPtr.Zero)
            {
                int openError = Marshal.GetLastWin32Error();
                if (openError == 87) { _lastDetail = "no process with pid " + pid; return 1; }   // ERROR_INVALID_PARAMETER
                if (openError == 5) { _lastDetail = "access denied opening pid " + pid; return 3; }
                _lastDetail = "OpenProcess failed with " + openError;
                return 3;
            }

            try
            {
                long creation, exit, kernel, user;
                if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user))
                {
                    _lastDetail = "GetProcessTimes failed with " + Marshal.GetLastWin32Error();
                    return 3;
                }
                _lastActualCreationFileTime = creation;

                // The identity check happens ON THE HANDLE THAT WILL TERMINATE. Two separate queries cannot close
                // the interval in which the pid is reused.
                if (creation != expectedCreationFileTime)
                {
                    _lastDetail = "identity mismatch: expected " + expectedCreationFileTime + ", handle has " + creation;
                    return 2;
                }

                uint currentExitCode;
                if (GetExitCodeProcess(handle, out currentExitCode) && currentExitCode != 259u)
                {
                    _lastExitCode = (int)currentExitCode;
                    _lastDetail = "the process had already exited";
                    return 1;
                }

                if (!TerminateProcess(handle, 1u))
                {
                    _lastDetail = "TerminateProcess failed with " + Marshal.GetLastWin32Error();
                    return 5;
                }

                uint wait = WaitForSingleObject(handle, (uint)waitMilliseconds);
                if (wait == WAIT_OBJECT_0)
                {
                    uint exitCode;
                    if (GetExitCodeProcess(handle, out exitCode)) { _lastExitCode = (int)exitCode; }
                    _lastDetail = "terminated through the verified handle";
                    return 0;
                }
                if (wait == WAIT_TIMEOUT) { _lastDetail = "the process did not exit inside the bounded wait"; return 4; }
                _lastDetail = "WaitForSingleObject returned " + wait;
                return 3;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        // Status codes: 0 Ok, 1 Gone, 3 Unknown. LastActualCreationFileTime carries the identity when Ok.
        public static int CreationFileTime(int pid)
        {
            _lastActualCreationFileTime = 0;
            _lastDetail = string.Empty;
            if (pid <= 0) { _lastDetail = "no process id"; return 3; }
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
            {
                int openError = Marshal.GetLastWin32Error();
                if (openError == 87) { _lastDetail = "no process with pid " + pid; return 1; }
                _lastDetail = "OpenProcess failed with " + openError;
                return 3;
            }
            try
            {
                long creation, exit, kernel, user;
                if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user))
                {
                    _lastDetail = "GetProcessTimes failed with " + Marshal.GetLastWin32Error();
                    return 3;
                }
                uint exitCode;
                if (GetExitCodeProcess(handle, out exitCode) && exitCode != 259u)
                {
                    _lastDetail = "the process has exited";
                    return 1;
                }
                _lastActualCreationFileTime = creation;
                return 0;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
'@
    }
    $script:AeroLinkTerminationNativeLoaded = [bool]('AeroLink.ProcessControl.NativeTermination' -as [type])
}
catch { $script:AeroLinkTerminationNativeLoaded = $false }

function Test-AeroLinkTerminationNative {
    <# True when the native identity-bound termination helper is available on this host. #>
    [CmdletBinding()] param()
    return [bool]$script:AeroLinkTerminationNativeLoaded
}

function Get-AeroLinkProcessSnapshot {
    <# One CIM inventory. Topology comes from here; identities come from the native reader. #>
    [CmdletBinding()] param()
    return @(Get-CimInstance Win32_Process -ErrorAction Stop)
}

function Get-AeroLinkProcessCreationFileTime {
    <# Canonical identity of one live process: the native creation FILETIME. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    if (-not $script:AeroLinkTerminationNativeLoaded) { return [ordered]@{ state = 'Unknown'; creationFileTime = 0L; detail = 'the native termination helper is unavailable' } }
    try { $status = [AeroLink.ProcessControl.NativeTermination]::CreationFileTime($ProcessId) }
    catch { return [ordered]@{ state = 'Unknown'; creationFileTime = 0L; detail = $_.Exception.Message } }
    $detail = [string][AeroLink.ProcessControl.NativeTermination]::LastDetail
    switch ($status) {
        0 { return [ordered]@{ state = 'Ok'; creationFileTime = [long][AeroLink.ProcessControl.NativeTermination]::LastActualCreationFileTime; detail = '' } }
        1 { return [ordered]@{ state = 'Gone'; creationFileTime = 0L; detail = $detail } }
        default { return [ordered]@{ state = 'Unknown'; creationFileTime = 0L; detail = $detail } }
    }
}

function ConvertTo-AeroLinkCreationFileTime {
    <# Exact UTC ISO (round-trip format, as the transition kernel records it) -> native FILETIME. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$IsoUtc)
    if (-not $IsoUtc) { return [long]0 }
    try {
        # RoundtripKind alone: an exact ISO written by the kernel ends in 'Z' (or carries an offset), and combining
        # it with AdjustToUniversal is an invalid DateTimeStyles value on both hosts (measured).
        $parsed = [DateTime]::Parse($IsoUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
        return [long]$parsed.ToFileTimeUtc()
    }
    catch { return [long]0 }
}

function New-AeroLinkProcessIdentity {
    <#
      Full identity of one live process: the canonical creation FILETIME plus diagnostics. `createdUtc` is derived
      from the FILETIME (exact); the formatted CIM string is never used for an ownership decision.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId, [string]$Role = '', [int]$Depth = 0, [string]$Source = '')
    $identity = [ordered]@{ processId = $ProcessId; creationFileTime = 0L; createdUtc = ''; name = ''; image = ''
        role = $Role; depth = $Depth; source = $Source }
    $record = $null
    try { $record = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop } catch { $record = $null }
    if ($record) {
        $identity.name = [string]$record.Name
        $identity.image = [string]$record.ExecutablePath
    }
    $read = Get-AeroLinkProcessCreationFileTime -ProcessId $ProcessId
    if ($read.state -eq 'Ok') {
        $identity.creationFileTime = [long]$read.creationFileTime
        $identity.createdUtc = [DateTime]::FromFileTimeUtc([long]$read.creationFileTime).ToString('o')
    }
    return $identity
}

function Test-AeroLinkProcessIdentity {
    <# Match | Gone | Reused | Unknown, on the canonical FILETIME only. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Identity)
    $processId = 0; $expected = [long]0
    if ($Identity -is [System.Collections.IDictionary]) {
        if ($Identity.Contains('processId')) { $processId = [int]$Identity['processId'] }
        if ($Identity.Contains('creationFileTime')) { $expected = [long]$Identity['creationFileTime'] }
    }
    else {
        if ($Identity.PSObject.Properties['processId']) { $processId = [int]$Identity.processId }
        if ($Identity.PSObject.Properties['creationFileTime']) { $expected = [long]$Identity.creationFileTime }
    }
    if ($processId -le 0) { return [ordered]@{ state = 'Unknown'; detail = 'no process id was recorded'; live = $null } }
    if ($expected -le 0) { return [ordered]@{ state = 'Unknown'; detail = "no full-precision creation identity was recorded for pid $processId"; live = $null } }
    $read = Get-AeroLinkProcessCreationFileTime -ProcessId $processId
    if ($read.state -eq 'Gone') { return [ordered]@{ state = 'Gone'; detail = ''; live = $null } }
    if ($read.state -eq 'Unknown') { return [ordered]@{ state = 'Unknown'; detail = $read.detail; live = $null } }
    if ([long]$read.creationFileTime -ne $expected) {
        return [ordered]@{ state = 'Reused'; detail = "pid $processId now has creation identity $($read.creationFileTime), expected $expected"; live = $read }
    }
    return [ordered]@{ state = 'Match'; detail = ''; live = $read }
}

function Stop-AeroLinkProcessByIdentity {
    <#
      Terminate exactly the process whose identity matches, through one verified handle. Returns
      Stopped | AlreadyGone | IdentityMismatch | WaitTimeout | Unknown. There is no bare-pid fallback.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][long]$CreationFileTime, [int]$WaitMilliseconds = 15000)
    if (-not $script:AeroLinkTerminationNativeLoaded) { return [ordered]@{ state = 'Unknown'; processId = $ProcessId; detail = 'the native termination helper is unavailable' } }
    if ($CreationFileTime -le 0) { return [ordered]@{ state = 'Unknown'; processId = $ProcessId; detail = 'no full-precision creation identity was supplied' } }
    try { $status = [AeroLink.ProcessControl.NativeTermination]::TerminateByIdentity($ProcessId, $CreationFileTime, $WaitMilliseconds) }
    catch { return [ordered]@{ state = 'Unknown'; processId = $ProcessId; detail = $_.Exception.Message } }
    $detail = [string][AeroLink.ProcessControl.NativeTermination]::LastDetail
    $actual = [long][AeroLink.ProcessControl.NativeTermination]::LastActualCreationFileTime
    $exitCode = [int][AeroLink.ProcessControl.NativeTermination]::LastExitCode
    switch ($status) {
        0 { return [ordered]@{ state = 'Stopped'; processId = $ProcessId; exitCode = $exitCode; actualCreationFileTime = $actual; detail = $detail } }
        1 { return [ordered]@{ state = 'AlreadyGone'; processId = $ProcessId; actualCreationFileTime = $actual; detail = $detail } }
        2 { return [ordered]@{ state = 'IdentityMismatch'; processId = $ProcessId; actualCreationFileTime = $actual; detail = $detail } }
        4 { return [ordered]@{ state = 'WaitTimeout'; processId = $ProcessId; actualCreationFileTime = $actual; detail = $detail } }
        default { return [ordered]@{ state = 'Unknown'; processId = $ProcessId; actualCreationFileTime = $actual; detail = $detail } }
    }
}

function Get-AeroLinkOwnedTreeIdentities {
    <#
      Identities of the live descendants of a VERIFIED root (root included). The root is verified natively against
      the recorded FILETIME, and the SAME topology snapshot that selects descendants is the one validated: a link
      is only accepted when every process in the chain is live in that snapshot and every parent predates its
      child, so a replacement root or a stale parent pid cannot be adopted.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$RootIdentity, [int]$MaxDepth = 12)
    $rootPid = 0; $expected = [long]0
    if ($RootIdentity -is [System.Collections.IDictionary]) {
        if ($RootIdentity.Contains('processId')) { $rootPid = [int]$RootIdentity['processId'] }
        if ($RootIdentity.Contains('creationFileTime')) { $expected = [long]$RootIdentity['creationFileTime'] }
    }
    else {
        if ($RootIdentity.PSObject.Properties['processId']) { $rootPid = [int]$RootIdentity.processId }
        if ($RootIdentity.PSObject.Properties['creationFileTime']) { $expected = [long]$RootIdentity.creationFileTime }
    }
    $verified = Test-AeroLinkProcessIdentity -Identity ([ordered]@{ processId = $rootPid; creationFileTime = $expected })
    if ($verified.state -ne 'Match') {
        return [ordered]@{ state = $verified.state; identities = @(); detail = [string]$verified.detail }
    }
    $snapshot = @(Get-AeroLinkProcessSnapshot)
    $byId = @{}
    foreach ($process in $snapshot) { $byId[[int]$process.ProcessId] = $process }
    if (-not $byId.ContainsKey($rootPid)) {
        # The recorded root left the inventory after the native verification: nothing is adopted from it.
        return [ordered]@{ state = 'Gone'; identities = @(); detail = 'the verified root left the selection inventory' }
    }
    # Re-verify the root against the inventory we are about to select from: if the pid was reused between the
    # identity check and this snapshot, the replacement must not become an owned root.
    $recheck = Get-AeroLinkProcessCreationFileTime -ProcessId $rootPid
    if ($recheck.state -eq 'Gone') { return [ordered]@{ state = 'Gone'; identities = @(); detail = 'the verified root exited before the selection inventory was validated' } }
    if ($recheck.state -ne 'Ok') { return [ordered]@{ state = 'Unknown'; identities = @(); detail = [string]$recheck.detail } }
    if ([long]$recheck.creationFileTime -ne $expected) {
        return [ordered]@{ state = 'Reused'; identities = @(); detail = 'the pid was reused between the identity check and the selection inventory' }
    }
    $identityCache = @{}
    $identityCache[$rootPid] = $expected
    $owned = [System.Collections.Generic.List[object]]::new()
    foreach ($process in $snapshot) {
        $cursor = $process; $depth = 0; $walked = @(); $chain = @()
        $reachedRoot = $false
        while ($cursor -and $depth -lt $MaxDepth) {
            $cursorId = [int]$cursor.ProcessId
            if ($cursorId -eq $rootPid) { $reachedRoot = $true; break }
            $chain += $cursorId
            $walked += $cursorId
            $parentId = 0
            try { $parentId = [int]$cursor.ParentProcessId } catch { $parentId = 0 }
            if ($parentId -gt 0 -and $byId.ContainsKey($parentId) -and ($walked -notcontains $parentId)) { $cursor = $byId[$parentId] } else { $cursor = $null }
            $depth++
        }
        if (-not $reachedRoot) { continue }
        # Exact identities for the candidate and every intermediate link; a link whose parent postdates it is a
        # stale parent pid (its real parent is gone and the pid was reused), never ownership.
        $candidateId = [int]$process.ProcessId
        if (-not $identityCache.ContainsKey($candidateId)) {
            $read = Get-AeroLinkProcessCreationFileTime -ProcessId $candidateId
            if ($read.state -ne 'Ok') { continue }
            $identityCache[$candidateId] = [long]$read.creationFileTime
        }
        $chainOk = $true
        $previousId = $candidateId
        foreach ($linkId in @($chain)) {
            if (-not $identityCache.ContainsKey($linkId)) {
                $read = Get-AeroLinkProcessCreationFileTime -ProcessId $linkId
                if ($read.state -ne 'Ok') { $chainOk = $false; break }
                $identityCache[$linkId] = [long]$read.creationFileTime
            }
            if ([long]$identityCache[$linkId] -gt [long]$identityCache[$previousId]) { $chainOk = $false; break }
            $previousId = $linkId
        }
        # The verified root must also predate its own child (the last link, or the candidate when the chain is just
        # the root). A child that is OLDER than the pid it points at means its real parent is gone and that pid was
        # reused by our root - an old parent pid is not ownership.
        if ($chainOk -and [long]$expected -gt [long]$identityCache[$previousId]) { $chainOk = $false }
        if (-not $chainOk) { continue }
        $owned.Add([ordered]@{ processId = $candidateId; creationFileTime = [long]$identityCache[$candidateId]
            createdUtc = [DateTime]::FromFileTimeUtc([long]$identityCache[$candidateId]).ToString('o')
            name = [string]$process.Name; image = [string]$process.ExecutablePath; depth = $depth; source = 'live-parent-chain' })
    }
    return [ordered]@{ state = 'Match'; identities = @($owned.ToArray()); detail = '' }
}

function Stop-AeroLinkVerifiedIdentity {
    <#
      Verify the identity, then terminate through the identity-bound native operation. A reused pid is preserved
      and reported as PidReused; an unreadable or identity-less record is Unknown. Never falls back to a bare pid.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Identity, [int]$WaitSeconds = 15)
    $processId = 0
    if ($Identity -is [System.Collections.IDictionary]) { if ($Identity.Contains('processId')) { $processId = [int]$Identity['processId'] } }
    elseif ($Identity.PSObject.Properties['processId']) { $processId = [int]$Identity.processId }
    $verified = Test-AeroLinkProcessIdentity -Identity $Identity
    if ($verified.state -eq 'Gone') { return [ordered]@{ processId = $processId; state = 'AlreadyGone'; detail = '' } }
    if ($verified.state -eq 'Reused') { return [ordered]@{ processId = $processId; state = 'PidReused'; detail = [string]$verified.detail } }
    if ($verified.state -eq 'Unknown') { return [ordered]@{ processId = $processId; state = 'Unknown'; detail = [string]$verified.detail } }
    $creationFileTime = [long]$Identity.creationFileTime
    if ($creationFileTime -le 0 -and $Identity -is [System.Collections.IDictionary] -and $Identity.Contains('creationFileTime')) { $creationFileTime = [long]$Identity['creationFileTime'] }
    $outcome = Stop-AeroLinkProcessByIdentity -ProcessId $processId -CreationFileTime $creationFileTime -WaitMilliseconds ([Math]::Max(1, $WaitSeconds) * 1000)
    switch ($outcome.state) {
        'Stopped' { return [ordered]@{ processId = $processId; state = 'Stopped'; exitCode = [int]$outcome.exitCode; detail = [string]$outcome.detail } }
        'AlreadyGone' { return [ordered]@{ processId = $processId; state = 'AlreadyGone'; detail = [string]$outcome.detail } }
        'IdentityMismatch' { return [ordered]@{ processId = $processId; state = 'PidReused'; detail = [string]$outcome.detail } }
        default { return [ordered]@{ processId = $processId; state = 'Unknown'; detail = [string]$outcome.detail } }
    }
}

Export-ModuleMember -Function Test-AeroLinkTerminationNative, Get-AeroLinkProcessSnapshot, Get-AeroLinkProcessCreationFileTime,
    ConvertTo-AeroLinkCreationFileTime, New-AeroLinkProcessIdentity, Test-AeroLinkProcessIdentity, Stop-AeroLinkProcessByIdentity,
    Get-AeroLinkOwnedTreeIdentities, Stop-AeroLinkVerifiedIdentity
