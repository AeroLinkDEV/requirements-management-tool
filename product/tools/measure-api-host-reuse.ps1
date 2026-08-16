[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Run', 'Evaluate')]
    [string]$Mode = 'Plan',
    [string]$BaselinePath,
    [string]$TreatmentPath,
    [string]$BaselineSummaryPath,
    [string]$TreatmentSummaryPath,
    [string]$OutputRoot,
    [int]$Runs = 10,
    [int]$ShardCount = 3,
    [int]$TimeoutMinutes = 30,
    [int]$ProcessTimeoutMinutes = 60,
    [int]$MaxProcessTreeCount = 256,
    [string]$Seeds,
    [string]$ProjectPath = 'product/tests/AeroLink.Api.Tests/AeroLink.Api.Tests.csproj',
    [string]$SolutionPath = 'product/AeroLink.slnx',
    [string]$TestListPath,
    [switch]$Warmup,
    [switch]$SkipBuild,
    [switch]$JobSmoke,
    [string]$DotnetExecutable = 'dotnet',
    [string]$NodeExecutable = 'node'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not ('AeroLinkJobNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class AeroLinkJobNative
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const uint JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9;
    private const long JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint CREATE_ALWAYS = 2;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class SecurityAttributes
    {
        public int nLength = Marshal.SizeOf<SecurityAttributes>();
        public IntPtr lpSecurityDescriptor = IntPtr.Zero;
        public int bInheritHandle = 1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class StartupInfo
    {
        public int cb = Marshal.SizeOf<StartupInfo>();
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public long LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, uint infoClass, ref ExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr hJob, uint infoClass, ref BasicAccountingInformation info, uint length, IntPtr returnLength);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, SecurityAttributes processAttributes, SecurityAttributes threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode, SecurityAttributes securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public sealed class LaunchResult
    {
        public bool Success;
        public string Error;
        public int ProcessId;
        public IntPtr ProcessHandle;
        public IntPtr JobHandle;
        public string StdoutPath;
        public string StderrPath;
    }

    public sealed class CleanupResult
    {
        public bool Success;
        public bool ProcessExited;
        public bool JobEmpty;
        public bool HandlesClosed;
        public int ActiveProcesses;
        public string Error;
    }

    private static string LastError(string operation)
    {
        return operation + " failed with Win32 error " + Marshal.GetLastWin32Error();
    }

    public static LaunchResult Launch(string applicationName, string commandLine, string currentDirectory, string stdoutPath, string stderrPath, string[] environmentEntries)
    {
        var result = new LaunchResult { Success = false, StdoutPath = stdoutPath, StderrPath = stderrPath };
        IntPtr job = IntPtr.Zero;
        IntPtr stdout = IntPtr.Zero;
        IntPtr stderr = IntPtr.Zero;
        ProcessInformation pi = default(ProcessInformation);
        IntPtr environment = IntPtr.Zero;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) { result.Error = LastError("CreateJobObject"); return result; }
            var limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!SetInformationJobObject(job, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>())) { result.Error = LastError("SetInformationJobObject"); return result; }
            var attributes = new SecurityAttributes();
            stdout = CreateFileW(stdoutPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, attributes, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (stdout == IntPtr.Zero || stdout == INVALID_HANDLE_VALUE) { result.Error = LastError("CreateFile stdout"); return result; }
            stderr = CreateFileW(stderrPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, attributes, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (stderr == IntPtr.Zero || stderr == INVALID_HANDLE_VALUE) { result.Error = LastError("CreateFile stderr"); return result; }
            if (environmentEntries != null && environmentEntries.Length > 0)
            {
                var block = string.Join("\0", environmentEntries) + "\0\0";
                environment = Marshal.StringToHGlobalUni(block);
            }
            var startup = new StartupInfo { dwFlags = STARTF_USESTDHANDLES, hStdOutput = stdout, hStdError = stderr, hStdInput = IntPtr.Zero };
            var command = new StringBuilder(commandLine);
            var flags = CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
            if (!CreateProcessW(applicationName, command, attributes, attributes, true, flags, environment, currentDirectory, startup, out pi)) { result.Error = LastError("CreateProcess"); return result; }
            if (!AssignProcessToJobObject(job, pi.hProcess))
            {
                result.Error = LastError("AssignProcessToJobObject");
                TerminateProcess(pi.hProcess, 1);
                WaitForSingleObject(pi.hProcess, 5000);
                return result;
            }
            if (ResumeThread(pi.hThread) == UInt32.MaxValue)
            {
                result.Error = LastError("ResumeThread");
                TerminateJobObject(job, 1);
                WaitForSingleObject(pi.hProcess, 5000);
                return result;
            }
            if (!CloseHandle(pi.hThread))
            {
                result.Error = LastError("CloseHandle thread");
                TerminateJobObject(job, 1);
                WaitForSingleObject(pi.hProcess, 5000);
                return result;
            }
            pi.hThread = IntPtr.Zero;
            result.Success = true;
            result.ProcessId = pi.dwProcessId;
            result.ProcessHandle = pi.hProcess;
            result.JobHandle = job;
            job = IntPtr.Zero;
            pi.hProcess = IntPtr.Zero;
            return result;
        }
        finally
        {
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (stdout != IntPtr.Zero && stdout != INVALID_HANDLE_VALUE) CloseHandle(stdout);
            if (stderr != IntPtr.Zero && stderr != INVALID_HANDLE_VALUE) CloseHandle(stderr);
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero)
            {
                if (!result.Success) { TerminateProcess(pi.hProcess, 1); WaitForSingleObject(pi.hProcess, 5000); }
                CloseHandle(pi.hProcess);
            }
            if (job != IntPtr.Zero)
            {
                if (!result.Success) TerminateJobObject(job, 1);
                CloseHandle(job);
            }
        }
    }

    public static int QueryActiveProcessCount(IntPtr job, out string error)
    {
        error = null;
        var info = new BasicAccountingInformation();
        if (!QueryInformationJobObject(job, 1, ref info, (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero)) { error = LastError("QueryInformationJobObject"); return -1; }
        return (int)info.ActiveProcesses;
    }

    public static CleanupResult Cleanup(LaunchResult launch, bool terminate, int timeoutMilliseconds)
    {
        var result = new CleanupResult { Success = false, ProcessExited = false, JobEmpty = false, HandlesClosed = false, ActiveProcesses = -1 };
        if (launch == null || !launch.Success || launch.ProcessHandle == IntPtr.Zero || launch.JobHandle == IntPtr.Zero) { result.Error = "No valid job-contained launch was available for cleanup."; return result; }
        var errors = new StringBuilder();
        string queryError;
        var active = QueryActiveProcessCount(launch.JobHandle, out queryError);
        if (active < 0) errors.Append(queryError + " ");
        if (active > 0 && terminate)
        {
            if (!TerminateJobObject(launch.JobHandle, 1)) errors.Append(LastError("TerminateJobObject") + " ");
        }
        var wait = WaitForSingleObject(launch.ProcessHandle, (uint)Math.Max(1, timeoutMilliseconds));
        result.ProcessExited = wait == WAIT_OBJECT_0;
        if (!result.ProcessExited) errors.Append(wait == WAIT_TIMEOUT ? "Process did not exit before cleanup timeout. " : LastError("WaitForSingleObject") + " ");
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1, timeoutMilliseconds));
        do
        {
            active = QueryActiveProcessCount(launch.JobHandle, out queryError);
            if (active < 0) { errors.Append(queryError + " "); break; }
            if (active == 0) break;
            if (!terminate)
            {
                if (!TerminateJobObject(launch.JobHandle, 1)) errors.Append("Unexpected residual job processes; " + LastError("TerminateJobObject") + " ");
                else errors.Append("Unexpected residual job processes were found and terminated. ");
                terminate = true;
            }
            System.Threading.Thread.Sleep(25);
        } while (DateTime.UtcNow < deadline);
        result.ActiveProcesses = active;
        result.JobEmpty = active == 0;
        var processClosed = CloseHandle(launch.ProcessHandle);
        var jobClosed = CloseHandle(launch.JobHandle);
        result.HandlesClosed = processClosed && jobClosed;
        if (!processClosed) errors.Append(LastError("CloseHandle process") + " ");
        if (!jobClosed) errors.Append(LastError("CloseHandle job") + " ");
        result.Error = errors.Length == 0 ? null : errors.ToString().Trim();
        result.Success = result.ProcessExited && result.JobEmpty && result.HandlesClosed && result.Error == null;
        return result;
    }
}
'@
}

function Fail([string]$Message) {
    throw "[api-host-reuse] $Message"
}

function Write-JsonFile([string]$Path, [object]$Value) {
    $directory = Split-Path -Parent $Path
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    $Value | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $Path -Encoding utf8
}

function ConvertTo-WindowsArgument([string]$Value) {
    if ($null -eq $Value -or $Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') { [void]$builder.Append(('\' * (($slashes * 2) + 1))); [void]$builder.Append('"'); $slashes = 0; continue }
        if ($slashes -gt 0) { [void]$builder.Append(('\' * $slashes)); $slashes = 0 }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) { [void]$builder.Append(('\' * ($slashes * 2))) }
    [void]$builder.Append('"')
    $builder.ToString()
}

function Get-EnvironmentEntries([hashtable]$Overrides) {
    $values = @{}
    foreach ($entry in Get-ChildItem Env:) { $values[$entry.Name] = [string]$entry.Value }
    if ($Overrides) { foreach ($entry in $Overrides.GetEnumerator()) { $values[$entry.Key] = [string]$entry.Value } }
    @($values.GetEnumerator() | Sort-Object Name | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value })
}

function Start-JobContainedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$StdoutPath,
        [Parameter(Mandatory = $true)][string]$StderrPath,
        [hashtable]$Environment
    )
    $resolvedFileName = $FileName
    if (-not [IO.Path]::IsPathRooted($resolvedFileName) -and $resolvedFileName -notmatch '[\\/]') {
        $commandInfo = Get-Command $resolvedFileName -CommandType Application -ErrorAction Stop | Select-Object -First 1
        if (-not $commandInfo -or [string]::IsNullOrWhiteSpace([string]$commandInfo.Source)) { Fail "Could not resolve executable on PATH: $FileName" }
        $resolvedFileName = [string]$commandInfo.Source
    }
    $command = @($resolvedFileName) + @($Arguments) | ForEach-Object { ConvertTo-WindowsArgument ([string]$_) }
    $launch = [AeroLinkJobNative]::Launch($resolvedFileName, ($command -join ' '), $WorkingDirectory, $StdoutPath, $StderrPath, (Get-EnvironmentEntries $Environment))
    if (-not $launch.Success) { Fail "Job-contained launch failed for ${FileName}: $($launch.Error)" }
    $launch
}

function Get-BoundedTextFile([string]$Path, [int]$MaxBytes = 8388608) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.Length -gt $MaxBytes) { Fail "Captured process output exceeded the $MaxBytes byte safety bound: $Path" }
    [IO.File]::ReadAllText($Path)
}

