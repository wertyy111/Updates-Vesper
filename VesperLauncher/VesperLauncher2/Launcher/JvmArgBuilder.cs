using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VesperLauncher.Launcher;

public sealed class JvmArgBuilder
{
    private const int MinimumHeapMb = 1024;
    private const int WeakPcThresholdMb = 6144;

    public JvmArgPlan Build(JvmArgBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var totalMemoryMb = request.TotalSystemMemoryMb ?? TryGetInstalledSystemMemoryMb();
        var maximumHeapMb = ResolveMaximumHeapMb(request, totalMemoryMb);
        var initialHeapMb = ResolveInitialHeapMb(maximumHeapMb, totalMemoryMb);
        var args = new List<string>
        {
            $"-Xms{initialHeapMb}M",
            $"-Xmx{maximumHeapMb}M"
        };

        args.AddRange(BuildAikarFlags(totalMemoryMb, request.JavaMajorVersion));
        args.AddRange(BuildCommonRuntimeFlags());

        return new JvmArgPlan(
            initialHeapMb,
            maximumHeapMb,
            totalMemoryMb,
            IsWeakPc(totalMemoryMb),
            RemoveDuplicateJvmOptions(args));
    }

    public IReadOnlyList<RecommendedOptimizationMod> GetOptimizationMods(string versionId)
    {
        var loaderHint = versionId.Contains("forge", StringComparison.OrdinalIgnoreCase)
            ? ModLoaderKind.Forge
            : ModLoaderKind.Fabric;

        if (loaderHint == ModLoaderKind.Forge)
        {
            return new[]
            {
                new RecommendedOptimizationMod("embeddium", "Embeddium", "Forge renderer optimization"),
                new RecommendedOptimizationMod("ferrite-core", "FerriteCore", "Lower memory usage"),
                new RecommendedOptimizationMod("modernfix", "ModernFix", "Startup and memory fixes")
            };
        }

        return new[]
        {
            new RecommendedOptimizationMod("sodium", "Sodium", "Renderer optimization"),
            new RecommendedOptimizationMod("lithium", "Lithium", "Server-side logic optimization"),
            new RecommendedOptimizationMod("ferrite-core", "FerriteCore", "Lower memory usage"),
            new RecommendedOptimizationMod("modernfix", "ModernFix", "Startup and memory fixes")
        };
    }

    private static int ResolveMaximumHeapMb(JvmArgBuildRequest request, int? totalMemoryMb)
    {
        var requestedMb = Math.Max(MinimumHeapMb, request.RequestedMaximumHeapMb);
        var adaptiveMb = ResolveAdaptiveMaximumHeapMb(totalMemoryMb);
        var safeMb = ResolveSafeMaximumHeapMb(totalMemoryMb);
        var maximumMb = Math.Min(requestedMb, adaptiveMb);

        if (safeMb.HasValue)
        {
            maximumMb = Math.Min(maximumMb, safeMb.Value);
        }

        if (request.JavaMajorVersion > 0 &&
            request.JavaMajorVersion <= 8 &&
            RequiresLegacyJava(request.VersionId))
        {
            maximumMb = Math.Min(maximumMb, 3072);
        }

        return Math.Max(MinimumHeapMb, RoundDownToHalfGb(maximumMb));
    }

    private static int ResolveAdaptiveMaximumHeapMb(int? totalMemoryMb)
    {
        if (!totalMemoryMb.HasValue || totalMemoryMb.Value <= 0)
        {
            return 4096;
        }

        var total = totalMemoryMb.Value;
        if (total < 4096)
        {
            return 1536;
        }

        if (total < 8192)
        {
            return 2048;
        }

        if (total < 16384)
        {
            return 4096;
        }

        return 6144;
    }

    private static int? ResolveSafeMaximumHeapMb(int? totalMemoryMb)
    {
        if (!totalMemoryMb.HasValue || totalMemoryMb.Value <= 0)
        {
            return null;
        }

        var reservedForSystemMb = totalMemoryMb.Value <= WeakPcThresholdMb ? 1536 : 2048;
        return Math.Max(MinimumHeapMb, totalMemoryMb.Value - reservedForSystemMb);
    }

