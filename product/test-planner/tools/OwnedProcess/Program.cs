using Microsoft.Win32.SafeHandles;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

// Windows-only boundary for the disposable planner API. The target is created suspended, assigned to a
// kill-on-close Job Object before it can execute, then resumed. No command line, environment value, or child
// diagnostic is written to the status stream; callers get bounded state only.
internal static class Program
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectBasicProcessIdList = 3;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint HandleFlagInherit = 1;
    private const uint StdInputHandle = unchecked((uint)-10);
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test-space-path", StringComparer.Ordinal)) return SpacePathSelfTest() ? 0 : 1;
            var options = Parse(args);
            return RunOwned(options);
        }
        catch
        {
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var commandArgs = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name.Equals("--arg", StringComparison.Ordinal))
            {
                if (++index >= args.Length) throw new InvalidOperationException();
                commandArgs.Add(args[index]);
                continue;
            }
            if (!name.StartsWith("--", StringComparison.Ordinal) || ++index >= args.Length) throw new InvalidOperationException();
            values[name] = args[index];
        }
        if (!values.TryGetValue("--executable", out var executable)
            || !values.TryGetValue("--status-file", out var status)
            || !values.TryGetValue("--stdout-file", out var stdout)
            || !values.TryGetValue("--stderr-file", out var stderr)
            || !values.TryGetValue("--env-file", out var envFile)
            || commandArgs.Count == 0)
            throw new InvalidOperationException();
        return new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
        {
            ["--executable"] = executable,
            ["--status-file"] = status,
            ["--stdout-file"] = stdout,
            ["--stderr-file"] = stderr,
            ["--env-file"] = envFile,
            ["--args"] = string.Join("\u001f", commandArgs),
        };
    }

    private static int RunOwned(IReadOnlyDictionary<string, string> options)
    {
        var executable = options["--executable"];
        var statusFile = options["--status-file"];
        var stdoutFile = options["--stdout-file"];
        var stderrFile = options["--stderr-file"];
        var envFile = options["--env-file"];
        var commandArgs = options["--args"].Split('\u001f');
        Directory.CreateDirectory(Path.GetDirectoryName(statusFile)!);
        var job = CreateJob();
        IntPtr process = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        IntPtr childRead = IntPtr.Zero;
        IntPtr childWrite = IntPtr.Zero;
        try
        {
            var pipeAttributes = new SecurityAttributes
            {
                nLength = Marshal.SizeOf<SecurityAttributes>(),
                bInheritHandle = true,
            };
            if (!CreatePipe(out childRead, out childWrite, ref pipeAttributes, 0)) throw new InvalidOperationException();
            SetHandleInformation(childRead, HandleFlagInherit, 0);
            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                dwFlags = 0x00000100,
                hStdInput = IntPtr.Zero,
                hStdOutput = childWrite,
                hStdError = childWrite,
            };
            var commandLine = Quote(executable) + " " + string.Join(" ", commandArgs.Select(Quote));
            var environment = EnvironmentBlock(ReadEnvironmentFile(envFile));
            var environmentHandle = GCHandle.Alloc(environment, GCHandleType.Pinned);
            try
            {
                if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                        CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                        environmentHandle.AddrOfPinnedObject(), null, ref startup, out var info))
                {
                    Append(statusFile, "ERROR|code=create");
                    return 1;
                }
                process = info.hProcess;
                thread = info.hThread;
            }
            finally { environmentHandle.Free(); }
            CloseHandle(childWrite);
            childWrite = IntPtr.Zero;
            if (!AssignProcessToJobObject(job, process))
            {
                TerminateProcess(process, 1);
                Append(statusFile, "ERROR|code=assign");
                return 1;
            }
            if (ResumeThread(thread) == unchecked((uint)-1))
            {
                TerminateProcess(process, 1);
                Append(statusFile, "ERROR|code=resume");
                return 1;
            }
            var pid = GetProcessId(process);
            var start = ProcessStartFileTime(process);
            Append(statusFile, $"STARTED|pid={pid}|start={start}|job=assigned");
            var capture = CaptureAsync(childRead, stdoutFile, stderrFile);
            childRead = IntPtr.Zero;
            var control = Task.Run(() => Console.ReadLine());
            var stopped = false;
            var exitCode = 0u;
            while (true)
            {
                if (WaitForSingleObject(process, 250) == WaitObject0)
                {
                    GetExitCodeProcess(process, out exitCode);
                    break;
                }
                if (control.IsCompleted)
                {
                    var command = control.GetAwaiter().GetResult();
                    if (string.Equals(command, "stop", StringComparison.OrdinalIgnoreCase))
                    {
                        stopped = true;
                        TerminateJobObject(job, 1);
                        WaitForSingleObject(process, 5000);
                        GetExitCodeProcess(process, out exitCode);
                        break;
                    }
                    control = Task.Run(() => Console.ReadLine());
                }
            }
            capture.GetAwaiter().GetResult();
            var jobCount = QueryJobProcessCount(job);
            if (jobCount != 0) TerminateJobObject(job, 1);
            jobCount = QueryJobProcessCount(job);
            Append(statusFile, $"{(stopped ? "STOPPED" : "EXITED")}|pid={pid}|exit={exitCode}|jobCount={jobCount}");
            return jobCount == 0 ? 0 : 1;
        }
        finally
        {
            if (childWrite != IntPtr.Zero) CloseHandle(childWrite);
            if (childRead != IntPtr.Zero) CloseHandle(childRead);
            if (thread != IntPtr.Zero) CloseHandle(thread);
            if (process != IntPtr.Zero) CloseHandle(process);
            if (job != IntPtr.Zero) CloseHandle(job); // KILL_ON_JOB_CLOSE is the final descendant boundary.
            Append(statusFile, "CLEANUP|handles=closed");
        }
    }

    private static bool SpacePathSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "aerolink planner space " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var copiedShell = Path.Combine(root, "command shell.exe");
        var output = Path.Combine(root, "stdout.log");
        var error = Path.Combine(root, "stderr.log");
        var status = Path.Combine(root, "status.log");
        var env = Path.Combine(root, "environment file.env");
        try
        {
            File.Copy(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", copiedShell);
            File.WriteAllText(env, "AEROLINK_SPACE_TEST=1\r\n");
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["--executable"] = copiedShell,
                ["--status-file"] = status,
                ["--stdout-file"] = output,
                ["--stderr-file"] = error,
                ["--env-file"] = env,
                ["--args"] = "/d\u001f/c\u001fecho\u001faerolink planner space fixture",
            };
            var exit = RunOwned(options);
            return exit == 0 && File.Exists(output) && File.ReadAllText(output).Contains("aerolink planner space fixture", StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static IntPtr CreateJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new InvalidOperationException();
        var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimitInformation>())) { CloseHandle(job); throw new InvalidOperationException(); }
        return job;
    }

    private static Dictionary<string, string> ReadEnvironmentFile(string path)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture);
            var value = Convert.ToString(entry.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(key) && value is not null) environment[key] = value;
        }
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var split = line.IndexOf('=');
            if (split <= 0 || line[..split].Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) throw new InvalidOperationException();
            environment[line[..split]] = line[(split + 1)..];
        }
        return environment;
    }

    private static byte[] EnvironmentBlock(IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder();
        foreach (var item in values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) builder.Append(item.Key).Append('=').Append(item.Value).Append('\0');
        builder.Append('\0');
        return Encoding.Unicode.GetBytes(builder.ToString());
    }

    private static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(character); slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private static async Task CaptureAsync(IntPtr readHandle, string stdout, string stderr)
    {
        using var input = new FileStream(new SafeFileHandle(readHandle, true), FileAccess.Read);
        using var output = new FileStream(stdout, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var error = new FileStream(stderr, FileMode.Create, FileAccess.Write, FileShare.Read);
        // A single pipe carries both streams; preserving it in stdout keeps the parent from printing arbitrary
        // child diagnostics. stderr is created as an empty bounded artifact for the caller's cleanup contract.
        await input.CopyToAsync(output);
        await output.FlushAsync();
        await error.FlushAsync();
    }

    private static long ProcessStartFileTime(IntPtr process)
    {
        GetProcessTimes(process, out var creation, out _, out _, out _);
        return creation;
    }

    private static int QueryJobProcessCount(IntPtr job)
    {
        var buffer = Marshal.AllocHGlobal(8 + IntPtr.Size * 512);
        try
        {
            if (!QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, (uint)(8 + IntPtr.Size * 512), out _)) return -1;
            return Marshal.ReadInt32(buffer, 4);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void Append(string path, string line) => File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation { public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int nLength; public IntPtr lpSecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags, wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, uint infoClass, ref ExtendedLimitInformation info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, uint infoClass, IntPtr info, uint length, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(uint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string? application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation processInfo);
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
}