function Stop-JobContainedProcess {
    param(
        [Parameter(Mandatory = $true)]$Launch,
        [switch]$Terminate,
        [int]$TimeoutMilliseconds = 5000
    )
    if ($null -eq $Launch) { return [pscustomobject]@{ exited = $false; remainingIds = @(); error = 'No job-contained launch was available for cleanup.' } }
    $cleanup = [AeroLinkJobNative]::Cleanup($Launch, [bool]$Terminate, $TimeoutMilliseconds)
    [pscustomobject]@{
        exited = [bool]$cleanup.Success
        remainingIds = if ($cleanup.ActiveProcesses -gt 0) { @($Launch.ProcessId) } else { @() }
        error = if ($cleanup.Error) { [string]$cleanup.Error } elseif (-not $cleanup.Success) { 'Job containment cleanup did not prove process exit, job drain, and handle closure.' } else { $null }
        jobEmpty = [bool]$cleanup.JobEmpty
        handlesClosed = [bool]$cleanup.HandlesClosed
    }
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [hashtable]$Environment,
        [int]$TimeoutSeconds = ($ProcessTimeoutMinutes * 60)
    )

    $scratch = Join-Path ([IO.Path]::GetTempPath()) ("aerolink-captured-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $stdoutPath = Join-Path $scratch 'stdout.log'
    $stderrPath = Join-Path $scratch 'stderr.log'
    $launch = $null
    $process = $null
    $startedAt = [DateTimeOffset]::UtcNow
    $timedOut = $false
    $cleanup = $null
    $primaryError = $null
    $stdout = ''
    $stderr = ''
    $exitCode = 1
    try {
        $launch = Start-JobContainedProcess -FileName $FileName -Arguments $Arguments -WorkingDirectory $WorkingDirectory -StdoutPath $stdoutPath -StderrPath $stderrPath -Environment $Environment
        $process = [System.Diagnostics.Process]::GetProcessById($launch.ProcessId)
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        $exitCode = if ($timedOut) { 124 } else { [int]$process.ExitCode }
        $cleanup = Stop-JobContainedProcess -Launch $launch -Terminate:$timedOut -TimeoutMilliseconds 5000
        $stdout = Get-BoundedTextFile $stdoutPath
        $stderr = Get-BoundedTextFile $stderrPath
    } catch {
        $primaryError = $_
    } finally {
        try {
            if ($launch -and $null -eq $cleanup) {
                $cleanup = Stop-JobContainedProcess -Launch $launch -Terminate -TimeoutMilliseconds 5000
            }
        } catch {
            $cleanup = [pscustomobject]@{ exited = $false; remainingIds = @($launch.ProcessId); error = $_.Exception.Message; jobEmpty = $false; handlesClosed = $false }
        }
        try { if (Test-Path -LiteralPath $stdoutPath) { Remove-Item -LiteralPath $stdoutPath -Force -ErrorAction SilentlyContinue } } catch { }
        try { if (Test-Path -LiteralPath $stderrPath) { Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue } } catch { }
        try { if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue } } catch { }
    }
    if ($primaryError) {
        $message = "Process $FileName failed after start: $($primaryError.Exception.Message)"
        $cleanupFailed = ($null -eq $cleanup) -or (-not [bool]$cleanup.exited) -or (@($cleanup.remainingIds).Count -gt 0) -or $cleanup.error
        if ($cleanupFailed) {
            $cleanupMessage = if ($cleanup -and $cleanup.error) { [string]$cleanup.error } elseif ($cleanup) { "remaining owned processes: $(@($cleanup.remainingIds) -join ',')" } else { 'cleanup result was unavailable' }
            $message += " Cleanup failure: $cleanupMessage"
        }
        throw $message
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        Stdout = $stdout
        Stderr = $stderr
        TimedOut = $timedOut
        Cleanup = $cleanup
        StartedAt = $startedAt
        EndedAt = [DateTimeOffset]::UtcNow
    }
}

function Invoke-JobContainmentSmoke {
    $root = Join-Path ([IO.Path]::GetTempPath()) ("aerolink-job-smoke-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    try {
        $childScript = '$child = Start-Process -FilePath ''pwsh'' -ArgumentList @(''-NoProfile'', ''-Command'', ''Start-Sleep -Seconds 30'') -PassThru; Start-Sleep -Milliseconds 250'
        $result = Invoke-CapturedProcess -FileName 'pwsh' -Arguments @('-NoProfile', '-Command', $childScript) -WorkingDirectory $root -TimeoutSeconds 20
        if ($null -eq $result.Cleanup -or -not [bool]$result.Cleanup.jobEmpty -or @($result.Cleanup.remainingIds).Count -ne 0) {
            Fail 'Job containment smoke did not prove that the late-spawned child was drained.'
        }
        [ordered]@{ smoke = 'job-containment'; exitCode = $result.ExitCode; jobEmpty = $result.Cleanup.jobEmpty; handlesClosed = $result.Cleanup.handlesClosed; cleanupError = $result.Cleanup.error } | ConvertTo-Json -Depth 10
    } finally {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Get-RepoInfo([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { Fail "Worktree does not exist: $Path" }
    $headResult = Invoke-CapturedProcess -FileName 'git' -Arguments @('-C', $Path, 'rev-parse', 'HEAD') -WorkingDirectory $Path
    $head = $headResult.Stdout.Trim()
    if ($headResult.ExitCode -ne 0) { Fail "Could not read the Git head for ${Path}: $head`n$($headResult.Stderr)" }
    $statusResult = Invoke-CapturedProcess -FileName 'git' -Arguments @('-C', $Path, 'status', '--porcelain') -WorkingDirectory $Path
    $status = @($statusResult.Stdout -split "`r?`n" | Where-Object { $_ -ne '' })
    if ($statusResult.ExitCode -ne 0) { Fail "Could not read Git status for $Path.`n$($statusResult.Stderr)" }
    $branchResult = Invoke-CapturedProcess -FileName 'git' -Arguments @('-C', $Path, 'branch', '--show-current') -WorkingDirectory $Path
    $branch = $branchResult.Stdout.Trim()
    if ($branchResult.ExitCode -ne 0) { Fail "Could not read Git branch for $Path.`n$($branchResult.Stderr)" }
    [pscustomobject]@{
        path = (Get-CanonicalPath $Path)
        head = $head
        branch = $branch
        clean = ($status.Count -eq 0)
        status = @($status)
    }
}

function Get-EnvironmentInfo([string]$Path) {
    $dotnetResult = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('--version') -WorkingDirectory $Path
    if ($dotnetResult.ExitCode -ne 0) { Fail "Could not read the .NET SDK version in $Path. ExitCode=$($dotnetResult.ExitCode) TimedOut=$($dotnetResult.TimedOut)`nstdout=$($dotnetResult.Stdout)`nstderr=$($dotnetResult.Stderr)" }
    $nodeResult = Invoke-CapturedProcess -FileName $NodeExecutable -Arguments @('--version') -WorkingDirectory $Path
    if ($nodeResult.ExitCode -ne 0) { Fail "Could not read the Node.js version in $Path. ExitCode=$($nodeResult.ExitCode) TimedOut=$($nodeResult.TimedOut)`nstdout=$($nodeResult.Stdout)`nstderr=$($nodeResult.Stderr)" }
    $dotnet = $dotnetResult.Stdout.Trim()
    $node = $nodeResult.Stdout.Trim()
    $os = try { Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber } catch { $null }
    $cpu = try { @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors) } catch { $null }
    [ordered]@{
        os = $os
        cpu = $cpu
        powershell = $PSVersionTable.PSVersion.ToString()
        dotnet = $dotnet
        node = $node
        processorCount = [Environment]::ProcessorCount
        machine = [Environment]::MachineName
        user = [Environment]::UserName
        worktree = (Get-CanonicalPath $Path)
    }
}

function Get-EnvironmentFingerprint([object]$Environment) {
    $canonical = [ordered]@{
        os = $Environment.os
        cpu = $Environment.cpu
        powershell = $Environment.powershell
        dotnet = $Environment.dotnet
        node = $Environment.node
        processorCount = $Environment.processorCount
        machine = $Environment.machine
    } | ConvertTo-Json -Depth 20 -Compress
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try { ([System.BitConverter]::ToString($hash.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '').ToLowerInvariant() } finally { $hash.Dispose() }
}

function Get-TestManifest([string]$Worktree, [string]$ListFile) {
    $raw = if ($ListFile) {
        if (-not (Test-Path -LiteralPath $ListFile -PathType Leaf)) { Fail "Test-list file does not exist: $ListFile" }
        @(Get-Content -LiteralPath $ListFile)
    } else {
        $project = Join-Path $Worktree $ProjectPath
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { Fail "API test project does not exist: $project" }
        $result = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('test', $project, '--configuration', 'Release', '--no-build', '--list-tests') -WorkingDirectory $Worktree
        if ($result.ExitCode -ne 0) { Fail "Test discovery failed in $Worktree.`n$($result.Stdout)`n$($result.Stderr)" }
        @($result.Stdout -split "`r?`n")
    }

    $cases = [System.Collections.Generic.List[object]]::new()
    foreach ($line in $raw) {
        $name = ([string]$line).Trim()
        if (-not $name.StartsWith('AeroLink.Api.Tests.', [StringComparison]::Ordinal)) { continue }
        $baseName = $name.Split('(')[0]
        $lastDot = $baseName.LastIndexOf('.')
        if ($lastDot -lt 1 -or $lastDot -ge $baseName.Length - 1) { continue }
        $className = $baseName.Substring(0, $lastDot)
        $methodName = $baseName.Substring($lastDot + 1)
        if ($className -eq 'AeroLink.Api.Tests') { continue }
        $cases.Add([pscustomobject]@{ name = $name; className = $className; methodName = $methodName })
    }
    if ($cases.Count -eq 0) { Fail 'No AeroLink.Api.Tests cases were discovered.' }

    $classes = @($cases | Group-Object className | ForEach-Object {
        [pscustomobject]@{
            name = $_.Name
            cases = $_.Count
            caseNames = @($_.Group | ForEach-Object name | Sort-Object)
        }
    } | Sort-Object name)
    $manifest = [pscustomobject]@{
        cases = @($cases)
        classes = $classes
        caseCount = $cases.Count
        classCount = $classes.Count
    }
    $manifestHashInput = ((@($manifest.cases | ForEach-Object name | Sort-Object) -join "`n") + "`n")
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try { $manifestHash = ([System.BitConverter]::ToString($hash.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($manifestHashInput)))).Replace('-', '').ToLowerInvariant() } finally { $hash.Dispose() }
    $manifest | Add-Member -NotePropertyName manifestHash -NotePropertyValue $manifestHash
    $manifest | Add-Member -NotePropertyName classFacts -NotePropertyValue @($manifest.classes | ForEach-Object { [ordered]@{ name = $_.name; cases = $_.cases; caseNames = @($_.caseNames) } })
    $manifest
}

function Get-ManifestFacts([object]$Manifest) {
    [ordered]@{
        manifestHash = [string]$Manifest.manifestHash
        caseCount = [int]$Manifest.caseCount
        classCount = [int]$Manifest.classCount
        classFacts = @($Manifest.classFacts)
        caseNames = @($Manifest.cases | ForEach-Object name | Sort-Object)
    }
}

function Shuffle-Items([object[]]$Items, [System.Random]$Random) {
    $copy = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $Items) { $copy.Add($item) }
    for ($index = $copy.Count - 1; $index -gt 0; $index--) {
        $swap = $Random.Next($index + 1)
        $value = $copy[$index]
        $copy[$index] = $copy[$swap]
        $copy[$swap] = $value
    }
    @($copy)
}

function New-Partition([object[]]$Classes, [int]$Seed, [int]$Count) {
    if ($Count -gt $Classes.Count) { Fail "ShardCount ($Count) cannot exceed discovered class count ($($Classes.Count)); an empty filter would run the full suite." }
    $random = [System.Random]::new($Seed)
    $shuffled = Shuffle-Items $Classes $random
    $loads = [int[]](0..($Count - 1) | ForEach-Object { 0 })
    $shards = [object[]]::new($Count)
    for ($index = 0; $index -lt $Count; $index++) { $shards[$index] = [System.Collections.Generic.List[object]]::new() }
    foreach ($class in $shuffled) {
        $lowest = ($loads | Measure-Object -Minimum).Minimum
        $candidates = @(0..($Count - 1) | Where-Object { $loads[$_] -eq $lowest })
        $shard = $candidates[$random.Next($candidates.Count)]
        $shards[$shard].Add($class)
        $loads[$shard] += [int]$class.cases
    }
    $entries = @(0..($Count - 1) | ForEach-Object {
        $ordered = @($shards[$_] | Sort-Object name)
        [pscustomobject]@{
            shard = $_ + 1
            expectedCases = $loads[$_]
            classes = @($ordered | ForEach-Object { $_.name })
            caseNames = @($ordered | ForEach-Object { $_.caseNames } | Sort-Object)
            filters = (@($ordered | ForEach-Object { "FullyQualifiedName~$($_.name)." }) -join '|')
        }
    })
    [pscustomobject]@{
        algorithm = 'fisher-yates-then-lightest-shard'
        seed = $Seed
        shardCount = $Count
        totalCases = ($loads | Measure-Object -Sum).Sum
        shards = $entries
    }
}

function Assert-SameManifest([object]$Expected, [object]$Actual) {
    $expectedNames = @($Expected.classes | ForEach-Object name | Sort-Object)
    $actualNames = @($Actual.classes | ForEach-Object name | Sort-Object)
    if (Compare-Object $expectedNames $actualNames) { Fail 'Baseline and treatment discovered different API class sets.' }
    $expectedCases = @($Expected.cases | ForEach-Object name | Sort-Object)
    $actualCases = @($Actual.cases | ForEach-Object name | Sort-Object)
    if (Compare-Object $expectedCases $actualCases) { Fail 'Baseline and treatment discovered different API test-case names.' }
    if ($Expected.caseCount -ne $Actual.caseCount) { Fail "Baseline/treatment case counts differ: $($Expected.caseCount) vs $($Actual.caseCount)." }
    if ([string]$Expected.manifestHash -ne [string]$Actual.manifestHash) { Fail 'Baseline and treatment manifest hashes differ.' }
}

function Assert-SummaryManifestMatchesLive([object]$Persisted, [object]$Live, [string]$Condition) {
    $persistedNames = @($Persisted.caseNames | ForEach-Object { [string]$_ } | Sort-Object)
    $liveNames = @($Live.cases | ForEach-Object name | Sort-Object)
    if (Compare-Object $persistedNames $liveNames) { Fail "$Condition persisted case-name manifest differs from live discovery." }
    if ([string]$Persisted.manifestHash -ne [string]$Live.manifestHash) { Fail "$Condition persisted manifest hash differs from live discovery." }
    if ([int]$Persisted.caseCount -ne [int]$Live.caseCount -or [int]$Persisted.classCount -ne [int]$Live.classCount) { Fail "$Condition persisted manifest counts differ from live discovery." }
    $persistedFacts = @($Persisted.classFacts | ForEach-Object { "{0}|{1}|{2}" -f $_.name, $_.cases, (@($_.caseNames) -join "`u{1f}") } | Sort-Object)
    $liveFacts = @($Live.classFacts | ForEach-Object { "{0}|{1}|{2}" -f $_.name, $_.cases, (@($_.caseNames) -join "`u{1f}") } | Sort-Object)
    if (Compare-Object $persistedFacts $liveFacts) { Fail "$Condition persisted class facts differ from live discovery." }
}

function Assert-SummaryPartitionsMatchLive([object]$Summary, [object]$LiveManifest, [string]$Condition) {
    $liveLoads = @{}
    foreach ($class in @($LiveManifest.classes)) { $liveLoads[[string]$class.name] = [int]$class.cases }
    $liveNames = @($LiveManifest.classes | ForEach-Object name | Sort-Object)
    foreach ($observation in @($Summary.observations)) {
        $seed = 0
        if (-not [int]::TryParse(([string]$observation.seed), [ref]$seed)) { Fail "$Condition partition has a non-integer observation seed." }
        $partition = $observation.partition
        if ($null -eq $partition -or [string]$partition.algorithm -ne 'fisher-yates-then-lightest-shard') { Fail "$Condition seed $seed partition algorithm is not authoritative." }
        if ([int]$partition.seed -ne $seed) { Fail "$Condition seed $seed partition seed does not match the observation." }
        $shardCount = [int]$partition.shardCount
        if ($shardCount -lt 1 -or $shardCount -gt $liveNames.Count) { Fail "$Condition seed $seed partition shardCount is invalid." }
        $shards = @($partition.shards)
        if ($shards.Count -ne $shardCount) { Fail "$Condition seed $seed partition shard count is incomplete." }
        $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $recomputedCases = 0
        foreach ($shard in $shards) {
            $classes = @($shard.classes | ForEach-Object { [string]$_ })
            $partitionCaseNames = @($shard.caseNames | ForEach-Object { [string]$_ } | Sort-Object)
            if ($partitionCaseNames.Count -ne [int]$shard.expectedCases) { Fail "$Condition seed $seed shard $($shard.shard) case-name load does not match expectedCases." }
            $expectedShardCaseNames = @($classes | ForEach-Object { @($liveLoads[$_]) } )
            $expectedShardCaseNames = @($LiveManifest.classes | Where-Object { $classes -contains [string]$_.name } | ForEach-Object { $_.caseNames } | Sort-Object)
            if (Compare-Object $partitionCaseNames $expectedShardCaseNames) { Fail "$Condition seed $seed shard $($shard.shard) case-name set does not match live class loads." }
            foreach ($className in $classes) {
                if (-not $seen.Add($className)) { Fail "$Condition seed $seed partition repeats class '$className'." }
                if (-not $liveLoads.ContainsKey($className)) { Fail "$Condition seed $seed partition contains unknown class '$className'." }
            }
            $expectedCases = ($classes | ForEach-Object { $liveLoads[$_] } | Measure-Object -Sum).Sum
            if ([int]$shard.expectedCases -ne [int]$expectedCases) { Fail "$Condition seed $seed shard $($shard.shard) expectedCases does not match live class loads." }
            $expectedFilters = (@($classes | ForEach-Object { "FullyQualifiedName~$($_)." }) -join '|')
            if ([string]$shard.filters -cne $expectedFilters) { Fail "$Condition seed $seed shard $($shard.shard) filters do not match its exact class list." }
            $recomputedCases += [int]$expectedCases
        }
        if (Compare-Object @($seen | Sort-Object) $liveNames) { Fail "$Condition seed $seed partition class union does not exactly match live discovery." }
        if ([int]$partition.totalCases -ne [int]$LiveManifest.caseCount -or $recomputedCases -ne [int]$LiveManifest.caseCount) { Fail "$Condition seed $seed partition totalCases does not match live discovery." }
        $expected = New-Partition $LiveManifest.classes $seed $shardCount
        if ([string]$partition.algorithm -ne [string]$expected.algorithm -or [int]$partition.totalCases -ne [int]$expected.totalCases) { Fail "$Condition seed $seed partition does not match the authoritative seeded planner." }
        for ($index = 0; $index -lt $shardCount; $index++) {
            $actualShard = $shards[$index]
            $expectedShard = @($expected.shards)[$index]
            if ([int]$actualShard.shard -ne [int]$expectedShard.shard -or
                [int]$actualShard.expectedCases -ne [int]$expectedShard.expectedCases -or
                (Compare-Object @($actualShard.classes) @($expectedShard.classes)) -or
                (Compare-Object @($actualShard.caseNames | Sort-Object) @($expectedShard.caseNames | Sort-Object)) -or
                [string]$actualShard.filters -cne [string]$expectedShard.filters) {
                Fail "$Condition seed $seed shard $($index + 1) does not match the authoritative seeded planner."
            }
        }
    }
}

function Get-ProcessIdentityResult([int]$Id) {
    try {
        $record = Get-CimInstance Win32_Process -Filter "ProcessId = $Id" -ErrorAction Stop | Select-Object -First 1 ProcessId, ParentProcessId, CreationDate, Name
        if (-not $record) { return [pscustomobject]@{ found = $false; identity = $null; error = $null } }
        return [pscustomobject]@{
            found = $true
            identity = [pscustomobject]@{ processId = [int]$record.ProcessId; parentProcessId = [int]$record.ParentProcessId; creationDate = [string]$record.CreationDate; name = [string]$record.Name }
            error = $null
        }
    } catch {
        return [pscustomobject]@{ found = $false; identity = $null; error = "CIM identity lookup failed for PID ${Id}: $($_.Exception.Message)" }
    }
}

function Get-ProcessIdentity([int]$Id) {
    $result = Get-ProcessIdentityResult $Id
    if ($result.error) { return $null }
    $result.identity
}

function Get-ProcessTreeSnapshot([int[]]$RootIds) {
    $all = $null
    try { $all = @(Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ProcessId, ParentProcessId, CreationDate, Name) }
    catch { return [pscustomobject]@{ success = $false; rootAvailable = $false; records = @(); error = "Process-tree enumeration failed: $($_.Exception.Message)" } }
    if (@($all | Where-Object { $null -eq $_.ProcessId -or $null -eq $_.ParentProcessId -or [string]::IsNullOrWhiteSpace([string]$_.CreationDate) }).Count -gt 0) {
        return [pscustomobject]@{ success = $false; rootAvailable = $false; records = @(); error = 'Process-tree enumeration returned incomplete identity records.' }
    }
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($root in $RootIds) { [void]$ids.Add([int]$root) }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($process in $all) {
            if ($ids.Contains([int]$process.ParentProcessId) -and $ids.Add([int]$process.ProcessId)) {
                if ($ids.Count -gt $MaxProcessTreeCount) {
                    return [pscustomobject]@{ success = $false; rootAvailable = $false; records = @(); error = "Owned process tree exceeded MaxProcessTreeCount=$MaxProcessTreeCount." }
                }
                $changed = $true
            }
        }
    }
    $records = @($all | Where-Object { $ids.Contains([int]$_.ProcessId) } | ForEach-Object {
        [pscustomobject]@{ processId = [int]$_.ProcessId; parentProcessId = [int]$_.ParentProcessId; creationDate = [string]$_.CreationDate; name = [string]$_.Name }
    })
    [pscustomobject]@{
        success = $true
        rootAvailable = (@($records | Where-Object { $RootIds -contains $_.processId }).Count -gt 0)
        records = $records
        error = $null
    }
}

