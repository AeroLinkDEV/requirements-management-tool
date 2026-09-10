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

Export-ModuleMember -Function Grant-AeroLinkCreatedProcessAccess, Get-AeroLinkProcessStartIdentity, Get-AeroLinkNativeProcessIdentity, Stop-AeroLinkProvenProcess
