#Requires -Version 5.1
Set-StrictMode -Version Latest

# Process ACLs, not a PID file, provide the cross-logon access boundary. A supported launcher grants its
# account only query/read/terminate/synchronize rights on a process it has just created and identified.
# Existing ACEs are retained. There is no Everyone grant, debug privilege, token change or elevation here.
if (-not ('AeroLink.ProcessAccess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace AeroLink {
    public static class ProcessAccess {
        [StructLayout(LayoutKind.Sequential)] struct Trustee {
            public IntPtr MultipleTrustee;
            public int MultipleTrusteeOperation;
            public int TrusteeForm;
            public int TrusteeType;
            public IntPtr Name;
        }
        [StructLayout(LayoutKind.Sequential)] struct ExplicitAccess {
            public uint Permissions;
            public int Mode;
            public uint Inheritance;
            public Trustee Trustee;
        }
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("shell32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CommandLineToArgvW(string command, out int count);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateProcess(IntPtr handle, uint exitCode);
        [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr handle, int informationClass, IntPtr information, int length, out int returnLength);
        [StructLayout(LayoutKind.Sequential)] struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
        public sealed class Snapshot {
            public int ProcessId;
            public string StartedAt;
            public string ExecutablePath;
            public string CommandLine;
        }
        public static Snapshot Read(int id) {
            IntPtr handle = OpenProcess(0x1000u, false, id);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr buffer = IntPtr.Zero;
            try {
                long created, exited, kernel, user;
                if (!GetProcessTimes(handle, out created, out exited, out kernel, out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
                StringBuilder image = new StringBuilder(32768);
                int imageLength = image.Capacity;
                if (!QueryFullProcessImageName(handle, 0, image, ref imageLength)) throw new Win32Exception(Marshal.GetLastWin32Error());
                int needed;
                NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out needed);
                if (needed < Marshal.SizeOf(typeof(UnicodeString)) || needed > 1048576) throw new InvalidOperationException("Native command-line query did not provide a bounded result.");
                buffer = Marshal.AllocHGlobal(needed);
                int status = NtQueryInformationProcess(handle, 60, buffer, needed, out needed);
                if (status != 0) throw new InvalidOperationException("Native command-line query failed with NTSTATUS " + status.ToString("X8") + ".");
                UnicodeString command = (UnicodeString)Marshal.PtrToStructure(buffer, typeof(UnicodeString));
                if (command.Length == 0 || command.Length % 2 != 0 || command.Buffer.ToInt64() < buffer.ToInt64() ||
                    command.Buffer.ToInt64() + command.Length > buffer.ToInt64() + needed) throw new InvalidOperationException("Native command-line result is invalid.");
                return new Snapshot { ProcessId = id, StartedAt = DateTime.FromFileTimeUtc(created).ToString("o"),
                    ExecutablePath = image.ToString(), CommandLine = Marshal.PtrToStringUni(command.Buffer, command.Length / 2) };
            } finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); CloseHandle(handle); }
        }
        [DllImport("advapi32.dll")] static extern uint GetSecurityInfo(IntPtr handle, int type, uint information,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode)] static extern uint SetEntriesInAclW(uint count,
            ref ExplicitAccess entry, IntPtr oldAcl, out IntPtr newAcl);
        [DllImport("advapi32.dll")] static extern uint SetSecurityInfo(IntPtr handle, int type, uint information,
            IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        // The handle is opened with WRITE_DAC only by the creating/adopting launcher. The ordinary reader
        // receives no WRITE_DAC, VM_WRITE, CREATE_THREAD or token rights.
        public static void GrantOperator(int id, long expectedCreationUtcTicks) {
            IntPtr handle = OpenProcess(0x00060000u | 0x1000u, false, id);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr descriptor = IntPtr.Zero, acl = IntPtr.Zero, sid = IntPtr.Zero;
            try {
                long created, exited, kernel, user;
                if (!GetProcessTimes(handle, out created, out exited, out kernel, out user))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (DateTime.FromFileTimeUtc(created).Ticks != expectedCreationUtcTicks)
                    throw new InvalidOperationException("Process creation identity changed; no access was granted.");
                IntPtr owner, group, oldAcl, sacl;
                uint result = GetSecurityInfo(handle, 6, 4, out owner, out group, out oldAcl, out sacl, out descriptor);
                if (result != 0) throw new Win32Exception((int)result);
                if (oldAcl == IntPtr.Zero) throw new InvalidOperationException("A null process DACL is not an attributable managed-process contract.");
                SecurityIdentifier operatorSid = WindowsIdentity.GetCurrent().User;
                byte[] bytes = new byte[operatorSid.BinaryLength];
                operatorSid.GetBinaryForm(bytes, 0);
                sid = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, sid, bytes.Length);
                ExplicitAccess entry = new ExplicitAccess {
                    Permissions = 0x00101001u, Mode = 1, Inheritance = 0,
                    Trustee = new Trustee { TrusteeForm = 0, TrusteeType = 1, Name = sid }
                };
                result = SetEntriesInAclW(1, ref entry, oldAcl, out acl);
                if (result != 0) throw new Win32Exception((int)result);
                result = SetSecurityInfo(handle, 6, 4, IntPtr.Zero, IntPtr.Zero, acl, IntPtr.Zero);
                if (result != 0) throw new Win32Exception((int)result);
            } finally {
                if (sid != IntPtr.Zero) Marshal.FreeHGlobal(sid);
                if (acl != IntPtr.Zero) LocalFree(acl);
                if (descriptor != IntPtr.Zero) LocalFree(descriptor);
                CloseHandle(handle);
            }
        }

        public static string[] Arguments(string command) {
            int count;
            IntPtr arguments = CommandLineToArgvW(command, out count);
            if (arguments == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                string[] result = new string[count];
                for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, i * IntPtr.Size));
                return result;
            } finally { LocalFree(arguments); }
        }

        public static int ExitCode(IntPtr handle) {
            uint code;
            if (!GetExitCodeProcess(handle, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)code);
        }

        public static bool Stop(int id, long expectedCreationUtcTicks, string expectedExecutable) {
            IntPtr handle = OpenProcess(0x00101001u, false, id);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                long created, exited, kernel, user;
                if (!GetProcessTimes(handle, out created, out exited, out kernel, out user))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                StringBuilder image = new StringBuilder(32768);
                int length = image.Capacity;
                if (!QueryFullProcessImageName(handle, 0, image, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (DateTime.FromFileTimeUtc(created).Ticks != expectedCreationUtcTicks ||
                    !String.Equals(image.ToString(), expectedExecutable, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Process start/executable identity changed; nothing was stopped.");
                if (!TerminateProcess(handle, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                // Returning false retains the fact that termination WAS requested. Callers record teardown
                // before reporting an incomplete exit. The open handle pins identity across PID reuse.
                return WaitForSingleObject(handle, 10000) == 0;
            } finally { CloseHandle(handle); }
        }
    }
}
'@
}

function Grant-AeroLinkCreatedProcessAccess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][DateTimeOffset]$StartedAt,
        [Parameter(Mandatory)][string]$ExpectedExecutable,
        [Parameter(Mandatory)][string[]]$ExpectedArguments
    )
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop
    if (-not $process -or -not $process.CreationDate -or
        ([DateTimeOffset]$process.CreationDate).UtcDateTime.ToString('yyyyMMddHHmmssffffff') -ne $StartedAt.UtcDateTime.ToString('yyyyMMddHHmmssffffff') -or
        -not [string]::Equals([string]$process.ExecutablePath, $ExpectedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Created process identity does not match; no process access was changed.'
    }
    if ($ExpectedArguments.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$process.CommandLine)) {
        throw 'A managed process requires a readable launch contract before access is granted.'
    }
    foreach ($argument in $ExpectedArguments) {
        if ([string]::IsNullOrWhiteSpace($argument) -or
            ([string]$process.CommandLine).IndexOf($argument, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw 'Created process launch contract does not match; no process access was changed.'
        }
    }
    [AeroLink.ProcessAccess]::GrantOperator($ProcessId, $StartedAt.UtcDateTime.Ticks)
}