function Get-IoRate([System.Collections.Generic.HashSet[int]]$Ids) {
    try {
        $idSamples = @(Get-Counter -Counter '\Process(*)\ID Process' -ErrorAction Stop).CounterSamples
        $pidByInstance = @{}
        foreach ($sample in $idSamples) { $pidByInstance[$sample.InstanceName] = [int]$sample.CookedValue }
        $ioSamples = @(Get-Counter -Counter '\Process(*)\IO Read Bytes/sec', '\Process(*)\IO Write Bytes/sec' -ErrorAction Stop).CounterSamples
        $read = 0.0
        $write = 0.0
        foreach ($sample in $ioSamples) {
            $pid = $pidByInstance[$sample.InstanceName]
            if ($null -eq $pid -or -not $Ids.Contains([int]$pid)) { continue }
            if ($sample.Path -match 'IO Read Bytes/sec$') { $read += [double]$sample.CookedValue }
            if ($sample.Path -match 'IO Write Bytes/sec$') { $write += [double]$sample.CookedValue }
        }
        [pscustomobject]@{ available = $true; readPerSecond = $read; writePerSecond = $write; error = $null }
    } catch {
        [pscustomobject]@{ available = $false; readPerSecond = $null; writePerSecond = $null; error = $_.Exception.Message }
    }
}

function Get-ProcessSample([int[]]$RootIds) {
    $snapshot = Get-ProcessTreeSnapshot $RootIds
    if (-not $snapshot.success) {
        return [pscustomobject]@{ ids = @(); records = @(); rootAvailable = $false; processTreeAvailable = $false; processTreeError = $snapshot.error; cpuMs = 0.0; cpuAvailable = $false; io = [pscustomobject]@{ available = $false; error = $snapshot.error }; at = [DateTimeOffset]::UtcNow }
    }
    $records = @($snapshot.records)
    $ids = @($records | ForEach-Object processId)
    $cpu = 0.0
    $cpuSamples = 0
    foreach ($id in $ids) {
        try { $cpu += (Get-Process -Id $id -ErrorAction Stop).TotalProcessorTime.TotalMilliseconds; $cpuSamples++ } catch { }
    }
    $rootAvailable = $false
    $rootAvailable = $snapshot.rootAvailable
    $io = Get-IoRate ([System.Collections.Generic.HashSet[int]]::new([int[]]$ids))
    [pscustomobject]@{ ids = $ids; records = $records; rootAvailable = $rootAvailable; processTreeAvailable = $true; processTreeError = $null; cpuMs = $cpu; cpuAvailable = ($rootAvailable -and $cpuSamples -gt 0); io = $io; at = [DateTimeOffset]::UtcNow }
}