    private static int ResolveInitialHeapMb(int maximumHeapMb, int? totalMemoryMb)
    {
        if (IsWeakPc(totalMemoryMb))
        {
            return Math.Clamp(maximumHeapMb / 2, 512, maximumHeapMb);
        }

        return Math.Clamp(maximumHeapMb / 2, MinimumHeapMb, maximumHeapMb);
    }

    private static IEnumerable<string> BuildAikarFlags(int? totalMemoryMb, int javaMajorVersion)
    {
        var flags = new List<string>
        {
            "-XX:+UseG1GC",
            "-XX:MaxGCPauseMillis=50",
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:G1NewSizePercent=20",
            "-XX:G1ReservePercent=20",
            "-XX:G1MixedGCCountTarget=4",
            "-XX:G1HeapWastePercent=5",
            "-XX:G1MixedGCLiveThresholdPercent=90",
            "-XX:G1RSetUpdatingPauseTimePercent=5",
            "-XX:SurvivorRatio=32",
            "-XX:+PerfDisableSharedMem",
            "-XX:MaxTenuringThreshold=1"
        };

        if (!IsWeakPc(totalMemoryMb) && javaMajorVersion >= 9)
        {
            flags.Insert(7, "-XX:+AlwaysPreTouch");
        }

        return flags;
    }

    private static IEnumerable<string> BuildCommonRuntimeFlags()
    {
        return new[]
        {
            "-Djava.net.preferIPv4Stack=true",
            "-Djava.net.useSystemProxies=true",
            "-Dlog4j2.formatMsgNoLookups=true",
            "-Dsun.stdout.encoding=UTF-8",
            "-Dsun.stderr.encoding=UTF-8",
            "-Dfile.encoding=UTF-8"
        };
    }

    private static IReadOnlyList<string> RemoveDuplicateJvmOptions(IEnumerable<string> args)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var arg in args)
        {
            var key = GetJvmOptionKey(arg);
            if (seen.Add(key))
            {
                result.Add(arg);
            }
        }

        return result;
    }

    private static string GetJvmOptionKey(string arg)
    {
        if (arg.StartsWith("-XX:", StringComparison.Ordinal))
        {
            var equalsIndex = arg.IndexOf('=');
            return equalsIndex > 0 ? arg[..equalsIndex] : arg;
        }

        if (arg.StartsWith("-D", StringComparison.Ordinal))
        {
            var equalsIndex = arg.IndexOf('=');
            return equalsIndex > 0 ? arg[..equalsIndex] : arg;
        }

        return arg;
    }

    private static bool RequiresLegacyJava(string versionId)
    {
        var candidate = new string(versionId
            .SkipWhile(ch => !char.IsDigit(ch))
            .TakeWhile(ch => char.IsDigit(ch) || ch == '.')
            .ToArray());

        if (Version.TryParse(candidate, out var parsed))
        {
            return parsed.Major == 1 && parsed.Minor <= 16;
        }

        return versionId.Contains("1.16.5", StringComparison.Ordinal);
    }

    private static int RoundDownToHalfGb(int memoryMb)
    {
        if (memoryMb < 2048)
        {
            return memoryMb;
        }

        return Math.Max(MinimumHeapMb, memoryMb / 512 * 512);
    }

    private static bool IsWeakPc(int? totalMemoryMb)
    {
        return totalMemoryMb.HasValue && totalMemoryMb.Value <= WeakPcThresholdMb;
    }

    private static int? TryGetInstalledSystemMemoryMb()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            if (GetPhysicallyInstalledSystemMemory(out var totalKilobytes) && totalKilobytes > 0)
            {
                var totalMb = totalKilobytes / 1024UL;
                return totalMb > int.MaxValue ? int.MaxValue : (int)totalMb;
            }
        }
        catch
        {
            // Keep launch resilient; selected memory is still validated by the caller.
        }

        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
}

public sealed record JvmArgBuildRequest(
    string VersionId,
    int RequestedMaximumHeapMb,
    int JavaMajorVersion,
    int? TotalSystemMemoryMb = null);

public sealed record JvmArgPlan(
    int InitialHeapMb,
    int MaximumHeapMb,
    int? TotalSystemMemoryMb,
    bool IsWeakPc,
    IReadOnlyList<string> Arguments);

public sealed record RecommendedOptimizationMod(
    string ProjectId,
    string DisplayName,
    string Reason);