function Get-AeroLinkProcessStartIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    try { return $process.StartTime.ToUniversalTime().ToString('o') }
    finally { $process.Dispose() }
}

function Get-AeroLinkNativeProcessIdentity {
    param([Parameter(Mandatory)][int]$ProcessId)
    return [AeroLink.ProcessAccess]::Read($ProcessId)
}

function Stop-AeroLinkProvenProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Process,
        [scriptblock]$OnStopped
    )
    if (-not $Process.PSObject.Properties['StartedAt'] -or -not $Process.StartedAt -or
        [string]::IsNullOrWhiteSpace([string]$Process.ExecutablePath)) {
        throw 'Exact process start/executable proof is unavailable; nothing was stopped.'
    }
    $exited = [AeroLink.ProcessAccess]::Stop([int]$Process.ProcessId,
        ([DateTimeOffset]$Process.StartedAt).UtcDateTime.Ticks, [string]$Process.ExecutablePath)
    if ($OnStopped) { & $OnStopped }
    if (-not $exited) { throw 'Termination was requested for the owned process, but its exit was not proven within ten seconds.' }
}

function Get-AeroLinkProcessDescendantSnapshot {
    <#
      .SYNOPSIS The live descendants of a process, captured with the identity needed to stop them safely.
      .DESCRIPTION
        Must be taken BEFORE the root is terminated, and that ordering is not incidental. Windows reparents
        orphans, so once the root is gone the parent links that identify its descendants are gone with it,
        and what was transient transition work becomes indistinguishable from anything else on the machine.

        Each entry carries ProcessId, StartedAt and ExecutablePath so termination can be identity-proven
        rather than PID-based. A PID alone is not an identity: between the snapshot and the stop, a process
        can exit and its number be reused by something unrelated.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    $all = @()
    try { $all = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop) } catch { return @() }
    $byParent = @{}
    foreach ($entry in $all) {
        $parent = [int]$entry.ParentProcessId
        if (-not $byParent.ContainsKey($parent)) { $byParent[$parent] = [System.Collections.ArrayList]::new() }
        [void]$byParent[$parent].Add($entry)
    }
    $descendants = [System.Collections.ArrayList]::new()
    $seen = @{}
    $queue = [System.Collections.Queue]::new()
    $queue.Enqueue([int]$ProcessId)
    while ($queue.Count -gt 0) {
        $current = [int]$queue.Dequeue()
        if (-not $byParent.ContainsKey($current)) { continue }
        foreach ($child in $byParent[$current]) {
            $childId = [int]$child.ProcessId
            # A cycle is impossible in a real tree, but PID reuse inside one snapshot can fake one.
            if ($seen.ContainsKey($childId)) { continue }
            $seen[$childId] = $true
            # WMI supplies the TOPOLOGY, never the identity used to authorize a stop. Win32_Process
            # CreationDate has one-second granularity, so its ticks never equal the value
            # Stop-AeroLinkProvenProcess compares against (GetProcessTimes, 100 ns) - and the stop then
            # refuses every descendant with "start/executable identity changed". Measured: three live
            # descendants, zero stopped, cleanup reported unproven for a reason that had nothing to do with
            # the processes. So start time and image path come from the process itself, at full precision.
            $startedAt = $null
            $imagePath = [string]$child.ExecutablePath
            try {
                $live = Get-Process -Id $childId -ErrorAction Stop
                $startedAt = $live.StartTime.ToUniversalTime()
                if ($live.Path) { $imagePath = $live.Path }
            }
            catch { $startedAt = $null }
            [void]$descendants.Add([pscustomobject]@{
                    ProcessId      = $childId
                    ParentId       = $current
                    StartedAt      = $startedAt
                    ExecutablePath = $imagePath
                    CommandLine    = [string]$child.CommandLine
                })
            $queue.Enqueue($childId)
        }
    }
    return @($descendants)
}