function New-TestProcess {
    param(
        [string]$Worktree,
        [string]$Filter,
        [string]$TelemetryPath,
        [string]$ResultsPath,
        [string]$StdoutPath,
        [string]$StderrPath
    )
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $TelemetryPath), $ResultsPath, (Split-Path -Parent $StdoutPath) | Out-Null
    $project = Join-Path $Worktree $ProjectPath
    $arguments = @('test', $project, '--configuration', 'Release', '--no-build', '--filter', $Filter,
        '--logger', 'console;verbosity=normal', '--logger', 'trx;LogFileName=shard.trx',
        '--results-directory', $ResultsPath)
    $startedAt = [DateTimeOffset]::UtcNow
    $launch = $null
    $process = $null
    try {
        $launch = Start-JobContainedProcess -FileName $DotnetExecutable -Arguments $arguments -WorkingDirectory $Worktree -StdoutPath $StdoutPath -StderrPath $StderrPath -Environment @{ AEROLINK_API_TELEMETRY_JSONL = $TelemetryPath; DOTNET_CLI_TELEMETRY_OPTOUT = '1' }
        $process = [System.Diagnostics.Process]::GetProcessById($launch.ProcessId)
        $rootIdentity = Get-ProcessIdentity $process.Id
        $initialTree = Get-ProcessTreeSnapshot @($process.Id)
        [pscustomobject]@{
            job = $launch
            process = $process
            rootIdentity = $rootIdentity
            processTreeAvailable = $initialTree.success
            processTreeError = $initialTree.error
            shardStartedAt = $startedAt
            telemetryPath = $TelemetryPath
            resultsPath = $ResultsPath
            stdoutPath = $StdoutPath
            stderrPath = $StderrPath
            maxCpuMs = 0.0
            diskReadBytes = 0.0
            diskWriteBytes = 0.0
            ioAvailable = $true
            ioError = $null
            cpuAvailable = $true
            cpuError = $null
            timedOut = $false
            cleanupFailure = $null
            waitError = $null
            ownedProcessIds = @($initialTree.records | ForEach-Object processId)
            ownedProcessRecords = @($initialTree.records)
            lastSampleAt = $startedAt
            samples = 0
            successfulSamples = 0
        }
    } catch {
        $primary = $_.Exception.Message
        $cleanup = $null
        try {
            if ($launch) { $cleanup = Stop-JobContainedProcess -Launch $launch -Terminate -TimeoutMilliseconds 5000 }
        } catch { $cleanup = [pscustomobject]@{ exited = $false; remainingIds = @($launch.ProcessId); error = $_.Exception.Message } }
        $cleanupFailed = ($null -eq $cleanup) -or (-not [bool]$cleanup.exited) -or (@($cleanup.remainingIds).Count -gt 0) -or $cleanup.error
        if ($cleanupFailed) {
            $cleanupMessage = if ($cleanup -and $cleanup.error) { [string]$cleanup.error } elseif ($cleanup) { "remaining owned processes: $(@($cleanup.remainingIds) -join ',')" } else { 'cleanup result was unavailable' }
            throw "API shard launch failed: $primary Cleanup failure: $cleanupMessage"
        }
        throw $primary
    }
}

function Wait-TestProcesses([object[]]$Shards) {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes($TimeoutMinutes)
    try {
        while ($true) {
            $active = @($Shards | Where-Object { -not $_.process.HasExited })
            foreach ($shard in $active) {
                try {
                    $sample = Get-ProcessSample @($shard.process.Id)
                    if (-not $sample.processTreeAvailable) {
                        $shard.processTreeAvailable = $false
                        $shard.processTreeError = $sample.processTreeError
                        $shard.cpuAvailable = $false
                        $shard.ioAvailable = $false
                        continue
                    }
                    if (-not $sample.rootAvailable) { continue }
                    $shard.processTreeAvailable = $true
                    $shard.ownedProcessIds = @($shard.ownedProcessIds + $sample.ids | Sort-Object -Unique)
                    $shard.ownedProcessRecords = @($shard.ownedProcessRecords + $sample.records | Sort-Object processId, creationDate -Unique)
                    $shard.samples++
                    if ($sample.cpuAvailable) { $shard.successfulSamples++ }
                    if ($sample.cpuMs -gt $shard.maxCpuMs) { $shard.maxCpuMs = $sample.cpuMs }
                    $elapsedSeconds = ($sample.at - $shard.lastSampleAt).TotalSeconds
                    if ($sample.io.available) {
                        $shard.diskReadBytes += $sample.io.readPerSecond * [math]::Max(0, $elapsedSeconds)
                        $shard.diskWriteBytes += $sample.io.writePerSecond * [math]::Max(0, $elapsedSeconds)
                    } else {
                        $shard.ioAvailable = $false
                        $shard.ioError = $sample.io.error
                    }
                    if (-not $sample.cpuAvailable) {
                        $shard.cpuAvailable = $false
                        $shard.cpuError = 'No active process-tree CPU sample was available.'
                    }
                    $shard.lastSampleAt = $sample.at
                } catch {
                    $shard.waitError = $_.Exception.Message
                    $shard.cpuAvailable = $false
                    $shard.ioAvailable = $false
                }
            }
            if ($active.Count -eq 0) { break }
            if ([DateTimeOffset]::UtcNow -gt $deadline) {
                foreach ($shard in $active) {
                    $cleanup = Stop-JobContainedProcess -Launch $shard.job -Terminate -TimeoutMilliseconds 5000
                    $shard.timedOut = $true
                    if (-not $cleanup.exited) { $shard.cleanupFailure = "Job-contained process did not exit: $($cleanup.remainingIds -join ',')" }
                    if ($cleanup.error) { $shard.cleanupFailure = $cleanup.error }
                }
                break
            }
            Start-Sleep -Milliseconds 500
        }
    } catch {
        foreach ($shard in $Shards) { $shard.waitError = $_.Exception.Message }
    } finally {
        foreach ($shard in $Shards) {
            try {
                if ($shard.job) {
                    $cleanup = Stop-JobContainedProcess -Launch $shard.job -Terminate:(!$shard.process.HasExited) -TimeoutMilliseconds 5000
                    if (-not $cleanup.exited) { $shard.cleanupFailure = "Job containment cleanup was not proven: $($cleanup.remainingIds -join ',')" }
                    if ($cleanup.error) { $shard.cleanupFailure = $cleanup.error }
                }
            } catch { $shard.cleanupFailure = $_.Exception.Message }
            try { $shard.stdout = Get-BoundedTextFile $shard.stdoutPath } catch { $shard.stdout = ''; $shard.waitError = $_.Exception.Message }
            try { $shard.stderr = Get-BoundedTextFile $shard.stderrPath } catch { $shard.stderr = ''; $shard.waitError = $_.Exception.Message }
            $shard.endedAt = [DateTimeOffset]::UtcNow
            try { $shard.exitCode = $shard.process.ExitCode } catch { $shard.exitCode = $null }
            $shard.wallMs = ($shard.endedAt - $shard.shardStartedAt).TotalMilliseconds
        }
    }
}

function Get-TrxEvidence([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.Length -gt 33554432) { Fail "TRX exceeded the 32 MiB safety bound: $Path" }
    $xml = [xml](Get-Content -Raw -LiteralPath $Path)
    $unitTests = @{}
    foreach ($unitTest in @($xml.SelectNodes("//*[local-name()='UnitTest']"))) {
        $id = [string]$unitTest.id
        $name = [string]$unitTest.name
        if ($id.Length -gt 1024 -or $name.Length -gt 4096) { Fail "TRX test identity exceeded the safety bound: $Path" }
        if ($id) { $unitTests[$id] = $name }
    }
    $nodes = @($xml.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($nodes.Count -gt 100000) { Fail "TRX contained more than 100000 test results: $Path" }
    $names = [System.Collections.Generic.List[string]]::new()
    $counts = @{ total = $nodes.Count; passed = 0; failed = 0; skipped = 0; other = 0 }
    foreach ($node in $nodes) {
        $name = [string]$node.testName
        if (-not $name) { $name = [string]$unitTests[[string]$node.testId] }
        if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 4096) { Fail "TRX result had no bounded testName identity: $Path" }
        $names.Add($name)
        switch ([string]$node.outcome) {
            'Passed' { $counts.passed++ }
            'Failed' { $counts.failed++ }
            'Skipped' { $counts.skipped++ }
            default { $counts.other++ }
        }
    }
    [pscustomobject]@{ counts = [pscustomobject]$counts; names = @($names) }
}

