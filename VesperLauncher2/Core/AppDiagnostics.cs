using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace VesperLauncher.Core;

public sealed record AppDiagnostics(
    string OsDescription,
    string RuntimeDescription,
    string ProcessArchitecture,
    string FrameworkDescription,
    long? TotalMemoryBytes,
    long WorkingSetBytes,
    bool? IsElevated,
    string BaseDirectory,
    string? ProcessPath)
{
    public static AppDiagnostics Capture()
    {
        using var process = Process.GetCurrentProcess();
        return new AppDiagnostics(
            RuntimeInformation.OSDescription,
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            TryGetTotalMemoryBytes(),
            process.WorkingSet64,
            TryGetElevatedState(),
            AppContext.BaseDirectory,
            Environment.ProcessPath);
    }

    public string ToLogText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Startup diagnostics:");
        builder.AppendLine($"OS: {OsDescription}");
        builder.AppendLine($"Runtime ID: {RuntimeDescription}");
        builder.AppendLine($"Framework: {FrameworkDescription}");
        builder.AppendLine($"Architecture: {ProcessArchitecture}");
        builder.AppendLine($"Total RAM: {FormatBytes(TotalMemoryBytes)}");
        builder.AppendLine($"Working set: {FormatBytes(WorkingSetBytes)}");
        builder.AppendLine($"Elevated: {FormatNullableBool(IsElevated)}");
        builder.AppendLine($"Base directory: {BaseDirectory}");
        builder.AppendLine($"Process path: {ProcessPath ?? "unknown"}");
        return builder.ToString();
    }

    private static long? TryGetTotalMemoryBytes()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return TryGetWindowsTotalMemoryBytes();
            }

            if (OperatingSystem.IsLinux())
            {
                return TryGetLinuxTotalMemoryBytes();
            }

            if (OperatingSystem.IsMacOS())
            {
                return TryGetMacOsTotalMemoryBytes();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool? TryGetElevatedState()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }

            return string.Equals(Environment.UserName, "root", StringComparison.Ordinal);
        }
        catch
        {
            return null;
        }
    }

    private static long? TryGetLinuxTotalMemoryBytes()
    {
        const string prefix = "MemTotal:";
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var valueText = line[prefix.Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return long.TryParse(valueText, out var kb) ? kb * 1024L : null;
        }

        return null;
    }

    private static long? TryGetMacOsTotalMemoryBytes()
    {
        var startInfo = new ProcessStartInfo("sysctl", "-n hw.memsize")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        var output = process?.StandardOutput.ReadToEnd().Trim();
        process?.WaitForExit(2000);
        return long.TryParse(output, out var bytes) ? bytes : null;
    }

    private static string FormatBytes(long? bytes)
    {
        return bytes.HasValue ? $"{bytes.Value / 1024d / 1024d:0.0} MB" : "unknown";
    }

    private static string FormatNullableBool(bool? value)
    {
        return value.HasValue ? value.Value.ToString() : "unknown";
    }

    private static long? TryGetWindowsTotalMemoryBytes()
    {
        var status = new MemoryStatusEx();
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhys : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