function Stop-AeroLinkTransientProcessTree {
    <#
      .SYNOPSIS Stops transient work under a root, preserving declared survivors, and reports what is proven.
      .DESCRIPTION
        The distinction this exists to make is between work still PERFORMING a transition and services the
        transition has already RESTORED. A transition deliberately leaves an API and a tunnel running; those
        are its product. Everything else beneath it - a build, an extraction, a restore, a migration - is
        transient work that must be stopped before anything may recover, because a second attempt running
        over live transition work is two writers on one installation.

        So this is deliberately NOT a tree killer. Ids in -PreserveProcessId are never touched, and every
        other descendant is stopped through the same identity-proven path as the root: it terminates only if
        start time and image path still match the snapshot.

        Returns Proven=$false if ANY transient process cannot be shown to have stopped. That is the honest
        answer, and the caller must treat it as "recovery is unsafe" rather than as a tidy-up failure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()]$Descendant,
        [int[]]$PreserveProcessId = @()
    )
    $preserved = @{}
    foreach ($id in $PreserveProcessId) { $preserved[[int]$id] = $true }
    $problems = [System.Collections.Generic.List[string]]::new()
    $stopped = [System.Collections.Generic.List[int]]::new()
    $skipped = [System.Collections.Generic.List[int]]::new()
    foreach ($entry in @($Descendant)) {
        $id = [int]$entry.ProcessId
        if ($preserved.ContainsKey($id)) { [void]$skipped.Add($id); continue }
        # Already gone is the common case, and it is a success rather than a failure.
        $live = $null
        try { $live = Get-Process -Id $id -ErrorAction SilentlyContinue } catch { }
        if (-not $live) { continue }
        if ($null -eq $entry.StartedAt -or [string]::IsNullOrWhiteSpace($entry.ExecutablePath)) {
            # Without start/image proof this cannot be stopped safely, and pretending otherwise risks
            # terminating an unrelated process that inherited the number.
            [void]$problems.Add("PID $id could not be stopped: its start/executable identity was unavailable.")
            continue
        }
        try {
            Stop-AeroLinkProvenProcess -Process ([pscustomobject]@{
                    ProcessId = $id; StartedAt = $entry.StartedAt; ExecutablePath = $entry.ExecutablePath
                })
            [void]$stopped.Add($id)
        }
        catch {
            # A stop that failed BECAUSE the process was already going is not an unproven stop. Terminating
            # the root takes its console host and other short-lived children with it, and those races
            # surfaced as "a device attached to the system is not functioning" against a process that was
            # already gone. What matters is the end state, so re-read it before calling this a problem.
            $failure = $_.Exception.Message
            $stillThere = $null
            try { $stillThere = Get-Process -Id $id -ErrorAction SilentlyContinue } catch { }
            if ($stillThere) { [void]$problems.Add("PID $id could not be stopped: $failure") }
        }
    }
    # Requested is not stopped. Re-read each one before claiming anything.
    foreach ($id in $stopped) {
        try { if ($null -ne (Get-Process -Id $id -ErrorAction SilentlyContinue)) { [void]$problems.Add("PID $id was still running after termination was requested.") } }
        catch { [void]$problems.Add("PID $id could not be re-checked after termination.") }
    }
    return [pscustomobject]@{
        Proven       = ($problems.Count -eq 0)
        StoppedIds   = @($stopped)
        PreservedIds = @($skipped)
        Detail       = if ($problems.Count -eq 0) { 'All transient transition work is proven stopped.' } else { ($problems -join ' | ') }
    }
}