function Compare-NameMultiset([string[]]$Expected, [string[]]$Actual) {
    $expectedCounts = [System.Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
    $actualCounts = [System.Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
    foreach ($name in @($Expected)) { if ($expectedCounts.ContainsKey($name)) { $expectedCounts[$name]++ } else { $expectedCounts[$name] = 1 } }
    foreach ($name in @($Actual)) { if ($actualCounts.ContainsKey($name)) { $actualCounts[$name]++ } else { $actualCounts[$name] = 1 } }
    $keySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($key in $expectedCounts.Keys) { [void]$keySet.Add($key) }
    foreach ($key in $actualCounts.Keys) { [void]$keySet.Add($key) }
    $keys = @($keySet | Sort-Object)
    $differences = @($keys | Where-Object { [int]$expectedCounts[$_] -ne [int]$actualCounts[$_] })
    if ($differences.Count -gt 0) {
        return (($differences | ForEach-Object { "$_ (expected=$($expectedCounts[$_]), actual=$($actualCounts[$_]))" }) -join '; ')
    }
    $null
}

function Invoke-TelemetryAggregator([string]$Worktree, [string]$TelemetryPath, [string]$TrxPath, [string]$OutputPath) {
    if (-not (Test-Path -LiteralPath $TelemetryPath -PathType Leaf) -or -not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        return [pscustomobject]@{ report = $null; malformed = $null; truncated = $null; output = 'telemetry or TRX missing'; exitCode = 2; timedOut = $false; cleanup = $null }
    }
    $aggregator = Join-Path $Worktree 'product/ci-metrics/bin/aggregate-api-telemetry.mjs'
    try { $result = Invoke-CapturedProcess -FileName $NodeExecutable -Arguments @($aggregator, $TelemetryPath, $TrxPath, $OutputPath) -WorkingDirectory $Worktree }
    catch { return [pscustomobject]@{ report = $null; malformed = $null; truncated = $null; output = $_.Exception.Message; exitCode = 1; timedOut = $false; cleanup = $null } }
    $combined = "$($result.Stdout)`n$($result.Stderr)"
    $malformed = $null
    $truncated = $null
    if ($combined -match '\((\d+) malformed lines, truncated=(true|false)\)') {
        $malformed = [int]$Matches[1]
        $truncated = [bool]::Parse($Matches[2])
    }
    $reportPath = Join-Path $OutputPath 'api-telemetry.json'
    $report = $null
    try { if (Test-Path -LiteralPath $reportPath -PathType Leaf) { $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json } } catch { $combined = "$combined`n$($_.Exception.Message)" }
    [pscustomobject]@{ report = $report; malformed = $malformed; truncated = $truncated; output = $combined; exitCode = $result.ExitCode; timedOut = $result.TimedOut; cleanup = $result.Cleanup }
}

function Invoke-Observation {
    param(
        [string]$Condition,
        [string]$Worktree,
        [object]$Partition,
        [int]$RunNumber,
        [int]$Seed,
        [string]$Root,
        [string[]]$Order,
        [object]$Environment,
        [object]$ConditionMetadata,
        [switch]$IsWarmup
    )
    $runDirectory = Join-Path $Root (if ($IsWarmup) { "warmup-$Condition-seed-$Seed" } else { "$Condition\run-$('{0:D2}' -f $RunNumber)-seed-$Seed" })
    if (Test-Path -LiteralPath $runDirectory) {
        $existing = @(Get-ChildItem -LiteralPath $runDirectory -Force -ErrorAction Stop)
        if ($existing.Count -gt 0) { Fail "Refusing to reuse non-empty observation directory: $runDirectory" }
    }
    New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
    $shards = @()
    $launchError = $null
    try {
        foreach ($entry in $Partition.shards) {
            $shardDirectory = Join-Path $runDirectory "shard-$($entry.shard)"
            $telemetry = Join-Path $shardDirectory 'api-telemetry.jsonl'
            $results = Join-Path $shardDirectory 'TestResults'
            $stdout = Join-Path $shardDirectory 'stdout.log'
            $stderr = Join-Path $shardDirectory 'stderr.log'
            $shards += New-TestProcess -Worktree $Worktree -Filter $entry.filters -TelemetryPath $telemetry -ResultsPath $results -StdoutPath $stdout -StderrPath $stderr
            $shards[-1] | Add-Member -NotePropertyName expectedCases -NotePropertyValue $entry.expectedCases
            $shards[-1] | Add-Member -NotePropertyName expectedCaseNames -NotePropertyValue @($entry.caseNames | ForEach-Object { [string]$_ } | Sort-Object)
            $shards[-1] | Add-Member -NotePropertyName shard -NotePropertyValue $entry.shard
            $shards[-1] | Add-Member -NotePropertyName classNames -NotePropertyValue @($entry.classes)
        }
        Wait-TestProcesses $shards
    } catch {
        $launchError = $_.Exception.Message
        foreach ($shard in $shards) {
            try {
                if ($shard.job) {
                    $cleanup = Stop-JobContainedProcess -Launch $shard.job -Terminate -TimeoutMilliseconds 5000
                    if (-not $cleanup.exited) { $shard.cleanupFailure = "Job-contained process did not exit: $($cleanup.remainingIds -join ',')" }
                }
            } catch { $shard.cleanupFailure = $_.Exception.Message }
        }
    }

    $invalidReasons = [System.Collections.Generic.List[string]]::new()
    if ($launchError) { $invalidReasons.Add("process launch/wait failed: $launchError") }
    $errorPattern = '(?i)(SQLITE_BUSY|SQLITE_LOCKED|database is locked|MSB3027|UnauthorizedAccessException|file.*locked|testhost.*crash)'
    $shardReports = foreach ($shard in $shards) {
        $trx = Join-Path $shard.resultsPath 'shard.trx'
        $trxError = $null
        $trxEvidence = $null
        try { $trxEvidence = Get-TrxEvidence $trx; $counts = if ($trxEvidence) { $trxEvidence.counts } else { $null } } catch { $counts = $null; $trxError = $_.Exception.Message }
        $testNames = if ($trxEvidence) { @($trxEvidence.names | ForEach-Object { [string]$_ }) } else { @() }
        if ($trxEvidence) {
            $nameMismatch = Compare-NameMultiset $shard.expectedCaseNames $testNames
            if ($nameMismatch) { $invalidReasons.Add("shard $($shard.shard) TRX exact case identity mismatch: $nameMismatch") }
        }
        $telemetryHasRecords = (Test-Path -LiteralPath $shard.telemetryPath -PathType Leaf) -and
            (@(Get-Content -LiteralPath $shard.telemetryPath -ErrorAction SilentlyContinue | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0)
        $aggregateDirectory = Join-Path $shard.resultsPath 'api-telemetry'
        $aggregateError = $null
        try { $aggregate = Invoke-TelemetryAggregator -Worktree $Worktree -TelemetryPath $shard.telemetryPath -TrxPath $trx -OutputPath $aggregateDirectory } catch { $aggregate = [pscustomobject]@{ report = $null; malformed = $null; truncated = $null; output = ''; exitCode = 1; timedOut = $false; cleanup = $null }; $aggregateError = $_.Exception.Message }
        $signals = @(("$($shard.stdout)`n$($shard.stderr)") | Select-String -Pattern $errorPattern -AllMatches | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique)
        if ($shard.exitCode -ne 0) { $invalidReasons.Add("shard $($shard.shard) exit code $($shard.exitCode)") }
        if ($shard.timedOut) { $invalidReasons.Add("shard $($shard.shard) exceeded the $TimeoutMinutes minute timeout") }
        if ($shard.waitError) { $invalidReasons.Add("shard $($shard.shard) wait error: $($shard.waitError)") }
        if ($shard.cleanupFailure) { $invalidReasons.Add("shard $($shard.shard) cleanup failure: $($shard.cleanupFailure)") }
        if (-not $shard.processTreeAvailable -or $shard.processTreeError) { $invalidReasons.Add("shard $($shard.shard) process-tree enumeration unavailable: $($shard.processTreeError)") }
        if ($shard.successfulSamples -eq 0) { $invalidReasons.Add("shard $($shard.shard) had no successful active process sample") }
        if ([double]$shard.wallMs -le 0) { $invalidReasons.Add("shard $($shard.shard) had non-positive wall time") }
        if ($null -eq $counts) { $invalidReasons.Add("shard $($shard.shard) TRX missing") }
        if ($trxError) { $invalidReasons.Add("shard $($shard.shard) TRX parse failed: $trxError") }
        elseif ($counts.total -ne $shard.expectedCases) { $invalidReasons.Add("shard $($shard.shard) expected $($shard.expectedCases) cases but TRX has $($counts.total)") }
        if ($counts -and ($counts.failed -gt 0 -or $counts.skipped -gt 0 -or $counts.other -gt 0)) { $invalidReasons.Add("shard $($shard.shard) has failed/skipped/other outcomes") }
        if (-not $telemetryHasRecords) { $invalidReasons.Add("shard $($shard.shard) telemetry JSONL was missing or empty") }
        if ($null -eq $aggregate.report) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregation missing") }
        elseif ($aggregate.report.totals.trxTests -ne $shard.expectedCases -or $aggregate.report.totals.tests -ne $shard.expectedCases) { $invalidReasons.Add("shard $($shard.shard) telemetry/TRX count mismatch") }
        if ($aggregate.report -and $aggregate.report.totals.factories -le 0) { $invalidReasons.Add("shard $($shard.shard) telemetry reported zero factories") }
        if ($aggregate.exitCode -ne 0) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregator exit code $($aggregate.exitCode)") }
        if ($aggregateError) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregation failed: $aggregateError") }
        if ($aggregate.timedOut) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregator timed out") }
        if ($aggregate.cleanup -and -not $aggregate.cleanup.exited) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregator cleanup failure") }
        if ($aggregate.malformed -ne 0 -or $aggregate.truncated -eq $true) { $invalidReasons.Add("shard $($shard.shard) telemetry malformed or truncated") }
        if ($signals.Count -gt 0) { $invalidReasons.Add("shard $($shard.shard) lock/cleanup signal: $($signals -join ', ')") }
        [pscustomobject]@{
            shard = $shard.shard
            classes = $shard.classNames
            expectedCases = $shard.expectedCases
            expectedCaseNames = $shard.expectedCaseNames
            testNames = $testNames
            wallMs = [math]::Round($shard.wallMs, 3)
            exitCode = $shard.exitCode
            counts = $counts
            telemetry = if ($aggregate.report) { $aggregate.report.totals } else { $null }
            telemetryPath = $shard.telemetryPath
            trxPath = $trx
            stdoutPath = $shard.stdoutPath
            stderrPath = $shard.stderrPath
            malformedTelemetry = $aggregate.malformed
            telemetryTruncated = $aggregate.truncated
            telemetryHasRecords = $telemetryHasRecords
            cpuMs = [math]::Round($shard.maxCpuMs, 3)
            cpuAvailable = $shard.cpuAvailable
            cpuError = $shard.cpuError
            processTreeAvailable = $shard.processTreeAvailable
            processTreeError = $shard.processTreeError
            rootIdentity = $shard.rootIdentity
            diskReadBytes = [math]::Round($shard.diskReadBytes, 3)
            diskWriteBytes = [math]::Round($shard.diskWriteBytes, 3)
            ioAvailable = $shard.ioAvailable
            ioError = $shard.ioError
            samples = $shard.samples
            successfulSamples = $shard.successfulSamples
            ownedProcessIds = $shard.ownedProcessIds
            cleanupFailure = $shard.cleanupFailure
            errorSignals = $signals
        }
    }
    if ($shards.Count -ne @($Partition.shards).Count) { $invalidReasons.Add("expected $(@($Partition.shards).Count) shards but launched $($shards.Count)") }
    if (@($shardReports | Where-Object { -not $_.ioAvailable }).Count -gt 0) { $invalidReasons.Add('disk performance counters were unavailable') }
    if (@($shardReports | Where-Object { -not $_.cpuAvailable }).Count -gt 0) { $invalidReasons.Add('process-tree CPU samples were unavailable') }
    $metrics = [ordered]@{
        worstShardWallMs = [math]::Round((@($shardReports | ForEach-Object wallMs | Measure-Object -Maximum).Maximum), 3)
        summedShardWallMs = [math]::Round((@($shardReports | ForEach-Object wallMs | Measure-Object -Sum).Sum), 3)
        cpuMs = [math]::Round((@($shardReports | ForEach-Object cpuMs | Measure-Object -Sum).Sum), 3)
        diskReadBytes = [math]::Round((@($shardReports | ForEach-Object diskReadBytes | Measure-Object -Sum).Sum), 3)
        diskWriteBytes = [math]::Round((@($shardReports | ForEach-Object diskWriteBytes | Measure-Object -Sum).Sum), 3)
        factories = [int](@($shardReports | ForEach-Object { $_.telemetry.factories } | Measure-Object -Sum).Sum)
        startupMs = [math]::Round((@($shardReports | ForEach-Object { $_.telemetry.summedFactoryStartupMs } | Measure-Object -Sum).Sum), 3)
        testCount = [int](@($shardReports | ForEach-Object { $_.counts.total } | Measure-Object -Sum).Sum)
    }
    if ($metrics.worstShardWallMs -le 0 -or $metrics.summedShardWallMs -le 0) { $invalidReasons.Add('observation wall-time metrics were non-positive') }
    $startedValues = @($shards | ForEach-Object shardStartedAt | Sort-Object)
    $endedValues = @($shards | ForEach-Object endedAt | Sort-Object -Descending)
    $observation = [ordered]@{
        schemaVersion = 'aerolink-api-host-reuse-measurement/v1'
        condition = $Condition
        run = $RunNumber
        seed = $Seed
        order = @($Order)
        warmup = [bool]$IsWarmup
        worktree = (Get-RepoInfo $Worktree)
        finalWorktree = (Get-RepoInfo $Worktree)
        conditionMetadata = $ConditionMetadata
        environment = $Environment
        partition = $Partition
        startedAt = if ($startedValues.Count -gt 0) { $startedValues[0].ToString('o') } else { $null }
        endedAt = if ($endedValues.Count -gt 0) { $endedValues[0].ToString('o') } else { $null }
        valid = ($invalidReasons.Count -eq 0)
        invalidReasons = @($invalidReasons)
        metricsComplete = (@($shardReports | Where-Object { -not $_.ioAvailable -or -not $_.cpuAvailable -or $_.successfulSamples -le 0 }).Count -eq 0)
        metrics = $metrics
        shards = @($shardReports)
    }
    Write-JsonFile (Join-Path $runDirectory 'observation.json') $observation
    [pscustomobject]$observation
}

function Get-Quantiles([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return [ordered]@{ p10 = $null; median = $null; p75 = $null; p95 = $null } }
    $at = {
        param([double]$Percentile)
        $position = ($sorted.Count - 1) * ($Percentile / 100.0)
        $lower = [math]::Floor($position)
        $upper = [math]::Ceiling($position)
        if ($lower -eq $upper) { return [double]$sorted[$lower] }
        [double]$sorted[$lower] + (($sorted[$upper] - $sorted[$lower]) * ($position - $lower))
    }
    [ordered]@{ p10 = & $at 10; median = & $at 50; p75 = & $at 75; p95 = & $at 95 }
}

function Get-ConditionSummary([string]$Condition, [object[]]$Observations, [int]$RequiredRuns, [object]$Manifest, [object]$ConditionMetadata) {
    $valid = @($Observations | Where-Object valid)
    $metrics = @('worstShardWallMs', 'summedShardWallMs', 'cpuMs', 'diskReadBytes', 'diskWriteBytes', 'factories', 'startupMs')
    $quantiles = [ordered]@{}
    foreach ($metric in $metrics) {
        $quantiles[$metric] = Get-Quantiles @($valid | ForEach-Object { [double]$_.metrics.$metric })
    }
    [ordered]@{
        schemaVersion = 'aerolink-api-host-reuse-measurement/v1'
        condition = $Condition
        requiredRuns = $RequiredRuns
        manifest = Get-ManifestFacts $Manifest
        conditionMetadata = $ConditionMetadata
        observationCount = @($Observations).Count
        validObservationCount = $valid.Count
        allValid = (@($Observations).Count -eq $RequiredRuns -and $valid.Count -eq $RequiredRuns)
        metricsComplete = (@($Observations | Where-Object { -not $_.metricsComplete }).Count -eq 0)
        quantiles = $quantiles
        observations = @($Observations)
    }
}

function Test-NonNegativeNumber([object]$Value) {
    $number = 0.0
    if ($null -eq $Value -or -not [double]::TryParse(([string]$Value), [ref]$number)) { return $false }
    -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number) -and $number -ge 0
}

function Get-ValidatedSummary([object]$Summary, [string]$ExpectedCondition) {
    $errors = [System.Collections.Generic.List[string]]::new()
    $schema = [string]$Summary.schemaVersion
    if ($schema -ne 'aerolink-api-host-reuse-measurement/v1') { $errors.Add("$ExpectedCondition summary has unsupported schemaVersion '$schema'.") }
    if ([string]$Summary.condition -ne $ExpectedCondition) { $errors.Add("Summary condition is '$($Summary.condition)', expected '$ExpectedCondition'.") }
    $summaryManifest = $Summary.manifest
    $summaryMetadata = $Summary.conditionMetadata
    if ($null -eq $summaryManifest -or [string]::IsNullOrWhiteSpace([string]$summaryManifest.manifestHash)) { $errors.Add("$ExpectedCondition summary is missing its canonical manifest facts.") }
    $caseNames = @($summaryManifest.caseNames | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [string]$_ } | Sort-Object)
    if ($caseNames.Count -eq 0) {
        $errors.Add("$ExpectedCondition summary is missing its full sorted case-name manifest.")
    } else {
        $manifestHashInput = (($caseNames -join "`n") + "`n")
        $manifestHashProvider = [System.Security.Cryptography.SHA256]::Create()
        try { $recomputedManifestHash = ([System.BitConverter]::ToString($manifestHashProvider.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($manifestHashInput)))).Replace('-', '').ToLowerInvariant() } finally { $manifestHashProvider.Dispose() }
        if ($recomputedManifestHash -ne [string]$summaryManifest.manifestHash) { $errors.Add("$ExpectedCondition summary manifestHash does not reconcile with its full case-name manifest.") }
        if ($summaryManifest.caseCount -and [int]$summaryManifest.caseCount -ne $caseNames.Count) { $errors.Add("$ExpectedCondition summary caseCount does not reconcile with its full case-name manifest.") }
    }
    if ($null -eq $summaryMetadata -or [string]::IsNullOrWhiteSpace([string]$summaryMetadata.head) -or [string]::IsNullOrWhiteSpace([string]$summaryMetadata.path) -or [string]::IsNullOrWhiteSpace([string]$summaryMetadata.environmentFingerprint) -or -not [bool]$summaryMetadata.cleanAtStart) { $errors.Add("$ExpectedCondition summary is missing condition identity metadata.") }
    if ($summaryMetadata -and [string]$summaryMetadata.condition -ne $ExpectedCondition) { $errors.Add("$ExpectedCondition summary condition metadata is inconsistent.") }
    $required = 0
    if (-not [int]::TryParse(([string]$Summary.requiredRuns), [ref]$required) -or $required -lt 1) { $errors.Add("$ExpectedCondition summary has an invalid requiredRuns value.") }
    elseif ($required -ne 10) { $errors.Add("$ExpectedCondition summary must contain exactly 10 required runs.") }
    $observations = @($Summary.observations)
    if ($observations.Count -ne $required) { $errors.Add("$ExpectedCondition summary has $($observations.Count) observations; expected $required.") }
    $seenSeeds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $normalized = [System.Collections.Generic.List[object]]::new()
    foreach ($observation in $observations) {
        $seedText = [string]$observation.seed
        foreach ($property in @('valid', 'metricsComplete', 'invalidReasons', 'metrics', 'shards')) {
            if (-not ($observation.PSObject.Properties.Name -contains $property)) { $errors.Add("Observation seed '$seedText' is missing '$property'.") }
        }
        if (-not $seenSeeds.Add($seedText)) { $errors.Add("$ExpectedCondition summary repeats seed '$seedText'.") }
        if ([string]$observation.condition -ne $ExpectedCondition) { $errors.Add("Observation seed '$seedText' has the wrong condition.") }
        $claimedValid = [bool]$observation.valid
        $claimedMetricsComplete = [bool]$observation.metricsComplete
        $evidenceValid = $true
        $evidenceMetricsComplete = $true
        $shards = @($observation.shards)
        $observationMetadata = $observation.conditionMetadata
        $finalWorktree = $observation.finalWorktree
        if ($null -eq $observationMetadata -or [string]$observationMetadata.head -ne [string]$summaryMetadata.head -or
            [string]$observationMetadata.path -ne [string]$summaryMetadata.path -or
            [string]$observationMetadata.environmentFingerprint -ne [string]$summaryMetadata.environmentFingerprint -or
            [string]$observationMetadata.manifest.manifestHash -ne [string]$summaryManifest.manifestHash) {
            $errors.Add("Observation seed '$seedText' condition metadata does not match its summary.")
            $evidenceValid = $false
        }
        if ($null -eq $finalWorktree -or [string]$finalWorktree.path -ne [string]$summaryMetadata.path -or
            [string]$finalWorktree.head -ne [string]$summaryMetadata.head -or -not [bool]$finalWorktree.clean) {
            $errors.Add("Observation seed '$seedText' final worktree state is not the expected clean SHA/path.")
            $evidenceValid = $false
        }
        if ($null -eq $observation.partition) { $errors.Add("Observation seed '$seedText' is missing its saved partition."); $evidenceValid = $false }
        elseif ([string]$observation.partition.seed -ne $seedText) { $errors.Add("Observation seed '$seedText' partition seed does not match."); $evidenceValid = $false }
        $wallValues = [System.Collections.Generic.List[double]]::new()
        $cpuValues = [System.Collections.Generic.List[double]]::new()
        $readValues = [System.Collections.Generic.List[double]]::new()
        $writeValues = [System.Collections.Generic.List[double]]::new()
        $factoryValues = [System.Collections.Generic.List[double]]::new()
        $startupValues = [System.Collections.Generic.List[double]]::new()
        $testValues = [System.Collections.Generic.List[double]]::new()
        if ($shards.Count -eq 0) { $evidenceValid = $false; $evidenceMetricsComplete = $false }
        foreach ($shard in $shards) {
            if ([int]$shard.exitCode -ne 0 -or [int]$shard.expectedCases -le 0) { $evidenceValid = $false }
            if ($null -eq $shard.counts -or [int]$shard.counts.total -ne [int]$shard.expectedCases -or
                [int]$shard.counts.failed -ne 0 -or [int]$shard.counts.skipped -ne 0 -or [int]$shard.counts.other -ne 0) { $evidenceValid = $false }
            $expectedNames = @($shard.expectedCaseNames | ForEach-Object { [string]$_ } | Sort-Object)
            $actualNames = @($shard.testNames | ForEach-Object { [string]$_ })
            $nameMismatch = Compare-NameMultiset $expectedNames $actualNames
            if ($expectedNames.Count -ne [int]$shard.expectedCases -or $actualNames.Count -ne [int]$shard.expectedCases -or $nameMismatch) {
                $evidenceValid = $false
                $errors.Add("Observation seed '$seedText' has TRX case identity mismatch: $nameMismatch")
            }
            if (-not [bool]$shard.telemetryHasRecords -or [int]$shard.malformedTelemetry -ne 0 -or [bool]$shard.telemetryTruncated) { $evidenceValid = $false }
            if ($null -eq $shard.telemetry -or [int]$shard.telemetry.tests -ne [int]$shard.expectedCases -or [int]$shard.telemetry.factories -le 0) { $evidenceValid = $false }
            if (-not [bool]$shard.cpuAvailable -or -not [bool]$shard.ioAvailable -or [int]$shard.successfulSamples -le 0 -or -not [bool]$shard.processTreeAvailable -or $shard.processTreeError) { $evidenceMetricsComplete = $false }
            if ($shard.cleanupFailure -or $shard.waitError -or @($shard.errorSignals).Count -gt 0) { $evidenceValid = $false }
            if (-not (Test-NonNegativeNumber $shard.wallMs) -or [double]$shard.wallMs -le 0) { $evidenceValid = $false; $errors.Add("Observation seed '$seedText' has non-positive shard wall time.") }
            foreach ($pair in @(
                @($wallValues, $shard.wallMs),
                @($cpuValues, $shard.cpuMs),
                @($readValues, $shard.diskReadBytes),
                @($writeValues, $shard.diskWriteBytes),
                @($factoryValues, $shard.telemetry.factories),
                @($startupValues, $shard.telemetry.summedFactoryStartupMs),
                @($testValues, $shard.counts.total))) {
                if (Test-NonNegativeNumber $pair[1]) { $pair[0].Add([double]$pair[1]) } else { $evidenceValid = $false }
            }
        }
        $invalidReasons = @($observation.invalidReasons)
        $actualValid = $evidenceValid -and $invalidReasons.Count -eq 0
        if ($claimedValid -ne $actualValid) { $errors.Add("Observation seed '$seedText' valid flag does not match its evidence.") }
        if ($claimedMetricsComplete -ne $evidenceMetricsComplete) { $errors.Add("Observation seed '$seedText' metricsComplete flag does not match its evidence.") }
        foreach ($metric in @('worstShardWallMs', 'summedShardWallMs', 'cpuMs', 'diskReadBytes', 'diskWriteBytes', 'factories', 'startupMs')) {
            if (-not (Test-NonNegativeNumber $observation.metrics.$metric)) { $errors.Add("Observation seed '$seedText' has invalid metric '$metric'.") }
        }
        $derivedMetrics = @{
            worstShardWallMs = if ($wallValues.Count) { ($wallValues | Measure-Object -Maximum).Maximum } else { 0 }
            summedShardWallMs = ($wallValues | Measure-Object -Sum).Sum
            cpuMs = ($cpuValues | Measure-Object -Sum).Sum
            diskReadBytes = ($readValues | Measure-Object -Sum).Sum
            diskWriteBytes = ($writeValues | Measure-Object -Sum).Sum
            factories = ($factoryValues | Measure-Object -Sum).Sum
            startupMs = ($startupValues | Measure-Object -Sum).Sum
            testCount = ($testValues | Measure-Object -Sum).Sum
        }
        foreach ($metric in $derivedMetrics.Keys) {
            if (-not (Test-NonNegativeNumber $observation.metrics.$metric) -or [math]::Abs([double]$observation.metrics.$metric - [double]$derivedMetrics[$metric]) -gt 0.01) {
                $errors.Add("Observation seed '$seedText' metric '$metric' does not reconcile with shard evidence.")
            }
        }
        if ([double]$derivedMetrics.worstShardWallMs -le 0 -or [double]$derivedMetrics.summedShardWallMs -le 0) { $evidenceValid = $false; $errors.Add("Observation seed '$seedText' has non-positive aggregate wall time.") }
        $normalized.Add([pscustomobject]@{
            seed = $seedText
            valid = $actualValid
            metricsComplete = $evidenceMetricsComplete
            metrics = $observation.metrics
            partition = $observation.partition
        })
    }
    $actualValidCount = @($normalized | Where-Object valid).Count
    $actualMetricsComplete = (@($normalized | Where-Object { -not $_.metricsComplete }).Count -eq 0 -and $normalized.Count -eq $required)
    if ([int]$Summary.observationCount -ne $observations.Count) { $errors.Add("$ExpectedCondition summary observationCount does not match its observations.") }
    if ([int]$Summary.validObservationCount -ne $actualValidCount) { $errors.Add("$ExpectedCondition summary validObservationCount does not match recomputed evidence.") }
    if ([bool]$Summary.allValid -ne ($normalized.Count -eq $required -and $actualValidCount -eq $required)) { $errors.Add("$ExpectedCondition summary allValid flag does not match recomputed evidence.") }
    if ([bool]$Summary.metricsComplete -ne $actualMetricsComplete) { $errors.Add("$ExpectedCondition summary metricsComplete flag does not match recomputed evidence.") }
    [pscustomobject]@{
        valid = ($errors.Count -eq 0)
        errors = @($errors)
        condition = $ExpectedCondition
        manifest = $summaryManifest
        conditionMetadata = $summaryMetadata
        requiredRuns = $required
        observations = @($normalized)
        validObservationCount = $actualValidCount
        allValid = ($normalized.Count -eq $required -and $actualValidCount -eq $required)
        metricsComplete = $actualMetricsComplete
    }
}

