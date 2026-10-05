using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AeroLink.Infrastructure.Notifications;

/// <summary>Native SMTP process evidence. Linux wall-clock StartTime estimates are never identity tokens.</summary>
internal static class NotificationProcessIdentity
{
    private sealed record LinuxIdentity(int Version, int Pid, string Boot, string PidNamespace, string TimeNamespace, ulong Start);

    internal static string? CaptureCurrent()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.GetCurrentProcess();
                return "windows-v1:" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
            }
            if (!OperatingSystem.IsLinux()) return null;
            var context = ReadContext();
            return JsonSerializer.Serialize(new LinuxIdentity(1, Environment.ProcessId, context.Boot,
                context.PidNamespace, context.TimeNamespace, ReadStart(Environment.ProcessId)));
        }
        catch { return null; }
    }

    internal static bool IsLinuxInstanceGone(int pid, string identity)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<LinuxIdentity>(identity);
            if (saved is null || saved.Version != 1 || saved.Pid != pid || pid <= 0 || saved.Start == 0) return false;
            var context = ReadContext();
            // A different namespace/proc mount/boot is not evidence about this host's original socket owner.
            if (saved.Boot != context.Boot || saved.PidNamespace != context.PidNamespace || saved.TimeNamespace != context.TimeNamespace) return false;
            if (Kill(pid, 0) != 0)
                return Marshal.GetLastPInvokeError() == 3; // ESRCH only; EPERM/EACCES and other failures are unproven.
            return ReadStart(pid) != saved.Start;
        }
        catch { return false; } // Missing/unreadable proc files, legacy records and malformed evidence never prove exit.
    }

    private static (string Boot, string PidNamespace, string TimeNamespace) ReadContext()
    {
        var ownStat = File.ReadAllText("/proc/self/stat");
        if (ReadPid(ownStat) != Environment.ProcessId) throw new IOException("Proc PID namespace is not the current process namespace.");
        var namespacePids = File.ReadLines("/proc/self/status").Single(x => x.StartsWith("NSpid:", StringComparison.Ordinal))
            [6..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (namespacePids.Length != 1 || !int.TryParse(namespacePids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var visiblePid)
            || visiblePid != Environment.ProcessId)
            throw new IOException("Proc mount must represent the current PID namespace.");
        var boot = Guid.Parse(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()).ToString("D");
        var pidNamespace = ReadNamespace("pid");
        var timeNamespace = ReadNamespace("time");
        // A proc mount for an ancestor namespace must not turn another visible PID into our socket owner.
        var ownPidPath = "/proc/" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        if (new FileInfo(ownPidPath + "/ns/pid").LinkTarget != pidNamespace
            || new FileInfo(ownPidPath + "/ns/time").LinkTarget != timeNamespace
            || ReadStart(Environment.ProcessId) != ParseStart(ownStat, Environment.ProcessId))
            throw new IOException("Proc mount does not identify the current process instance.");
        return (boot, pidNamespace, timeNamespace);
    }

    private static string ReadNamespace(string kind)
    {
        var target = new FileInfo("/proc/self/ns/" + kind).LinkTarget;
        var prefix = kind + ":[";
        if (target is null || !target.StartsWith(prefix, StringComparison.Ordinal) || !target.EndsWith(']')
            || !ulong.TryParse(target.AsSpan(prefix.Length, target.Length - prefix.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var inode) || inode == 0)
            throw new IOException("Process namespace identity is unavailable.");
        return target;
    }

    private static int ReadPid(string stat)
    {
        var firstSpace = stat.IndexOf(' ');
        if (firstSpace <= 0) throw new IOException("Malformed process stat identity.");
        return int.Parse(stat.AsSpan(0, firstSpace), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static ulong ReadStart(int pid)
    {
        var stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
        return ParseStart(stat, pid);
    }

    private static ulong ParseStart(string stat, int pid)
    {
        var commStart = stat.IndexOf(" ("); var commEnd = stat.LastIndexOf(')');
        if (ReadPid(stat) != pid || commStart < 0 || commEnd <= commStart || commEnd + 1 >= stat.Length)
            throw new IOException("Malformed process stat identity.");
        // comm may contain whitespace and ')'; the fields after the final ')' begin at field 3.
        var fields = stat[(commEnd + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start == 0)
            throw new IOException("Process creation identity is unavailable.");
        return start;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
}