function Push-AeroLinkDeterministicProcessInputEncoding {
    <#
      .SYNOPSIS Pins the encoding a child's redirected stdin will use, and returns what to restore.
      .DESCRIPTION
        A redirected StandardInput is a StreamWriter built over Console.InputEncoding - the AMBIENT console
        codepage, captured when the Process object first hands the property out. On .NET Framework (Windows
        PowerShell 5.1) that encoding is used exactly as given, so under a UTF-8 console (chcp 65001, or a
        host that assigns [Console]::InputEncoding) its preamble is emitted and the child received

            EF BB BF 73 74 6F 70 0D 0A   instead of   73 74 6F 70 0D 0A

        and an exact token match failed. PowerShell 7 wraps the same stream in ConsoleEncoding, which
        suppresses the preamble, so one script stopped the owned API helper under 7 and ran it to its
        diagnostic bound under 5.1 - surfacing as unprovable cleanup rather than as an encoding problem.

        Writing raw bytes to BaseStream is NOT sufficient and that was measured, not assumed: the
        StandardInput getter sets AutoFlush, which flushes the writer as it is created, so the preamble is
        already in the pipe before any caller writes a thing. The encoding therefore has to be pinned BEFORE
        the process starts, which is what this does.

        Fails soft by design. A process with no console cannot set this, and that case is not a failure: the
        helper's reader decodes its stdin as UTF-8 and consumes a leading preamble, so the token still
        matches. Both halves exist because either alone leaves the token host-dependent in one direction.
    #>
    [CmdletBinding()]
    param()
    try {
        $previous = [Console]::InputEncoding
        # UTF-8 WITHOUT the byte-order-mark preamble. ASCII tokens are then byte-identical on every host.
        [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
        return $previous
    }
    catch { return $null }
}

function Pop-AeroLinkDeterministicProcessInputEncoding {
    <#
      .SYNOPSIS Restores the console input encoding captured by Push-AeroLinkDeterministicProcessInputEncoding.
    #>
    [CmdletBinding()]
    param($Previous)
    if ($null -eq $Previous) { return }
    try { [Console]::InputEncoding = $Previous } catch { }
}

function Write-AeroLinkProcessControlToken {
    <#
      .SYNOPSIS Writes a control token to an owned child's redirected stdin as exact bytes.
      .DESCRIPTION
        Paired with Push-AeroLinkDeterministicProcessInputEncoding, which is what actually removes the
        preamble. This writes the token's bytes straight to the underlying stream so that no encoder chosen
        from console state sits between the caller and the pipe for the token itself.

        The token stays an exact match at both ends. This is a byte-determinism fix, not a loosened protocol.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Process,
        [Parameter(Mandatory)][ValidatePattern('^[\x21-\x7E]+$')][string]$Token
    )
    # ASCII by construction, via the validation above: an ASCII-range token has identical bytes in UTF-8, so
    # this writes exactly the token and a CRLF with no preamble and no codepage dependence.
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($Token + "`r`n")
    $stream = $Process.StandardInput.BaseStream
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}

Export-ModuleMember -Function Grant-AeroLinkCreatedProcessAccess, Get-AeroLinkProcessStartIdentity, Get-AeroLinkNativeProcessIdentity, Stop-AeroLinkProvenProcess, Write-AeroLinkProcessControlToken, Push-AeroLinkDeterministicProcessInputEncoding, Pop-AeroLinkDeterministicProcessInputEncoding, Get-AeroLinkProcessDescendantSnapshot, Stop-AeroLinkTransientProcessTree