function Get-Decision([object]$Baseline, [object]$Treatment) {
    $base = Get-ValidatedSummary $Baseline 'baseline'
    $treat = Get-ValidatedSummary $Treatment 'treatment'
    $validationErrors = @($base.errors + $treat.errors)
    if ([string]$base.manifest.manifestHash -ne [string]$treat.manifest.manifestHash) { $validationErrors += 'Baseline and treatment canonical manifest hashes differ.' }
    if ([int]$base.manifest.caseCount -ne [int]$treat.manifest.caseCount -or [int]$base.manifest.classCount -ne [int]$treat.manifest.classCount) { $validationErrors += 'Baseline and treatment manifest counts differ.' }
    if ([string]$base.conditionMetadata.head -eq [string]$treat.conditionMetadata.head) { $validationErrors += 'Baseline and treatment SHAs must be distinct.' }
    if ([string]$base.conditionMetadata.path -eq [string]$treat.conditionMetadata.path) { $validationErrors += 'Baseline and treatment paths must be distinct.' }
    if ([string]$base.conditionMetadata.environmentFingerprint -ne [string]$treat.conditionMetadata.environmentFingerprint) { $validationErrors += 'Baseline and treatment environment fingerprints differ.' }
    $basePartitions = @{}
    foreach ($observation in $base.observations) { $basePartitions[[string]$observation.seed] = $observation.partition | ConvertTo-Json -Depth 20 -Compress }
    $treatmentPartitions = @{}
    foreach ($observation in $treat.observations) { $treatmentPartitions[[string]$observation.seed] = $observation.partition | ConvertTo-Json -Depth 20 -Compress }
    if (Compare-Object @($basePartitions.Keys | Sort-Object) @($treatmentPartitions.Keys | Sort-Object)) { $validationErrors += 'Baseline and treatment partition seed sets differ.' }
    foreach ($seed in @($basePartitions.Keys)) {
        if ($treatmentPartitions.ContainsKey($seed) -and $basePartitions[$seed] -ne $treatmentPartitions[$seed]) { $validationErrors += "Partition differs for seed '$seed'." }
    }
    $baseValid = @($base.observations | Where-Object valid)
    $treatmentValid = @($treat.observations | Where-Object valid)
    $baseValues = @($baseValid | ForEach-Object { [double]$_.metrics.worstShardWallMs })
    $treatmentValues = @($treatmentValid | ForEach-Object { [double]$_.metrics.worstShardWallMs })
    $baseMedian = (Get-Quantiles $baseValues).median
    $treatmentMedian = (Get-Quantiles $treatmentValues).median
    $improvement = if ($baseMedian -gt 0) { 1 - ($treatmentMedian / $baseMedian) } else { $null }
    $paired = @()
    $baseBySeed = @{}
    foreach ($observation in $baseValid) { $baseBySeed[$observation.seed] = $observation }
    $treatmentBySeed = @{}
    foreach ($observation in $treatmentValid) { $treatmentBySeed[$observation.seed] = $observation }
    $baseSeeds = @($baseBySeed.Keys | Sort-Object)
    $treatmentSeeds = @($treatmentBySeed.Keys | Sort-Object)
    if (Compare-Object $baseSeeds $treatmentSeeds) { $validationErrors += 'Baseline and treatment valid observations do not have the same seed set.' }
    foreach ($seed in $baseSeeds) {
        if (-not $treatmentBySeed.ContainsKey($seed)) { continue }
        $baseWall = [double]$baseBySeed[$seed].metrics.worstShardWallMs
        $treatmentWall = [double]$treatmentBySeed[$seed].metrics.worstShardWallMs
        if ($baseWall -le 0) { $validationErrors += "Baseline seed '$seed' has a non-positive worst-shard wall time."; continue }
        if ($treatmentWall -le 0) { $validationErrors += "Treatment seed '$seed' has a non-positive worst-shard wall time."; continue }
        $paired += 1 - ($treatmentWall / $baseWall)
    }
    foreach ($value in $baseValues) { if ($value -le 0) { $validationErrors += 'A baseline worst-shard wall time is non-positive.' } }
    foreach ($value in $treatmentValues) { if ($value -le 0) { $validationErrors += 'A treatment worst-shard wall time is non-positive.' } }
    $pairedMedian = if ($paired.Count -gt 0) { (Get-Quantiles $paired).median } else { $null }
    $complete = $base.allValid -and $treat.allValid -and $base.metricsComplete -and $treat.metricsComplete -and $validationErrors.Count -eq 0
    $pass = $complete -and $improvement -ge 0.15 -and $pairedMedian -ge 0.15
    [ordered]@{
        rule = 'Both conditions require the configured number of valid, fully instrumented observations. The treatment median worst-shard wall must be at least 15% lower, and the paired-seed median must also be at least 15% lower.'
        baselineMedianWorstShardWallMs = $baseMedian
        treatmentMedianWorstShardWallMs = $treatmentMedian
        aggregateImprovement = $improvement
        pairedImprovement = Get-Quantiles $paired
        completeEvidence = $complete
        validationErrors = @($validationErrors | Sort-Object -Unique)
        status = if (-not $complete) { 'inconclusive' } elseif ($pass) { 'pass' } else { 'fail' }
    }
}

