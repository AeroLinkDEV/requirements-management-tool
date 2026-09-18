#Requires -Version 5.1
<#
    The native layer and durable records for HOME transitions (#1041, #1043, #1053).

    Everything above this module - the completion witness, the transition job, admission, the launch authority -
    depends on two properties that a PowerShell process object cannot give:

      * CONTAINMENT FROM CREATION. A process that must be collected with a transition is created suspended INSIDE
        the transition's job, so there is no moment at which it runs unassigned. Its creation handle is retained,
        so its exit status is read from the kernel and never inferred from a pid.
      * NO INHERITED CALLER HANDLES. A service that must outlive the transition is created with an explicit handle
        list naming only its own log files. It therefore never holds the caller's redirected stdout open, which is
        the #1053 hang: a caller waiting for EOF on a pipe held by the API it just restored.

    Every query answers affirmatively or reports Unknown. Nothing here turns "could not look" into "absent", and
    every close is idempotent because the field it closes is zeroed.

    The C# namespace carries a version. Add-Type cannot replace a loaded type, so a long-lived session that imported
    an older copy would otherwise keep using it silently; a changed native layer must change the namespace.
#>
Set-StrictMode -Version Latest

$script:TransitionKernelSource = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AeroLink.TransitionV1 {
    [StructLayout(LayoutKind.Sequential)] public struct BasicLimits {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] public struct IoCounters { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)] public struct ExtendedLimits {
        public BasicLimits Basic; public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] public struct SecurityAttributes { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    // One kernel observation of a job's membership: the count and the ids come from the SAME call.
    public sealed class Membership { public int Assigned; public int[] ProcessIds; }

    public sealed class ProbeResult {
        public string State;     // Absent | Present | Unknown
        public int Error;
        public override string ToString() { return State + (Error != 0 ? ":" + Error : ""); }
    }

    public sealed class Staged {
        public IntPtr Process, Thread;
        public int ProcessId;
        public string StartedAtUtc, ImagePath;
    }

    public sealed class LaunchSpec {
        public string CommandLine;
        public string WorkingDirectory;
        // Null inherits this process's environment. Otherwise the COMPLETE block for the child.
        public Dictionary<string, string> Environment;
        // Null or empty discards that stream (NUL). The same path for both opens one handle.
        public string StandardOutputPath;
        public string StandardErrorPath;
        public bool Breakaway;
        // Create under a restricted copy of this process's token with Administrators and Power Users deny-only and
        // every privilege but change-notify removed - the token PostgreSQL requires (it refuses administrative
        // tokens) and the one pg_ctl itself builds. A restricted copy of the caller's own token is assignable
        // without any privilege.
        public bool RestrictAdministrators;
    }

    public sealed class TokenFacts {
        public bool Elevated;
        public string ElevationType;        // Default | Full | Limited | Unknown
        public string IntegrityLevel;       // Untrusted | Low | Medium | High | System | rid:<n>
        public int IntegrityRid;
        public string AdministratorsGroup;  // Enabled | DenyOnly | Absent | Present:<attributes>
        public bool HasLinkedToken;
        public int SessionId;
        public string[] LogonSids;
    }

    public static class Kernel {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateJobObjectW(ref SecurityAttributes sa, string name);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateJobObjectW(IntPtr sa, string name);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr OpenJobObjectW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr j, int c, ref ExtendedLimits i, int l);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr j, int c, ref ExtendedLimits i, int l, IntPtr r);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr j, int c, IntPtr i, int l, IntPtr r);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(IntPtr j, uint code);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool IsProcessInJob(IntPtr p, IntPtr j, out bool r);
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern int GetCurrentProcessId();
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint rev, out IntPtr sd, out uint size);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
        [DllImport("kernel32.dll", SetLastError=true)]
        static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);

        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
            uint flags, IntPtr env, string dir, ref StartupInfoEx si, out ProcessInformation pi);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern bool CreateProcessAsUserW(IntPtr token, string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
            uint flags, IntPtr env, string dir, ref StartupInfoEx si, out ProcessInformation pi);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr prev, IntPtr returned);
        [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr t);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateProcess(IntPtr p, uint c);
        [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessTimes(IntPtr h, out long c, out long e, out long k, out long u);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageNameW(IntPtr h, uint f, StringBuilder n, ref int s);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, ref SecurityAttributes sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetFilePointerEx(IntPtr file, long distance, IntPtr newPointer, uint method);

        [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int length, out int returned);
        [DllImport("advapi32.dll", SetLastError=true)]
        static extern bool CreateRestrictedToken(IntPtr existing, uint flags, int disableCount, SidAndAttributes[] disable,
            int deleteCount, IntPtr deletePrivileges, int restrictCount, IntPtr restrict, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool ConvertStringSidToSidW([MarshalAs(UnmanagedType.LPWStr)] string sid, out IntPtr psid);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool ProcessIdToSessionId(int pid, out int session);

        [StructLayout(LayoutKind.Sequential)] public struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx {
            public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError; public IntPtr lpAttributeList; }
        [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        public const uint LimitBreakawayOk = 0x00000800, LimitSilentBreakawayOk = 0x00001000, LimitKillOnClose = 0x00002000;
        const uint JobAllAccess = 0x1F001F;
        const uint JobQuery = 0x0004 | 0x00100000;
        const int ErrorAlreadyExists = 183, ErrorFileNotFound = 2;
        public const string PlacementProtocol = "aerolink-placement-1";

        // ---------------- Jobs ----------------

        // Refuses a name that already exists. CreateJobObject silently OPENS an existing object of that name and
        // reports ERROR_ALREADY_EXISTS, so without this check another attempt's job would be adopted as ours.
        public static IntPtr CreateJob(string name, uint limitFlags, string sddl) {
            IntPtr job;
            if (sddl != null) {
                IntPtr sd; uint size;
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, out size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try {
                    SecurityAttributes sa = new SecurityAttributes();
                    sa.nLength = Marshal.SizeOf(typeof(SecurityAttributes)); sa.lpSecurityDescriptor = sd; sa.bInheritHandle = false;
                    job = CreateJobObjectW(ref sa, name);
                } finally { LocalFree(sd); }
            } else {
                job = CreateJobObjectW(IntPtr.Zero, name);
            }
            int error = Marshal.GetLastWin32Error();
            if (job == IntPtr.Zero) throw new Win32Exception(error);
            if (name != null && error == ErrorAlreadyExists) {
                CloseHandle(job);
                throw new InvalidOperationException("JobNameCollision: a job named '" + name + "' already exists; it is not ours and is not used.");
            }
            try { SetLimits(job, limitFlags); } catch { CloseHandle(job); throw; }
            return job;
        }
        public static void SetLimits(IntPtr job, uint flags) {
            ExtendedLimits l = new ExtendedLimits(); l.Basic.LimitFlags = flags;
            if (!SetInformationJobObject(job, 9, ref l, Marshal.SizeOf(typeof(ExtendedLimits)))) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        // IntPtr.Zero queries the job the CALLING process belongs to (the immediate one).
        public static uint LimitFlags(IntPtr job) {
            ExtendedLimits l = new ExtendedLimits();
            if (!QueryInformationJobObject(job, 9, ref l, Marshal.SizeOf(typeof(ExtendedLimits)), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return l.Basic.LimitFlags;
        }
        public static Membership Members(IntPtr job) {
            for (int capacity = 64; capacity <= 16384; capacity *= 4) {
                int size = 8 + IntPtr.Size * capacity;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try {
                    Marshal.WriteInt32(buffer, 0, capacity); Marshal.WriteInt32(buffer, 4, 0);
                    if (!QueryInformationJobObject(job, 3, buffer, size, IntPtr.Zero)) {
                        int e = Marshal.GetLastWin32Error();
                        if (e == 234) continue;
                        throw new Win32Exception(e);
                    }
                    Membership m = new Membership();
                    m.Assigned = Marshal.ReadInt32(buffer, 0);
                    int returned = Marshal.ReadInt32(buffer, 4);
                    if (returned != m.Assigned) continue;   // truncated: never report a partial set
                    m.ProcessIds = new int[returned];
                    for (int i = 0; i < returned; i++) m.ProcessIds[i] = (int)(long)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
                    return m;
                } finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidOperationException("job membership exceeded the supported bound");
        }
        public static IntPtr OpenJobQuery(string name) {
            IntPtr h = OpenJobObjectW(JobQuery, false, name);
            if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            return h;
        }
        // The transition job's DACL: this user may only QUERY/SYNCHRONIZE by name. Nobody can assign, terminate or
        // change limits through the name; only the creator's handle and handles DUPLICATED from it can.
        public static string QueryOnlySddl() {
            return "D:P(A;;0x00100004;;;" + WindowsIdentity.GetCurrent().User.Value + ")";
        }
        // Places a copy of 'handle' in the target process and returns its value THERE.
        public static long DuplicateInto(IntPtr handle, IntPtr targetProcess) {
            IntPtr remote;
            if (!DuplicateHandle(GetCurrentProcess(), handle, targetProcess, out remote, 0, false, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return (long)remote;
        }
        // A synchronize handle bound to the identity recorded for it; zero when the identity does not match.
        public static IntPtr OpenProcessWait(int pid, string expectedStartedAtIso) {
            IntPtr h = OpenProcess(0x00100000 | 0x1000, false, pid);
            if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            long c, e, k, u;
            if (!GetProcessTimes(h, out c, out e, out k, out u) || DateTime.FromFileTimeUtc(c).ToString("o") != expectedStartedAtIso) { CloseHandle(h); return IntPtr.Zero; }
            return h;
        }
        // 0 = signaled (exited), 258 = still running, anything else = unknown.
        public static uint WaitHandle(IntPtr h, uint ms) { return WaitForSingleObject(h, ms); }
        // Terminates pid ONLY if the kernel confirms, through the same handle, that it is a member of 'job'.
        public static string TerminateVerifiedMember(IntPtr job, int pid) {
            IntPtr h = OpenProcess(0x0001 | 0x1000 | 0x00100000, false, pid);
            if (h == IntPtr.Zero) {
                int e = Marshal.GetLastWin32Error();
                return e == 87 ? "Gone" : "Unknown:open-" + e;
            }
            try {
                bool member;
                if (!IsProcessInJob(h, job, out member)) return "Unknown:membership-" + Marshal.GetLastWin32Error();
                if (!member) return "NotMember";
                uint code;
                if (GetExitCodeProcess(h, out code) && code != 259u) return "Gone";
                if (!TerminateProcess(h, 1)) return "Unknown:terminate-" + Marshal.GetLastWin32Error();
                return WaitForSingleObject(h, 10000) == 0 ? "Stopped" : "Unknown:wait";
            } finally { CloseHandle(h); }
        }
        // Existence of a NAMED job. Only ERROR_FILE_NOT_FOUND is absence; anything else is Unknown.
        public static ProbeResult Probe(string name) {
            IntPtr h = OpenJobObjectW(JobQuery, false, name);
            if (h != IntPtr.Zero) { CloseHandle(h); return new ProbeResult { State = "Present" }; }
            int e = Marshal.GetLastWin32Error();
            if (e == ErrorFileNotFound) return new ProbeResult { State = "Absent", Error = e };
            return new ProbeResult { State = "Unknown", Error = e };
        }
        public static void Terminate(IntPtr job) { if (!TerminateJobObject(job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        // Throws when unanswerable. pid 0 = this process; job zero asks "is it in ANY job".
        public static bool InJob(int pid, IntPtr job) {
            IntPtr p = pid == 0 ? GetCurrentProcess() : OpenProcess(0x1000, false, pid);
            if (p == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { bool r; if (!IsProcessInJob(p, job, out r)) throw new Win32Exception(Marshal.GetLastWin32Error()); return r; }
            finally { if (pid != 0) CloseHandle(p); }
        }
        public static void CloseHandleChecked(IntPtr h) { if (h != IntPtr.Zero) CloseHandle(h); }

        // ---------------- Processes ----------------

        const uint CreateSuspended = 0x4, CreateNoWindow = 0x08000000, CreateBreakawayFromJob = 0x01000000,
            CreateNewProcessGroup = 0x200, ExtendedStartupInfoPresent = 0x00080000, CreateUnicodeEnvironment = 0x400;
        const int StartfUseStdHandles = 0x100;
        static readonly IntPtr AttributeHandleList = (IntPtr)0x00020002;
        static readonly IntPtr AttributeJobList = (IntPtr)0x0002000D;

        static IntPtr OpenInheritable(string path, bool forWrite) {
            SecurityAttributes sa = new SecurityAttributes();
            sa.nLength = Marshal.SizeOf(typeof(SecurityAttributes)); sa.bInheritHandle = true;
            // Read | Write | Delete sharing: a log reader must never be able to fail the service writing it.
            uint access = forWrite ? 0x40000000u : 0x80000000u;
            uint disposition = forWrite ? 4u : 3u;   // OPEN_ALWAYS for logs (appended), OPEN_EXISTING for NUL input
            IntPtr h = CreateFileW(path, access, 0x1 | 0x2 | 0x4, ref sa, disposition, 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "cannot open '" + path + "'");
            if (forWrite && !SetFilePointerEx(h, 0, IntPtr.Zero, 2)) { int e = Marshal.GetLastWin32Error(); CloseHandle(h); throw new Win32Exception(e); }
            return h;
        }

        static IntPtr BuildEnvironment(Dictionary<string, string> environment) {
            List<string> keys = new List<string>(environment.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            StringBuilder block = new StringBuilder();
            foreach (string key in keys) {
                if (string.IsNullOrEmpty(key) || key.IndexOf('=') > 0 || key.IndexOf('\0') >= 0) throw new ArgumentException("invalid environment name '" + key + "'");
                string value = environment[key] ?? "";
                if (value.IndexOf('\0') >= 0) throw new ArgumentException("invalid environment value for '" + key + "'");
                block.Append(key).Append('=').Append(value).Append('\0');
            }
            block.Append('\0');
            return Marshal.StringToHGlobalUni(block.ToString());
        }

        static IntPtr RestrictedAdministratorsToken() {
            IntPtr token, restricted, admins = IntPtr.Zero, powerUsers = IntPtr.Zero;
            // TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY
            if (!OpenProcessToken(GetCurrentProcess(), 0x0002 | 0x0001 | 0x0008, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                if (!ConvertStringSidToSidW("S-1-5-32-544", out admins) || !ConvertStringSidToSidW("S-1-5-32-547", out powerUsers)) throw new Win32Exception(Marshal.GetLastWin32Error());
                SidAndAttributes[] disable = new SidAndAttributes[2];
                disable[0].Sid = admins; disable[1].Sid = powerUsers;
                // DISABLE_MAX_PRIVILEGE: every privilege except SeChangeNotifyPrivilege is removed.
                if (!CreateRestrictedToken(token, 0x1, 2, disable, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return restricted;
            } finally {
                CloseHandle(token);
                if (admins != IntPtr.Zero) LocalFree(admins);
                if (powerUsers != IntPtr.Zero) LocalFree(powerUsers);
            }
        }

        // Suspended, a member of 'job' AT CREATION (job zero: no job is requested), inheriting ONLY the handles
        // named for its standard streams. The handle pair is RETAINED: the caller resumes, terminates and closes
        // through it, so a failure path can always clean up.
        public static Staged Launch(IntPtr job, LaunchSpec spec) {
            if (spec == null || string.IsNullOrEmpty(spec.CommandLine)) throw new ArgumentException("a command line is required");
            List<IntPtr> inherited = new List<IntPtr>();
            IntPtr stdin = IntPtr.Zero, stdout = IntPtr.Zero, stderr = IntPtr.Zero;
            IntPtr size = IntPtr.Zero, list = IntPtr.Zero, jobPtr = IntPtr.Zero, handleArray = IntPtr.Zero, env = IntPtr.Zero, token = IntPtr.Zero;
            bool listReady = false;
            try {
                stdin = OpenInheritable("NUL", false); inherited.Add(stdin);
                string outPath = string.IsNullOrEmpty(spec.StandardOutputPath) ? "NUL" : spec.StandardOutputPath;
                string errPath = string.IsNullOrEmpty(spec.StandardErrorPath) ? "NUL" : spec.StandardErrorPath;
                stdout = OpenInheritable(outPath, true); inherited.Add(stdout);
                if (string.Equals(outPath, errPath, StringComparison.OrdinalIgnoreCase)) { stderr = stdout; }
                else { stderr = OpenInheritable(errPath, true); inherited.Add(stderr); }

                int attributes = job != IntPtr.Zero ? 2 : 1;
                InitializeProcThreadAttributeList(IntPtr.Zero, attributes, 0, ref size);
                list = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(list, attributes, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                listReady = true;
                handleArray = Marshal.AllocHGlobal(IntPtr.Size * inherited.Count);
                for (int i = 0; i < inherited.Count; i++) Marshal.WriteIntPtr(handleArray, i * IntPtr.Size, inherited[i]);
                if (!UpdateProcThreadAttribute(list, 0, AttributeHandleList, handleArray, (IntPtr)(IntPtr.Size * inherited.Count), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (job != IntPtr.Zero) {
                    jobPtr = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(jobPtr, job);
                    if (!UpdateProcThreadAttribute(list, 0, AttributeJobList, jobPtr, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                StartupInfoEx si = new StartupInfoEx();
                si.cb = Marshal.SizeOf(typeof(StartupInfoEx));
                si.dwFlags = StartfUseStdHandles;
                si.hStdInput = stdin; si.hStdOutput = stdout; si.hStdError = stderr;
                si.lpAttributeList = list;
                uint flags = ExtendedStartupInfoPresent | CreateSuspended | CreateNoWindow | CreateNewProcessGroup;
                if (spec.Breakaway) flags |= CreateBreakawayFromJob;
                if (spec.Environment != null) { env = BuildEnvironment(spec.Environment); flags |= CreateUnicodeEnvironment; }
                string dir = string.IsNullOrEmpty(spec.WorkingDirectory) ? null : spec.WorkingDirectory;
                ProcessInformation pi;
                bool created;
                if (spec.RestrictAdministrators) {
                    token = RestrictedAdministratorsToken();
                    created = CreateProcessAsUserW(token, null, new StringBuilder(spec.CommandLine), IntPtr.Zero, IntPtr.Zero, true, flags, env, dir, ref si, out pi);
                } else {
                    created = CreateProcessW(null, new StringBuilder(spec.CommandLine), IntPtr.Zero, IntPtr.Zero, true, flags, env, dir, ref si, out pi);
                }
                if (!created) throw new Win32Exception(Marshal.GetLastWin32Error());
                Staged s = new Staged { Process = pi.hProcess, Thread = pi.hThread, ProcessId = pi.dwProcessId };
                long c, e, k, u;
                if (GetProcessTimes(pi.hProcess, out c, out e, out k, out u)) s.StartedAtUtc = DateTime.FromFileTimeUtc(c).ToString("o");
                StringBuilder image = new StringBuilder(32768); int len = image.Capacity;
                if (QueryFullProcessImageNameW(pi.hProcess, 0, image, ref len)) s.ImagePath = image.ToString();
                return s;
            } finally {
                if (listReady) DeleteProcThreadAttributeList(list);
                if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
                if (jobPtr != IntPtr.Zero) Marshal.FreeHGlobal(jobPtr);
                if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
                if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
                if (token != IntPtr.Zero) CloseHandle(token);
                foreach (IntPtr h in inherited) CloseHandle(h);
            }
        }
        public static void Resume(Staged s) {
            if (s.Thread == IntPtr.Zero) throw new InvalidOperationException("thread handle already closed");
            if (ResumeThread(s.Thread) == 0xFFFFFFFFu) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        // Checked: "Stopped" only on an affirmative wait result or a readable exit code.
        public static string TerminateChecked(Staged s, uint waitMs) {
            if (s.Process == IntPtr.Zero) return "NoHandle";
            if (!TerminateProcess(s.Process, 1)) {
                int error = Marshal.GetLastWin32Error();
                uint code;
                if (GetExitCodeProcess(s.Process, out code) && code != 259u) return "Stopped";
                return "TerminateFailed:" + error;
            }
            uint w = WaitForSingleObject(s.Process, waitMs);
            if (w == 0) return "Stopped";
            if (w == 0x102) return "WaitTimedOut";
            return "WaitFailed:" + Marshal.GetLastWin32Error();
        }
        public static int ExitCode(Staged s) {
            uint code;
            if (!GetExitCodeProcess(s.Process, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)code);
        }
        public static bool HasExited(Staged s) { return WaitForSingleObject(s.Process, 0) == 0; }
        // Idempotent: a second close is a no-op, never a close of a recycled handle value.
        public static void Close(Staged s) {
            if (s == null) return;
            if (s.Thread != IntPtr.Zero) { CloseHandle(s.Thread); s.Thread = IntPtr.Zero; }
            if (s.Process != IntPtr.Zero) { CloseHandle(s.Process); s.Process = IntPtr.Zero; }
        }

        // Affirmative exit evidence, or Unknown. Gone | RunningMatch | RunningDifferent | Unknown:<why>
        public static string Classify(int pid, string expectedStartedAtIso, string expectedImage) {
            if (pid <= 0) return "Unknown:no-pid";
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) {
                int error = Marshal.GetLastWin32Error();
                if (error == 87) return "Gone";
                if (error == 5) return "Unknown:access-denied";
                return "Unknown:open-failed-" + error;
            }
            try {
                uint code;
                if (!GetExitCodeProcess(h, out code)) return "Unknown:exit-code-unreadable";
                if (code != 259u) return "Gone";
                long c, e, k, u;
                if (!GetProcessTimes(h, out c, out e, out k, out u)) return "Unknown:times-unreadable";
                StringBuilder image = new StringBuilder(32768); int len = image.Capacity;
                if (!QueryFullProcessImageNameW(h, 0, image, ref len)) return "Unknown:image-unreadable";
                // Identity is EXACT and REQUIRED: an empty expectation never vouches for whatever holds the pid.
                if (string.IsNullOrEmpty(expectedStartedAtIso) || string.IsNullOrEmpty(expectedImage)) return "Unknown:no-recorded-identity";
                bool same = DateTime.FromFileTimeUtc(c).ToString("o") == expectedStartedAtIso &&
                            string.Equals(image.ToString(), expectedImage, StringComparison.OrdinalIgnoreCase);
                return same ? "RunningMatch" : "RunningDifferent";
            } finally { CloseHandle(h); }
        }
        public static string StartedAtUtc(int pid) {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { long c, e, k, u; if (!GetProcessTimes(h, out c, out e, out k, out u)) throw new Win32Exception(Marshal.GetLastWin32Error()); return DateTime.FromFileTimeUtc(c).ToString("o"); }
            finally { CloseHandle(h); }
        }
        public static string ImagePath(int pid) {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { StringBuilder image = new StringBuilder(32768); int len = image.Capacity; if (!QueryFullProcessImageNameW(h, 0, image, ref len)) throw new Win32Exception(Marshal.GetLastWin32Error()); return image.ToString(); }
            finally { CloseHandle(h); }
        }

        // ---------------- Token facts (B3) ----------------

        static int ReadInt(IntPtr token, int cls) {
            IntPtr buffer = Marshal.AllocHGlobal(4);
            try { int r; if (!GetTokenInformation(token, cls, buffer, 4, out r)) throw new Win32Exception(Marshal.GetLastWin32Error()); return Marshal.ReadInt32(buffer); }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        static IntPtr ReadVariable(IntPtr token, int cls) {
            int needed;
            GetTokenInformation(token, cls, IntPtr.Zero, 0, out needed);
            if (needed <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr buffer = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(token, cls, buffer, needed, out needed)) { int e = Marshal.GetLastWin32Error(); Marshal.FreeHGlobal(buffer); throw new Win32Exception(e); }
            return buffer;
        }
        [DllImport("advapi32.dll", SetLastError=true)] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
        [DllImport("advapi32.dll", SetLastError=true)] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        // The actual token of THIS process, read from the kernel - not a role test.
        public static TokenFacts CurrentTokenFacts() {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                TokenFacts f = new TokenFacts();
                f.Elevated = ReadInt(token, 20) != 0;                       // TokenElevation
                int type = ReadInt(token, 18);                              // TokenElevationType
                f.ElevationType = type == 1 ? "Default" : type == 2 ? "Full" : type == 3 ? "Limited" : "Unknown";
                IntPtr label = ReadVariable(token, 25);                     // TokenIntegrityLevel
                try {
                    IntPtr sid = Marshal.ReadIntPtr(label);
                    int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    f.IntegrityRid = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                    f.IntegrityLevel = f.IntegrityRid == 0 ? "Untrusted" : f.IntegrityRid == 0x1000 ? "Low" : f.IntegrityRid == 0x2000 ? "Medium" :
                        f.IntegrityRid == 0x3000 ? "High" : f.IntegrityRid == 0x4000 ? "System" : "rid:" + f.IntegrityRid;
                } finally { Marshal.FreeHGlobal(label); }
                IntPtr groups = ReadVariable(token, 2);                     // TokenGroups
                List<string> logon = new List<string>();
                f.AdministratorsGroup = "Absent";
                try {
                    int n = Marshal.ReadInt32(groups);
                    int offset = IntPtr.Size;                               // GroupCount is padded to pointer alignment
                    int entry = Marshal.SizeOf(typeof(SidAndAttributes));
                    for (int i = 0; i < n; i++) {
                        IntPtr sid = Marshal.ReadIntPtr(groups, offset + i * entry);
                        uint attributes = (uint)Marshal.ReadInt32(groups, offset + i * entry + IntPtr.Size);
                        string value = new SecurityIdentifier(sid).Value;
                        if (value == "S-1-5-32-544") {
                            if ((attributes & 0x10) != 0) f.AdministratorsGroup = "DenyOnly";
                            else if ((attributes & 0x4) != 0) f.AdministratorsGroup = "Enabled";
                            else f.AdministratorsGroup = "Present:0x" + attributes.ToString("X");
                        }
                        if ((attributes & 0xC0000000u) == 0xC0000000u) logon.Add(value);   // SE_GROUP_LOGON_ID
                        if (value == "S-1-5-4" || value == "S-1-5-3" || value == "S-1-5-6" || value == "S-1-5-2") logon.Add(value);
                    }
                } finally { Marshal.FreeHGlobal(groups); }
                f.LogonSids = logon.ToArray();
                try { IntPtr linked = ReadVariable(token, 19); try { f.HasLinkedToken = Marshal.ReadIntPtr(linked) != IntPtr.Zero; if (f.HasLinkedToken) CloseHandle(Marshal.ReadIntPtr(linked)); } finally { Marshal.FreeHGlobal(linked); } }
                catch (Win32Exception) { f.HasLinkedToken = false; }
                int session;
                f.SessionId = ProcessIdToSessionId(GetCurrentProcessId(), out session) ? session : -1;
                return f;
            } finally { CloseHandle(token); }
        }
    }
}
'@

if (-not ('AeroLink.TransitionV1.Kernel' -as [type])) {
    Add-Type -TypeDefinition $script:TransitionKernelSource
}

function Get-AeroLinkTransitionKernelSourceHash {
    <#
      .SYNOPSIS The hash of the native placement code, which is part of a launch-context descriptor.
      .DESCRIPTION
        A context qualification says "a service created THIS way survived THIS context's cleanup paths". The way a
        service is created is this source text (creation flags, job and breakaway handling, handle inheritance), so a
        change to it must invalidate every qualification made with the previous one. Script edits elsewhere do not
        change placement and therefore do not.
    #>
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($script:TransitionKernelSource.Replace("`r`n", "`n")))) -replace '-', '') }
    finally { $sha.Dispose() }
}

function Get-AeroLinkProcessIdentityRecord {
    <# Identity for a pid, from the kernel, or $null when it cannot be read (never a partial identity). #>
    param([Parameter(Mandatory)][int]$ProcessId)
    try {
        return [pscustomobject]@{ ProcessId = $ProcessId
            StartedAtUtc = [AeroLink.TransitionV1.Kernel]::StartedAtUtc($ProcessId)
            ImagePath = [AeroLink.TransitionV1.Kernel]::ImagePath($ProcessId) }
    }
    catch { return $null }
}

# ------------------------------------------------------------------------------------------------------------------
# Durable records. Every read answers with a CLASS, never with "nothing" when it means "could not tell".
# ------------------------------------------------------------------------------------------------------------------

function ConvertTo-AeroLinkUtcIso {
    <#
      ConvertFrom-Json yields a string on 5.1 and a DateTime on 7; a [string] cast of the latter drops fractional
      seconds and the UTC marker. Lossless round-trip only.
    #>
    param([AllowNull()]$Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [datetime]) { return ([datetime]$Value).ToUniversalTime().ToString('o') }
    if ($Value -is [DateTimeOffset]) { return ([DateTimeOffset]$Value).UtcDateTime.ToString('o') }
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return '' }
    $parsed = [datetime]::MinValue
    if ([datetime]::TryParse($text, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsed)) {
        return $parsed.ToUniversalTime().ToString('o')
    }
    return $text
}

function ConvertTo-AeroLinkUtcDate {
    param([AllowNull()]$Value)
    $iso = ConvertTo-AeroLinkUtcIso $Value
    if (-not $iso) { return $null }
    $parsed = [datetime]::MinValue
    if (-not [datetime]::TryParse($iso, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsed)) { return $null }
    return $parsed.ToUniversalTime()
}

function Test-AeroLinkIntegral { param([AllowNull()]$Value) return ($Value -is [int] -or $Value -is [long] -or $Value -is [int16] -or $Value -is [byte]) }

function Get-AeroLinkProperty {
    <# StrictMode-safe optional property read. #>
    param([AllowNull()]$Object, [Parameter(Mandatory)][string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    if ($Object -is [Collections.IDictionary]) { if ($Object.Contains($Name)) { return $Object[$Name] }; return $Default }
    if ($null -ne $Object.PSObject.Properties[$Name]) { return $Object.$Name }
    return $Default
}

function ConvertTo-AeroLinkKernelIoPath {
    <#
      The form of a path that System.IO can actually open, whatever its length.

      Windows PowerShell runs on .NET Framework, which refuses ANY path longer than MAX_PATH (260 characters)
      unless it is given the extended-length form. A deep installation root - or a probe state root such as the
      per-definition experiment roots the launch-context qualification owns - otherwise fails EVERY evidence write
      with a misleading 'Could not find a part of the path' on the temporary name, which is exactly what turned a
      qualification into Unqualifiable during the #1055 revised Checkpoint 1 run. GetFullPath has already
      normalised this path, so the prefix only disables the legacy length check; it never changes the target.
    #>
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::DirectorySeparatorChar -ne '\' -or $full.StartsWith('\\?\')) { return $full }
    if ($full.StartsWith('\\')) { return '\\?\UNC\' + $full.Substring(2) }
    return '\\?\' + $full
}

function Publish-AeroLinkJsonAtomic {
    <# Temp + rename: a reader sees the whole record or no record. #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    $ioPath = ConvertTo-AeroLinkKernelIoPath $Path
    $ioDirectory = [IO.Path]::GetDirectoryName($ioPath)
    if ($ioDirectory -and -not [IO.Directory]::Exists($ioDirectory)) { [void][IO.Directory]::CreateDirectory($ioDirectory) }
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $ioTemporary = ConvertTo-AeroLinkKernelIoPath $temporary
    [IO.File]::WriteAllText($ioTemporary, ($Value | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    # Same replacement semantics as Move-Item -Force (which also removes the destination first), so a reader never
    # sees a half-written record and the previous record is never left behind.
    if ([IO.File]::Exists($ioPath)) { [IO.File]::Delete($ioPath) }
    [IO.File]::Move($ioTemporary, $ioPath)
}

function New-AeroLinkExclusiveRecord {
    <#
      Create-with-content atomically, or report who got there first. CreateNew followed by a write can leave an
      EMPTY claim if the claimant dies in between; here the content is complete before File.Move publishes it.
    #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $ioTemporary = ConvertTo-AeroLinkKernelIoPath $temporary
    [IO.File]::WriteAllText($ioTemporary, ($Value | ConvertTo-Json -Depth 12 -Compress), (New-Object Text.UTF8Encoding($false)))
    try {
        [IO.File]::Move($ioTemporary, (ConvertTo-AeroLinkKernelIoPath $Path))
        return [pscustomobject]@{ Won = $true; Existing = $null; Class = 'Created' }
    }
    catch {
        try { [IO.File]::Delete($ioTemporary) } catch { }
        $read = Read-AeroLinkJsonRecord -Path $Path
        return [pscustomobject]@{ Won = $false; Existing = $read.Value; Class = $read.Class }
    }
}

function Read-AeroLinkJsonRecord {
    <# Class: Absent | Valid | Malformed | Unreadable #>
    param([Parameter(Mandatory)][string]$Path)
    $ioPath = ConvertTo-AeroLinkKernelIoPath $Path
    if (-not [IO.File]::Exists($ioPath)) { return [pscustomobject]@{ Class = 'Absent'; Value = $null; Detail = 'absent' } }
    $text = $null
    try {
        $fs = [IO.File]::Open($ioPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try { $text = (New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
    }
    catch { return [pscustomobject]@{ Class = 'Unreadable'; Value = $null; Detail = $_.Exception.Message } }
    try {
        $value = $text | ConvertFrom-Json
        if ($null -eq $value) { return [pscustomobject]@{ Class = 'Malformed'; Value = $null; Detail = 'empty' } }
        return [pscustomobject]@{ Class = 'Valid'; Value = $value; Detail = 'valid' }
    }
    catch { return [pscustomobject]@{ Class = 'Malformed'; Value = $null; Detail = 'not valid JSON' } }
}

function Write-AeroLinkTransitionEvent {
    <#
      Append one JSON line, flushed to disk before returning, for journals with several writing processes.

      The append handle is opened FileShare.Read, so while one writer holds it every other writer's open fails with
      a sharing violation: appends are mutually exclusive and FILE ORDER IS COMPLETION ORDER. A sharing violation is
      retried with bounded backoff; beyond the bound, or on any other failure, it THROWS
      'EvidenceWriteContention:' / 'EvidenceWriteFailed:'. Nothing is dropped silently: the caller fails its operation.
    #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Record, [int]$ContentionTimeoutMs = 10000)
    $ioPath = ConvertTo-AeroLinkKernelIoPath $Path
    $ioDirectory = [IO.Path]::GetDirectoryName($ioPath)
    if ($ioDirectory -and -not [IO.Directory]::Exists($ioDirectory)) { [void][IO.Directory]::CreateDirectory($ioDirectory) }
    $bytes = [Text.Encoding]::UTF8.GetBytes((($Record | ConvertTo-Json -Depth 12 -Compress) + "`n"))
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $attempts = 0; $delay = 5
    $stream = $null
    while ($null -eq $stream) {
        $attempts++
        try { $stream = [IO.File]::Open($ioPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read) }
        catch {
            $exception = $_.Exception
            while ($exception -and -not ($exception -is [IO.IOException]) -and $exception.InnerException) { $exception = $exception.InnerException }
            $code = if ($exception -is [IO.IOException]) { $exception.HResult -band 0xFFFF } else { -1 }
            if ($code -ne 32 -and $code -ne 33) { throw "EvidenceWriteFailed: '$Path' could not be opened for append: $($exception.Message)" }
            if ($clock.ElapsedMilliseconds -ge $ContentionTimeoutMs) {
                throw "EvidenceWriteContention: '$Path' stayed locked by another writer for $($clock.ElapsedMilliseconds) ms ($attempts attempts, bound $ContentionTimeoutMs ms); the event was NOT written"
            }
            Start-Sleep -Milliseconds $delay
            $delay = [Math]::Min($delay * 2, 100)
        }
    }
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    catch { throw "EvidenceWriteFailed: '$Path' append did not complete: $($_.Exception.Message)" }
    finally { $stream.Dispose() }
}

function Read-AeroLinkTransitionEvents {
    <#
      Class: Absent | Valid | Corrupt | Unreadable. A final line with no terminator is a TORN APPEND (the writer died
      mid-write) and is dropped. A malformed line anywhere else is corruption: the journal can no longer say what
      happened, so callers must treat its subject as Unknown.
    #>
    param([Parameter(Mandatory)][string]$Path)
    $ioPath = ConvertTo-AeroLinkKernelIoPath $Path
    if (-not [IO.File]::Exists($ioPath)) { return [pscustomobject]@{ Class = 'Absent'; Events = @(); TornTail = $false; Detail = 'absent' } }
    $text = $null
    try {
        $fs = [IO.File]::Open($ioPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $text = (New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
    }
    catch { return [pscustomobject]@{ Class = 'Unreadable'; Events = @(); TornTail = $false; Detail = $_.Exception.Message } }
    $parts = $text -split "`n"
    $events = [System.Collections.Generic.List[object]]::new()
    $torn = $false
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $line = $parts[$i].TrimEnd("`r")
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $events.Add(($line | ConvertFrom-Json)) }
        catch {
            if ($i -eq $parts.Count - 1) { $torn = $true; continue }
            return [pscustomobject]@{ Class = 'Corrupt'; Events = @(); TornTail = $false; Detail = "line $($i + 1) is not valid JSON" }
        }
    }
    return [pscustomobject]@{ Class = 'Valid'; Events = @($events); TornTail = $torn; Detail = $(if ($torn) { 'valid with a torn final append' } else { 'valid' }) }
}

function Enter-AeroLinkTransitionLock {
    <#
      An OS-held lock: a file opened FileShare.None for the life of the holder. The kernel closes it when the holder
      dies, so "acquirable" means "no live holder". State: Held (with Stream) | Busy | Unknown.
    #>
    param([Parameter(Mandatory)][string]$Path)
    try {
        $ioPath = ConvertTo-AeroLinkKernelIoPath $Path
        $ioDirectory = [IO.Path]::GetDirectoryName($ioPath)
        if ($ioDirectory -and -not [IO.Directory]::Exists($ioDirectory)) { [void][IO.Directory]::CreateDirectory($ioDirectory) }
        $stream = [IO.File]::Open($ioPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        return [pscustomobject]@{ State = 'Held'; Stream = $stream; Detail = 'held' }
    }
    catch [System.IO.IOException] {
        $code = $_.Exception.HResult -band 0xFFFF
        if ($code -eq 32 -or $code -eq 33) { return [pscustomobject]@{ State = 'Busy'; Stream = $null; Detail = 'held by another process' } }
        return [pscustomobject]@{ State = 'Unknown'; Stream = $null; Detail = $_.Exception.Message }
    }
    catch { return [pscustomobject]@{ State = 'Unknown'; Stream = $null; Detail = $_.Exception.Message } }
}

function Test-AeroLinkLockHolderAlive {
    <# Alive | Dead | Unknown - probes and immediately releases. #>
    param([Parameter(Mandatory)][string]$Path)
    $lock = Enter-AeroLinkTransitionLock -Path $Path
    if ($lock.State -eq 'Held') { $lock.Stream.Dispose(); return 'Dead' }
    if ($lock.State -eq 'Busy') { return 'Alive' }
    return 'Unknown'
}

function Get-AeroLinkBootTimeUtc {
    <# The one fact that proves every process from before it is gone. Null when unreadable (never guessed). #>
    try { return ([datetime](Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime).ToUniversalTime() } catch { return $null }
}

function Get-AeroLinkSha256Text([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))) -replace '-', '') } finally { $sha.Dispose() }
}

function Get-AeroLinkSha256File([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $fs = [IO.File]::Open((ConvertTo-AeroLinkKernelIoPath $Path), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '') } finally { $fs.Dispose(); $sha.Dispose() }
}

function Get-AeroLinkTokenFacts {
    <#
      .SYNOPSIS The ACTUAL token of this process (B3): elevation, elevation type, integrity level, the Administrators
        group's attributes, a linked token, session and logon SIDs.
      .DESCRIPTION
        WindowsPrincipal.IsInRole(Administrator) is a group-membership test; it does not say whether the token is
        elevated or filtered. These are read from the kernel. The role test is reported beside them because the
        product used it historically, never instead of them.
    #>
    try {
        $facts = [AeroLink.TransitionV1.Kernel]::CurrentTokenFacts()
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        return [pscustomobject]@{
            Readable = $true
            TokenElevation = [bool]$facts.Elevated
            TokenElevationType = [string]$facts.ElevationType
            IntegrityLevel = [string]$facts.IntegrityLevel
            AdministratorsGroup = [string]$facts.AdministratorsGroup
            HasLinkedToken = [bool]$facts.HasLinkedToken
            SessionId = [int]$facts.SessionId
            LogonSids = @($facts.LogonSids)
            IsInRoleAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        }
    }
    catch { return [pscustomobject]@{ Readable = $false; Detail = $_.Exception.Message } }
}

Export-ModuleMember -Function Get-AeroLinkTransitionKernelSourceHash, Get-AeroLinkProcessIdentityRecord, ConvertTo-AeroLinkUtcIso, `
    ConvertTo-AeroLinkUtcDate, ConvertTo-AeroLinkKernelIoPath, Test-AeroLinkIntegral, Get-AeroLinkProperty, Publish-AeroLinkJsonAtomic, New-AeroLinkExclusiveRecord, `
    Read-AeroLinkJsonRecord, Write-AeroLinkTransitionEvent, Read-AeroLinkTransitionEvents, Enter-AeroLinkTransitionLock, `
    Test-AeroLinkLockHolderAlive, Get-AeroLinkBootTimeUtc, Get-AeroLinkSha256Text, Get-AeroLinkSha256File, Get-AeroLinkTokenFacts
