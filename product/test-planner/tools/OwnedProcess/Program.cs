using Microsoft.Win32.SafeHandles;
using System.Collections;
using System.Diagnostics;
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
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xffffffff;
    // Fault injection is reachable only through the explicit self-test command-line mode.
    private static string? SelfTestFault;
    private static bool LastCleanupFailure;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test-space-path", StringComparer.Ordinal)) return SpacePathSelfTest() ? 0 : 1;
            if (args.Contains("--self-test-late-child", StringComparer.Ordinal)) return LateChildSelfTest() ? 0 : 1;
            if (args.Contains("--self-test-exit-codes", StringComparer.Ordinal)) return ExitCodeSelfTest() ? 0 : 1;
            var faultIndex = Array.FindIndex(args, arg => string.Equals(arg, "--self-test-fault", StringComparison.Ordinal));
            if (faultIndex >= 0 && faultIndex + 1 < args.Length)
            {
                SelfTestFault = args[faultIndex + 1];
                return FaultSelfTest(SelfTestFault) ? 0 : 1;
            }
            var options = Parse(args);
            var result = RunOwned(options);
            return LastCleanupFailure ? 1 : result;
        }
        catch
        {
            return 1;
        }
    }

    private static bool Fault(string name) =>
        SelfTestFault is not null && string.Equals(SelfTestFault, name, StringComparison.Ordinal);

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

        IntPtr job = IntPtr.Zero;
        IntPtr process = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        IntPtr childRead = IntPtr.Zero;
        IntPtr childWrite = IntPtr.Zero;
        SafeFileHandle? captureHandle = null;
        Task<bool>? capture = null;
        CancellationTokenSource? captureCancellation = null;
        var cleanupSuccess = true;
        var operationFailed = false;
        LastCleanupFailure = false;

        try
        {
            if (!CreateJob(out job))
            {
                Append(statusFile, "ERROR|code=create-job");
                operationFailed = true;
                return 1;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(stdoutFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(stderrFile)!);
            var pipeAttributes = new SecurityAttributes
            {
                nLength = Marshal.SizeOf<SecurityAttributes>(),
                bInheritHandle = true,
            };
            if (!CreatePipeNative(out childRead, out childWrite, ref pipeAttributes, 0))
            {
                Append(statusFile, "ERROR|code=create-pipe");
                operationFailed = true;
                return 1;
            }
            if (!SetHandleInformationNative(childRead, HandleFlagInherit, 0))
            {
                Append(statusFile, "ERROR|code=set-handle");
                operationFailed = true;
                return 1;
            }

            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                dwFlags = 0x00000100,
                hStdInput = IntPtr.Zero,
                hStdOutput = childWrite,
                hStdError = childWrite,
            };
            var commandLine = new StringBuilder(Quote(executable) + " " + string.Join(" ", commandArgs.Select(Quote)));
            var environment = EnvironmentBlock(ReadEnvironmentFile(envFile));
            var environmentHandle = GCHandle.Alloc(environment, GCHandleType.Pinned);
            try
            {
                if (!CreateProcessNative(null, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                        CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                        environmentHandle.AddrOfPinnedObject(), null, ref startup, out var info))
                {
                    Append(statusFile, "ERROR|code=create");
                    operationFailed = true;
                    return 1;
                }
                process = info.hProcess;
                thread = info.hThread;
            }
            finally { environmentHandle.Free(); }

            if (!CloseOwned(ref childWrite, "close-child-write", ref cleanupSuccess))
            {
                Append(statusFile, "ERROR|code=close-child-write");
                operationFailed = true;
                if (!TerminateProcessAndWait(process, ref cleanupSuccess, out _)) Append(statusFile, "ERROR|code=terminate-process");
                return 1;
            }
            if (!AssignProcessToJobObjectNative(job, process))
            {
                Append(statusFile, "ERROR|code=assign");
                operationFailed = true;
                if (!TerminateProcessAndWait(process, ref cleanupSuccess, out _)) Append(statusFile, "ERROR|code=terminate-process");
                return 1;
            }
            if (ResumeThreadNative(thread) == unchecked((uint)-1))
            {
                Append(statusFile, "ERROR|code=resume");
                operationFailed = true;
                if (!TerminateProcessAndWait(process, ref cleanupSuccess, out _)) Append(statusFile, "ERROR|code=terminate-process");
                return 1;
            }

            var pid = GetProcessIdNative(process);
            if (pid == 0)
            {
                Append(statusFile, "ERROR|code=pid");
                operationFailed = true;
                if (!TerminateProcessAndWait(process, ref cleanupSuccess, out _)) Append(statusFile, "ERROR|code=terminate-process");
                return 1;
            }
            if (!GetProcessTimesNative(process, out var start))
            {
                Append(statusFile, "ERROR|code=start-time");
                operationFailed = true;
                if (!TerminateProcessAndWait(process, ref cleanupSuccess, out _)) Append(statusFile, "ERROR|code=terminate-process");
                return 1;
            }
            Append(statusFile, $"STARTED|pid={pid}|start={start}|job=assigned");

            // Keep ownership in the explicit IntPtr so the final CloseHandle result is observable. The
            // SafeFileHandle is only a non-owning adapter for async FileStream reads.
            captureHandle = new SafeFileHandle(childRead, false);
            captureCancellation = new CancellationTokenSource();
            capture = CaptureAsync(captureHandle, stdoutFile, stderrFile, captureCancellation.Token);
            var control = Task.Run(() => Console.ReadLine());
            var stopped = false;
            var rootExited = false;
            var exitCode = 0u;

            if (Fault("terminate-process"))
            {
                operationFailed = true;
                if (!TerminateProcessAndWait(process, ref cleanupSuccess, out exitCode)) Append(statusFile, "ERROR|code=terminate-process");
                rootExited = true;
            }

            while (!rootExited)
            {
                var waitResult = WaitForSingleObjectNative(process, 250);
                if (waitResult == WaitFailed)
                {
                    Append(statusFile, "ERROR|code=wait");
                    operationFailed = true;
                    if (!TerminateProcessAndWait(process, ref cleanupSuccess, out exitCode)) Append(statusFile, "ERROR|code=terminate-process");
                    break;
                }
                if (waitResult == WaitObject0)
                {
                    rootExited = true;
                    if (!GetExitCodeProcessNative(process, out exitCode))
                    {
                        Append(statusFile, "ERROR|code=exit-code");
                        operationFailed = true;
                    }
                    break;
                }
                if (waitResult != WaitTimeout)
                {
                    Append(statusFile, "ERROR|code=wait-state");
                    operationFailed = true;
                    if (!TerminateProcessAndWait(process, ref cleanupSuccess, out exitCode)) Append(statusFile, "ERROR|code=terminate-process");
                    break;
                }
                if (control.IsCompleted)
                {
                    var command = control.GetAwaiter().GetResult();
                    if (string.Equals(command, "stop", StringComparison.OrdinalIgnoreCase))
                    {
                        stopped = true;
                        if (!TerminateJobAndDrain(job, process, ref cleanupSuccess, out _))
                        {
                            Append(statusFile, "ERROR|code=terminate-job");
                            operationFailed = true;
                        }
                        if (!GetExitCodeProcessNative(process, out exitCode))
                        {
                            Append(statusFile, "ERROR|code=exit-code");
                            operationFailed = true;
                        }
                        rootExited = true;
                    }
                    else { control = Task.Run(() => Console.ReadLine()); }
                }
            }

            var jobCount = -1;
            var jobEmpty = rootExited && DrainJobAfterRootExit(job, process, ref cleanupSuccess, out jobCount);
            if (!jobEmpty)
            {
                Append(statusFile, "ERROR|code=job-drain");
                operationFailed = true;
            }

            // The job must be empty before waiting for inherited-pipe EOF. A descendant can retain the
            // write end after the root exits; awaiting CaptureAsync first is an unbounded hang.
            var captureCompleted = !Fault("capture-timeout") && !Fault("cancel-capture") && capture is not null && capture.Wait(TimeSpan.FromSeconds(5));
            if (!captureCompleted)
            {
                Append(statusFile, "ERROR|code=capture-timeout");
                operationFailed = true;
                if (captureHandle is not null)
                {
                    if (!CancelIoExNative(captureHandle.DangerousGetHandle(), IntPtr.Zero)) cleanupSuccess = false;
                    captureCancellation?.Cancel();
                    captureHandle.Dispose();
                }
                if (capture is not null && !capture.Wait(TimeSpan.FromSeconds(1))) cleanupSuccess = false;
            }
            else if (capture is not null)
            {
                try { if (!capture.GetAwaiter().GetResult()) operationFailed = true; }
                catch { operationFailed = true; }
            }

            if (!stopped && exitCode != 0)
            {
                Append(statusFile, "ERROR|code=target-exit");
                operationFailed = true;
            }
            Append(statusFile, $"{(stopped ? "STOPPED" : "EXITED")}|pid={pid}|exit={exitCode}|jobCount={jobCount}");
            return operationFailed || !jobEmpty || !captureCompleted ? 1 : 0;
        }
        finally
        {
            captureCancellation?.Dispose();
            if (captureHandle is not null)
            {
                captureHandle.Dispose();
                if (!captureHandle.IsClosed) cleanupSuccess = false;
            }
            if (!CloseOwned(ref childWrite, "close-child-write-final", ref cleanupSuccess)) cleanupSuccess = false;
            if (!CloseOwned(ref childRead, "close-child-read-final", ref cleanupSuccess)) cleanupSuccess = false;
            if (!CloseOwned(ref thread, "close-thread", ref cleanupSuccess)) cleanupSuccess = false;
            if (!CloseOwned(ref process, "close-process", ref cleanupSuccess)) cleanupSuccess = false;
            if (!CloseOwned(ref job, "close-job", ref cleanupSuccess)) cleanupSuccess = false;
            LastCleanupFailure = !cleanupSuccess;
            try { Append(statusFile, cleanupSuccess ? "CLEANUP|handles=closed" : "CLEANUP|handles=failed"); } catch { cleanupSuccess = false; LastCleanupFailure = true; }
        }
    }

    private static bool TerminateProcessAndWait(IntPtr process, ref bool cleanupSuccess, out uint exitCode)
    {
        exitCode = 1;
        var terminateOk = TerminateProcessNative(process, 1);
        var waitResult = WaitForSingleObjectNative(process, 5000);
        var waitOk = waitResult == WaitObject0;
        var exitOk = GetExitCodeProcessNative(process, out exitCode);
        if (!terminateOk || !waitOk || !exitOk) cleanupSuccess = false;
        return terminateOk && waitOk && exitOk;
    }

    private static bool TerminateJobAndDrain(IntPtr job, IntPtr process, ref bool cleanupSuccess, out int jobCount)
    {
        jobCount = -1;
        if (!TerminateJobObjectNative(job, 1)) { cleanupSuccess = false; return false; }
        return WaitForJobEmpty(job, process, ref cleanupSuccess, out jobCount);
    }

    private static bool DrainJobAfterRootExit(IntPtr job, IntPtr process, ref bool cleanupSuccess, out int jobCount)
    {
        jobCount = -1;
        if (!QueryJobProcessCount(job, out jobCount)) { cleanupSuccess = false; return false; }
        if (jobCount == 0) return true;
        return TerminateJobAndDrain(job, process, ref cleanupSuccess, out jobCount);
    }

    private static bool WaitForJobEmpty(IntPtr job, IntPtr process, ref bool cleanupSuccess, out int jobCount)
    {
        jobCount = -1;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (!QueryJobProcessCount(job, out jobCount)) { cleanupSuccess = false; return false; }
            if (jobCount == 0) return true;
            var waitResult = WaitForSingleObjectNative(process, 100);
            if (waitResult == WaitFailed || (waitResult != WaitObject0 && waitResult != WaitTimeout)) { cleanupSuccess = false; return false; }
        }
        cleanupSuccess = false;
        return false;
    }

    private static async Task<bool> CaptureAsync(SafeFileHandle readHandle, string stdout, string stderr, CancellationToken cancellationToken)
    {
        try
        {
            using var input = new FileStream(readHandle, FileAccess.Read);
            using var output = new FileStream(stdout, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var error = new FileStream(stderr, FileMode.Create, FileAccess.Write, FileShare.Read);
            await input.CopyToAsync(output, 81920, cancellationToken);
            await output.FlushAsync(cancellationToken);
            await error.FlushAsync(cancellationToken);
            return true;
        }
        catch { return false; }
    }

    private static bool SpacePathSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "aerolink planner space " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var shell = Path.Combine(root, "command shell.exe");
        var output = Path.Combine(root, "stdout.log");
        var error = Path.Combine(root, "stderr.log");
        var status = Path.Combine(root, "status.log");
        var env = Path.Combine(root, "environment file.env");
        try
        {
            File.Copy(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", shell);
            File.WriteAllText(env, "AEROLINK_SPACE_TEST=1\r\n");
            var exit = RunOwned(CreateOptions(shell, status, output, error, env, new[] { "/d", "/c", "echo", "aerolink planner space fixture" }));
            return exit == 0 && File.Exists(output) && File.ReadAllText(output).Contains("aerolink planner space fixture", StringComparison.Ordinal)
                && File.ReadAllText(status).Contains("CLEANUP|handles=closed", StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static bool LateChildSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "aerolink late child " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var childScript = Path.Combine(root, "child.ps1");
        var parentScript = Path.Combine(root, "parent.ps1");
        var pidFile = Path.Combine(root, "child.pid");
        var output = Path.Combine(root, "stdout.log");
        var error = Path.Combine(root, "stderr.log");
        var status = Path.Combine(root, "status.log");
        var env = Path.Combine(root, "environment.env");
        try
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) return false;
            File.WriteAllText(childScript, "$PID | Set-Content -LiteralPath '" + pidFile.Replace("'", "''") + "' ; Start-Sleep -Seconds 30");
            File.WriteAllText(parentScript, "$p = Start-Process -FilePath '" + powershell.Replace("'", "''") + "' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File','" + childScript.Replace("'", "''") + "') -PassThru; $p.Id | Set-Content -LiteralPath '" + pidFile.Replace("'", "''") + "'");
            File.WriteAllText(env, "AEROLINK_LATE_CHILD=1\r\n");
            var stopwatch = Stopwatch.StartNew();
            var exit = RunOwned(CreateOptions(powershell, status, output, error, env,
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", parentScript }));
            stopwatch.Stop();
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(8)) return false;
            if (exit != 0 || !File.Exists(pidFile) || !File.ReadAllText(status).Contains("jobCount=0", StringComparison.Ordinal)
                || !File.ReadAllText(status).Contains("CLEANUP|handles=closed", StringComparison.Ordinal)) return false;
            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var childPid)) return false;
            try { using var child = Process.GetProcessById(childPid); return false; } catch (ArgumentException) { return true; }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static bool ExitCodeSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "aerolink exit codes " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var shell = Path.Combine(root, "command shell.exe");
        var env = Path.Combine(root, "environment.env");
        try
        {
            File.Copy(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", shell);
            File.WriteAllText(env, "AEROLINK_EXIT_CODE_TEST=1\r\n");
            foreach (var code in new[] { 0, 7 })
            {
                var status = Path.Combine(root, $"status-{code}.log");
                var output = Path.Combine(root, $"stdout-{code}.log");
                var error = Path.Combine(root, $"stderr-{code}.log");
                var exit = RunOwned(CreateOptions(shell, status, output, error, env, new[] { "/d", "/c", "exit", code.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                var expectedSuccess = code == 0;
                if ((exit == 0) != expectedSuccess || !File.ReadAllText(status).Contains($"exit={code}|jobCount=0", StringComparison.Ordinal)
                    || !File.ReadAllText(status).Contains("CLEANUP|handles=closed", StringComparison.Ordinal)) return false;
            }
            return true;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static bool FaultSelfTest(string? fault)
    {
        var root = Path.Combine(Path.GetTempPath(), "aerolink fault " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var shell = Path.Combine(root, "command shell.exe");
        var output = Path.Combine(root, "stdout.log");
        var error = Path.Combine(root, "stderr.log");
        var status = Path.Combine(root, "status.log");
        var env = Path.Combine(root, "environment.env");
        try
        {
            File.Copy(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", shell);
            File.WriteAllText(env, "AEROLINK_FAULT_TEST=1\r\n");
            // Keep a descendant alive for terminate-job so DrainJobAfterRootExit must invoke the injected
            // TerminateJobObject failure. The old one-shot echo could leave an already-empty job,
            // making this fault-path self-test timing-dependent on the Windows runner.
            var commandArgs = string.Equals(fault, "terminate-job", StringComparison.Ordinal)
                ? new[] { "/d", "/c", "start /b ping -n 31 127.0.0.1 > nul" }
                : new[] { "/d", "/c", "echo", "fault" };
            var exit = RunOwned(CreateOptions(shell, status, output, error, env, commandArgs));
            var statusText = File.Exists(status) ? File.ReadAllText(status) : string.Empty;
            return (exit != 0 || statusText.Contains("CLEANUP|handles=failed", StringComparison.Ordinal))
                && statusText.Contains("CLEANUP|handles=", StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static Dictionary<string, string> CreateOptions(string executable, string status, string stdout, string stderr, string env, IReadOnlyList<string> args) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["--executable"] = executable,
            ["--status-file"] = status,
            ["--stdout-file"] = stdout,
            ["--stderr-file"] = stderr,
            ["--env-file"] = env,
            ["--args"] = string.Join("\u001f", args),
        };

    private static bool CreateJob(out IntPtr job)
    {
        job = CreateJobObjectNative(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return false;
        var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObjectNative(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            if (CloseHandleNative(job, "close-job-create")) job = IntPtr.Zero;
            return false;
        }
        return true;
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

    private static bool CloseOwned(ref IntPtr handle, string faultName, ref bool cleanupSuccess)
    {
        if (handle == IntPtr.Zero) return true;
        if (!CloseHandleNative(handle, faultName))
        {
            cleanupSuccess = false;
            return false;
        }
        handle = IntPtr.Zero;
        return true;
    }

    private static uint WaitForSingleObjectNative(IntPtr handle, uint milliseconds) =>
        Fault("wait") ? WaitFailed : WaitForSingleObject(handle, milliseconds);

    private static bool SetHandleInformationNative(IntPtr handle, uint mask, uint flags) =>
        !Fault("set-handle") && SetHandleInformation(handle, mask, flags);

    private static bool CreatePipeNative(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size)
    {
        read = IntPtr.Zero;
        write = IntPtr.Zero;
        return !Fault("create-pipe") && CreatePipe(out read, out write, ref attributes, size);
    }

    private static bool CreateProcessNative(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation processInfo)
    {
        processInfo = default;
        return !Fault("create-process") && CreateProcessW(application, commandLine, processAttributes, threadAttributes, inheritHandles, flags, environment, directory, ref startup, out processInfo);
    }

    private static bool AssignProcessToJobObjectNative(IntPtr job, IntPtr process) =>
        !Fault("assign") && AssignProcessToJobObject(job, process);

    private static bool SetInformationJobObjectNative(IntPtr job, uint infoClass, ref ExtendedLimitInformation info, int length) =>
        !Fault("set-job") && !Fault("close-job-create") && SetInformationJobObject(job, infoClass, ref info, length);

    private static IntPtr CreateJobObjectNative(IntPtr attributes, string? name) =>
        Fault("create-job") ? IntPtr.Zero : CreateJobObjectW(attributes, name);

    private static bool TerminateJobObjectNative(IntPtr job, uint code) =>
        !Fault("terminate-job") && TerminateJobObject(job, code);

    private static bool QueryInformationJobObjectNative(IntPtr job, uint infoClass, IntPtr info, uint length, out uint returned)
    {
        returned = 0;
        return !Fault("query-job") && QueryInformationJobObject(job, infoClass, info, length, out returned);
    }

    private static bool TerminateProcessNative(IntPtr process, uint code) =>
        !Fault("terminate-process") && TerminateProcess(process, code);

    private static uint ResumeThreadNative(IntPtr thread) =>
        Fault("resume") ? unchecked((uint)-1) : ResumeThread(thread);

    private static uint GetProcessIdNative(IntPtr process) =>
        Fault("process-id") ? 0 : GetProcessId(process);

    private static bool GetProcessTimesNative(IntPtr process, out long creation)
    {
        creation = 0;
        return !Fault("process-times") && GetProcessTimes(process, out creation, out _, out _, out _);
    }

    private static bool GetExitCodeProcessNative(IntPtr process, out uint code)
    {
        code = 1;
        return !Fault("exit-code") && GetExitCodeProcess(process, out code);
    }

    private static bool CloseHandleNative(IntPtr handle, string faultName) =>
        !Fault(faultName) && CloseHandle(handle);

    private static bool CancelIoExNative(IntPtr handle, IntPtr overlapped) =>
        !Fault("cancel-capture") && CancelIoEx(handle, overlapped);

    private static bool QueryJobProcessCount(IntPtr job, out int count)
    {
        count = -1;
        var buffer = Marshal.AllocHGlobal(8 + IntPtr.Size * 512);
        try
        {
            if (!QueryInformationJobObjectNative(job, JobObjectBasicProcessIdList, buffer, (uint)(8 + IntPtr.Size * 512), out _)) return false;
            count = Marshal.ReadInt32(buffer, 4);
            return count >= 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void Append(string path, string line) => File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation { public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int nLength; public IntPtr lpSecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags, wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation processInfo);
}