function Get-CanonicalPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($full)
    if ([string]::IsNullOrEmpty($root)) { Fail "Could not determine a filesystem root for path: $Path" }
    $components = @($full.Substring($root.Length) -split '[\\/]+' | Where-Object { $_ -ne '' })
    $current = $root
    $visited = @{}
    for ($depth = 0; $depth -lt 64; $depth++) {
        $restarted = $false
        for ($index = 0; $index -lt $components.Count; $index++) {
            $candidate = Join-Path $current $components[$index]
            if (-not (Test-Path -LiteralPath $candidate)) {
                $tail = @($components[$index..($components.Count - 1)]) -join [System.IO.Path]::DirectorySeparatorChar
                $result = Join-Path $current $tail
                return $result.TrimEnd('\', '/')
            }
            $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
            $linkType = [string]$item.LinkType
            $isReparse = ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            if (-not $isReparse -and [string]::IsNullOrEmpty($linkType)) {
                $current = [string]$item.FullName
                continue
            }
            $targetValues = @($item.Target | Where-Object { -not [string]::IsNullOrEmpty([string]$_) })
            $target = if ($targetValues.Count -gt 0) { [string]$targetValues[0] } else { [string](Resolve-Path -LiteralPath $candidate -ErrorAction Stop).Path }
            if (-not [System.IO.Path]::IsPathRooted($target)) { $target = Join-Path (Split-Path -Parent $candidate) $target }
            $target = [System.IO.Path]::GetFullPath($target)
            $remainingComponents = if (($index + 1) -lt $components.Count) { @($components[($index + 1)..($components.Count - 1)]) } else { @() }
            $remaining = $remainingComponents -join '|'
            $visitKey = ("{0}|{1}" -f $target.ToLowerInvariant(), $remaining.ToLowerInvariant())
            if ($visited.ContainsKey($visitKey)) { Fail "Reparse-point resolution loop detected at: $candidate" }
            $visited[$visitKey] = $true
            $current = $target
            $components = $remainingComponents
            $restarted = $true
            break
        }
        if (-not $restarted) {
            if ($current -match '^[A-Za-z]:\\$' -or $current -match '^\\\\[^\\]+\\[^\\]+\\$') { return $current }
            return $current.TrimEnd('\', '/')
        }
    }
    Fail "Reparse-point resolution exceeded the component/depth bound for: $Path"
}

function Assert-OutputOutsideWorktrees([string]$OutputPath, [object[]]$Worktrees) {
    $fullOutput = Get-CanonicalPath $OutputPath
    foreach ($worktree in $Worktrees) {
        $fullWorktree = Get-CanonicalPath ([string]$worktree.path)
        $prefix = $fullWorktree + [System.IO.Path]::DirectorySeparatorChar
        if ($fullOutput.Equals($fullWorktree, [StringComparison]::OrdinalIgnoreCase) -or
            $fullOutput.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            Fail "Output must be outside the condition worktrees: $OutputPath"
        }
    }
}

function Assert-EmptyOutput([string]$Path, [string]$Mode = 'Run') {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { Fail "Run output path is a file: $Path" }
    if (Test-Path -LiteralPath $Path -PathType Container) {
        $items = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop)
        if ($items.Count -gt 0) { Fail "Refusing to reuse non-empty $Mode output directory: $Path" }
    }
}

function Assert-WorktreeStable([object]$Expected) {
    $current = Get-RepoInfo $Expected.path
    if (-not $current.clean) { Fail "Worktree became dirty: $($Expected.path)" }
    if (-not $current.head.Equals($Expected.head, [StringComparison]::OrdinalIgnoreCase)) {
        Fail "Worktree HEAD changed from $($Expected.head) to $($current.head): $($Expected.path)"
    }
}

function Assert-ComparableEnvironment([object]$Baseline, [object]$Treatment) {
    foreach ($property in @('dotnet', 'node', 'powershell', 'processorCount', 'machine', 'os', 'cpu')) {
        $left = $Baseline.$property | ConvertTo-Json -Depth 10 -Compress
        $right = $Treatment.$property | ConvertTo-Json -Depth 10 -Compress
        if ($left -ne $right) { Fail "Baseline/treatment environment differs for '$property'." }
    }
}

function New-Plan([string]$Root, [object]$BaselineInfo, [object]$TreatmentInfo, [object]$Manifest, [object[]]$Partitions, [string]$PlanMode = 'Plan') {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    $observations = @($Partitions | ForEach-Object {
        $index = [array]::IndexOf($Partitions, $_)
        [ordered]@{
            run = $index + 1
            seed = $_.seed
            order = if (($index % 2) -eq 0) { @('baseline', 'treatment') } else { @('treatment', 'baseline') }
            partition = $_
        }
    })
    $plan = [ordered]@{
        schemaVersion = 'aerolink-api-host-reuse-measurement/v1'
        mode = $PlanMode
        planOnly = ($PlanMode -eq 'Plan')
        runs = $Runs
        shardCount = $ShardCount
        warmup = [bool]$Warmup
        baseline = $BaselineInfo
        treatment = $TreatmentInfo
        baselineManifest = $Manifest.baseline
        treatmentManifest = $Manifest.treatment
        baselineConditionMetadata = $Manifest.baselineMetadata
        treatmentConditionMetadata = $Manifest.treatmentMetadata
        observations = $observations
        decisionRule = 'Require ten valid observations per condition, no test/telemetry/lock failures, and at least 15% reduction in median worst-shard wall clock plus paired-seed median improvement.'
        execution = if ($PlanMode -eq 'Plan') { 'This plan does not build, launch test shards, touch PostgreSQL, or change either worktree.' } else { 'Saved Run plan. The harness will restore/build sequentially, then launch only the planned shards; baseline and treatment never run concurrently.' }
    }
    Write-JsonFile (Join-Path $Root 'plan.json') $plan
    $planMarkdownExecution = if ($PlanMode -eq 'Plan') { '- No tests, builds, database connections, or GitHub operations were started in plan mode.' } else { '- This is the saved execution plan for Run mode; restore/build and shard execution occur after this file is written.' }
    @(
        '# API host-reuse measurement plan',
        '',
        "- Conditions: $($BaselineInfo.path) and $($TreatmentInfo.path)",
        "- Runs: $Runs; shards per observation: $ShardCount; warmup: $Warmup",
        "- Cases: $($Manifest.baseline.caseCount); classes: $($Manifest.baseline.classCount)",
        '- The plan is deterministic for each seed and reuses the same partition for baseline and treatment.',
        $planMarkdownExecution,
        '',
        'Run mode requires two clean, already prepared worktrees and writes only beneath the selected output directory.'
    ) | Set-Content -LiteralPath (Join-Path $Root 'plan.md') -Encoding utf8
    $plan
}

function Main {
    if ($JobSmoke) {
        if (-not $IsWindows) { Fail 'This harness is intentionally Windows-only.' }
        Invoke-JobContainmentSmoke
        return
    }
    if (-not $OutputRoot) { $OutputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'aerolink-api-host-reuse-measurement' }
    if ($ShardCount -lt 1) { Fail 'ShardCount must be positive.' }
    if ($TimeoutMinutes -lt 1) { Fail 'TimeoutMinutes must be positive.' }
    if ($ProcessTimeoutMinutes -lt 1) { Fail 'ProcessTimeoutMinutes must be positive.' }
    if ($MaxProcessTreeCount -lt 1) { Fail 'MaxProcessTreeCount must be positive.' }
    if ($Runs -lt 1) { Fail 'Runs must be positive.' }
    if ($Seeds) {
        $seedValues = @($Seeds -split ',' | ForEach-Object {
            $parsed = 0
            if (-not [int]::TryParse($_.Trim(), [ref]$parsed)) { Fail "Seed '$($_.Trim())' is not an integer." }
            $parsed
        })
    } else {
        $seedValues = @(563000..(563000 + $Runs - 1))
    }
    if ($seedValues.Count -lt $Runs) { Fail "Provide at least $Runs deterministic seeds." }
    $Seeds = $seedValues
    if (-not $IsWindows) { Fail 'This harness is intentionally Windows-only.' }
    if ($Mode -eq 'Evaluate') {
        if (-not $BaselineSummaryPath -or -not $TreatmentSummaryPath) { Fail 'Evaluate mode requires BaselineSummaryPath and TreatmentSummaryPath.' }
        $baseline = Get-Content -Raw -LiteralPath $BaselineSummaryPath | ConvertFrom-Json
        $treatment = Get-Content -Raw -LiteralPath $TreatmentSummaryPath | ConvertFrom-Json
        $baselineRecordedPath = [string]$baseline.conditionMetadata.path
        $treatmentRecordedPath = [string]$treatment.conditionMetadata.path
        if ([string]::IsNullOrWhiteSpace($baselineRecordedPath) -or [string]::IsNullOrWhiteSpace($treatmentRecordedPath) -or
            -not (Test-Path -LiteralPath $baselineRecordedPath -PathType Container) -or
            -not (Test-Path -LiteralPath $treatmentRecordedPath -PathType Container)) {
            Fail 'Evaluate requires both recorded condition worktree paths to exist before writing output.'
        }
        $liveBaselineInfo = Get-RepoInfo $baselineRecordedPath
        $liveTreatmentInfo = Get-RepoInfo $treatmentRecordedPath
        Assert-OutputOutsideWorktrees $OutputRoot @($liveBaselineInfo, $liveTreatmentInfo)
        Assert-EmptyOutput $OutputRoot 'Evaluate'
        foreach ($pair in @(@($baseline, $liveBaselineInfo, 'baseline'), @($treatment, $liveTreatmentInfo, 'treatment'))) {
            $summary = $pair[0]
            $liveInfo = $pair[1]
            $condition = $pair[2]
            if (-not $liveInfo.clean) { Fail "Evaluate $condition worktree is dirty; no decision output was written." }
            if ([string]$liveInfo.head -ne [string]$summary.conditionMetadata.head) { Fail "Evaluate $condition worktree HEAD no longer matches the recorded SHA." }
            if (-not $liveInfo.path.Equals([string]$summary.conditionMetadata.path, [StringComparison]::OrdinalIgnoreCase)) { Fail "Evaluate $condition worktree path no longer matches the recorded canonical path." }
            $liveManifest = Get-TestManifest $liveInfo.path $null
            Assert-SummaryManifestMatchesLive $summary.manifest $liveManifest $condition
            Assert-SummaryManifestMatchesLive $summary.conditionMetadata.manifest $liveManifest "$condition condition metadata"
            Assert-SummaryPartitionsMatchLive $summary $liveManifest $condition
            $liveEnvironment = Get-EnvironmentInfo $liveInfo.path
            $liveFingerprint = Get-EnvironmentFingerprint $liveEnvironment
            if ([string]$liveFingerprint -ne [string]$summary.conditionMetadata.environmentFingerprint) { Fail "Evaluate $condition environment fingerprint no longer matches the recorded evidence." }
        }
        $decision = Get-Decision $baseline $treatment
        Write-JsonFile (Join-Path $OutputRoot 'decision.json') $decision
        $decision | ConvertTo-Json -Depth 20
        return
    }
    if (-not $BaselinePath) { $BaselinePath = (Get-Location).Path }
    if (-not $TreatmentPath) { $TreatmentPath = $BaselinePath }
    if ($Mode -eq 'Run' -and $Runs -ne 10) { Fail 'Run mode requires exactly 10 measured observations.' }
    if ($Mode -eq 'Run' -and $TestListPath) { Fail 'Run mode requires live discovery; TestListPath is allowed only for Plan contract smoke.' }
    if ($Mode -eq 'Run' -and $SkipBuild) { Fail 'Run mode always restores and builds each exact clean SHA before live test discovery; SkipBuild is not allowed.' }
    if (-not $IsWindows) { Fail 'This harness is intentionally Windows-only.' }

    $baselineInfo = Get-RepoInfo $BaselinePath
    $treatmentInfo = Get-RepoInfo $TreatmentPath
    if ($Mode -eq 'Run' -and $baselineInfo.path.Equals($treatmentInfo.path, [StringComparison]::OrdinalIgnoreCase)) {
        Fail 'Run mode requires distinct baseline and treatment worktrees.'
    }
    Assert-OutputOutsideWorktrees $OutputRoot @($baselineInfo, $treatmentInfo)
    if ($Mode -eq 'Run' -and $baselineInfo.head.Equals($treatmentInfo.head, [StringComparison]::OrdinalIgnoreCase)) {
        Fail 'Run mode rejects identical baseline and treatment SHAs; provide two distinct commits.'
    }
    if ($Mode -eq 'Run') {
        if (-not $baselineInfo.clean -or -not $treatmentInfo.clean) { Fail 'Run mode requires clean baseline and treatment worktrees before restore/build.' }
        Assert-EmptyOutput $OutputRoot 'Run'
        foreach ($condition in @(@('baseline', $BaselinePath), @('treatment', $TreatmentPath))) {
            if ($condition[0] -eq 'baseline') { Assert-WorktreeStable $baselineInfo } else { Assert-WorktreeStable $treatmentInfo }
            $restore = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('restore', (Join-Path $condition[1] $SolutionPath)) -WorkingDirectory $condition[1]
            if ($restore.ExitCode -ne 0) { Fail "$($condition[0]) restore failed.`n$($restore.Stdout)`n$($restore.Stderr)" }
            if ($condition[0] -eq 'baseline') { Assert-WorktreeStable $baselineInfo } else { Assert-WorktreeStable $treatmentInfo }
            $build = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('build', (Join-Path $condition[1] $SolutionPath), '--configuration', 'Release', '--no-restore') -WorkingDirectory $condition[1]
            if ($build.ExitCode -ne 0) { Fail "$($condition[0]) build failed.`n$($build.Stdout)`n$($build.Stderr)" }
            if ($condition[0] -eq 'baseline') { Assert-WorktreeStable $baselineInfo } else { Assert-WorktreeStable $treatmentInfo }
        }
    }
    $environment = [ordered]@{
        baseline = Get-EnvironmentInfo $BaselinePath
        treatment = Get-EnvironmentInfo $TreatmentPath
    }
    if ($Mode -eq 'Run') { Assert-ComparableEnvironment $environment.baseline $environment.treatment }
    $baselineList = if ($TestListPath) { $TestListPath } else { $null }
    $baselineManifest = Get-TestManifest $BaselinePath $baselineList
    $treatmentManifest = Get-TestManifest $TreatmentPath $baselineList
    Assert-SameManifest $baselineManifest $treatmentManifest
    $baselineMetadata = [ordered]@{
        condition = 'baseline'
        path = $baselineInfo.path
        head = $baselineInfo.head
        cleanAtStart = $baselineInfo.clean
        manifest = Get-ManifestFacts $baselineManifest
        environmentFingerprint = Get-EnvironmentFingerprint $environment.baseline
    }
    $treatmentMetadata = [ordered]@{
        condition = 'treatment'
        path = $treatmentInfo.path
        head = $treatmentInfo.head
        cleanAtStart = $treatmentInfo.clean
        manifest = Get-ManifestFacts $treatmentManifest
        environmentFingerprint = Get-EnvironmentFingerprint $environment.treatment
    }
    $partitions = @($Seeds[0..($Runs - 1)] | ForEach-Object { New-Partition $baselineManifest.classes $_ $ShardCount })
    if ($Mode -eq 'Run') {
        Assert-WorktreeStable $baselineInfo
        Assert-WorktreeStable $treatmentInfo
    }
    $manifest = [pscustomobject]@{ baseline = $baselineManifest; treatment = $treatmentManifest; baselineMetadata = $baselineMetadata; treatmentMetadata = $treatmentMetadata }
    if ($Mode -eq 'Plan') {
        Assert-EmptyOutput $OutputRoot 'Plan'
        $plan = New-Plan $OutputRoot $baselineInfo $treatmentInfo $manifest $partitions 'Plan'
        $plan | ConvertTo-Json -Depth 20
        return
    }

    $plan = New-Plan $OutputRoot $baselineInfo $treatmentInfo $manifest $partitions 'Run'
    Assert-WorktreeStable $baselineInfo
    Assert-WorktreeStable $treatmentInfo
    Assert-WorktreeStable $baselineInfo
    Assert-WorktreeStable $treatmentInfo
    $all = @{ baseline = [System.Collections.Generic.List[object]]::new(); treatment = [System.Collections.Generic.List[object]]::new() }
    if ($Warmup) {
        $warmupPartition = $partitions[0]
        Assert-WorktreeStable $baselineInfo
        Assert-WorktreeStable $treatmentInfo
        $warmupBaseline = Invoke-Observation -Condition baseline -Worktree $BaselinePath -Partition $warmupPartition -RunNumber 0 -Seed $warmupPartition.seed -Root $OutputRoot -Order @('baseline') -Environment $environment -ConditionMetadata $baselineMetadata -IsWarmup
        if (-not $warmupBaseline.valid) { Fail 'Baseline warmup was invalid; no measured observations were started.' }
        Assert-WorktreeStable $baselineInfo
        Assert-WorktreeStable $treatmentInfo
        $warmupTreatment = Invoke-Observation -Condition treatment -Worktree $TreatmentPath -Partition $warmupPartition -RunNumber 0 -Seed $warmupPartition.seed -Root $OutputRoot -Order @('treatment') -Environment $environment -ConditionMetadata $treatmentMetadata -IsWarmup
        if (-not $warmupTreatment.valid) { Fail 'Treatment warmup was invalid; no measured observations were started.' }
    }
    for ($index = 0; $index -lt $Runs; $index++) {
        $partition = $partitions[$index]
        $order = if (($index % 2) -eq 0) { @('baseline', 'treatment') } else { @('treatment', 'baseline') }
        foreach ($condition in $order) {
            Assert-WorktreeStable $baselineInfo
            Assert-WorktreeStable $treatmentInfo
            $path = if ($condition -eq 'baseline') { $BaselinePath } else { $TreatmentPath }
            $metadata = if ($condition -eq 'baseline') { $baselineMetadata } else { $treatmentMetadata }
            $observation = Invoke-Observation -Condition $condition -Worktree $path -Partition $partition -RunNumber ($index + 1) -Seed $partition.seed -Root $OutputRoot -Order $order -Environment $environment -ConditionMetadata $metadata
            $all[$condition].Add($observation)
            Assert-WorktreeStable $baselineInfo
            Assert-WorktreeStable $treatmentInfo
        }
    }
    Assert-WorktreeStable $baselineInfo
    Assert-WorktreeStable $treatmentInfo
    $baselineSummary = Get-ConditionSummary baseline @($all.baseline) $Runs $baselineManifest $baselineMetadata
    $treatmentSummary = Get-ConditionSummary treatment @($all.treatment) $Runs $treatmentManifest $treatmentMetadata
    $decision = Get-Decision $baselineSummary $treatmentSummary
    Write-JsonFile (Join-Path $OutputRoot 'baseline-summary.json') $baselineSummary
    Write-JsonFile (Join-Path $OutputRoot 'treatment-summary.json') $treatmentSummary
    Write-JsonFile (Join-Path $OutputRoot 'summary.json') ([ordered]@{ schemaVersion = 'aerolink-api-host-reuse-measurement/v1'; environment = $environment; finalBaseline = Get-RepoInfo $BaselinePath; finalTreatment = Get-RepoInfo $TreatmentPath; baseline = $baselineSummary; treatment = $treatmentSummary; decision = $decision })
    $decision | ConvertTo-Json -Depth 20
}

Main
