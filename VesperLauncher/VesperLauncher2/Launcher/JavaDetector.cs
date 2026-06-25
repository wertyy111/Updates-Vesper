using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VesperLauncher.Launcher;

public sealed class JavaDetector
{
    private static readonly string[] WindowsExecutableNames = ["javaw.exe", "java.exe"];
    private static readonly string[] UnixExecutableNames = ["java"];

    public async Task<JavaDetectionResult> DetectAsync(
        int? requiredMajorVersion = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = EnumerateCandidatePaths()
            .Distinct(GetPathComparer())
            .ToArray();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(candidate))
            {
                continue;
            }

            var version = await TryReadVersionAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                continue;
            }

            if (requiredMajorVersion.HasValue && version.MajorVersion < requiredMajorVersion.Value)
            {
                continue;
            }

            return new JavaDetectionResult(candidate, version, true, null);
        }

        return new JavaDetectionResult(
            JavaExecutablePath: null,
            Version: null,
            IsUsable: false,
            Problem: requiredMajorVersion.HasValue
                ? $"Java {requiredMajorVersion.Value}+ не найдена."
                : "Java не найдена.");
    }

    public IReadOnlyList<string> EnumerateCandidatePaths()
    {
        var candidates = new List<string>();
        AddJavaHomeCandidates(candidates);
        AddPathCandidates(candidates);
        AddCommonInstallCandidates(candidates);
        return candidates;
    }

    private static void AddJavaHomeCandidates(List<string> candidates)
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (string.IsNullOrWhiteSpace(javaHome))
        {
            return;
        }

        AddExecutableCandidates(candidates, Path.Combine(javaHome, "bin"));
    }

    private static void AddPathCandidates(List<string> candidates)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddExecutableCandidates(candidates, directory);
        }
    }

    private static void AddCommonInstallCandidates(List<string> candidates)
    {
        if (OperatingSystem.IsWindows())
        {
            AddWindowsInstallCandidates(candidates);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            AddExecutableCandidates(candidates, "/Library/Java/JavaVirtualMachines");
            AddExecutableCandidates(candidates, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Java",
                "JavaVirtualMachines"));
        }

        AddExecutableCandidates(candidates, "/usr/bin");
        AddExecutableCandidates(candidates, "/usr/local/bin");
        AddExecutableCandidates(candidates, "/opt/homebrew/bin");
        AddExecutableCandidates(candidates, "/usr/lib/jvm/default/bin");

        if (Directory.Exists("/usr/lib/jvm"))
        {
            foreach (var directory in Directory.EnumerateDirectories("/usr/lib/jvm"))
            {
                AddExecutableCandidates(candidates, Path.Combine(directory, "bin"));
            }
        }
    }

    private static void AddWindowsInstallCandidates(List<string> candidates)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            AddJavaRootCandidates(candidates, Path.Combine(root, "Java"));
            AddJavaRootCandidates(candidates, Path.Combine(root, "Eclipse Adoptium"));
            AddJavaRootCandidates(candidates, Path.Combine(root, "Microsoft"));
        }
    }

    private static void AddJavaRootCandidates(List<string> candidates, string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            AddExecutableCandidates(candidates, Path.Combine(directory, "bin"));
            AddExecutableCandidates(candidates, Path.Combine(directory, "jre", "bin"));
        }
    }

    private static void AddExecutableCandidates(List<string> candidates, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var executableNames = OperatingSystem.IsWindows() ? WindowsExecutableNames : UnixExecutableNames;
        foreach (var executableName in executableNames)
        {
            candidates.Add(Path.Combine(directory, executableName));
        }
    }

    private static async Task<JavaVersionInfo?> TryReadVersionAsync(
        string javaExecutablePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = javaExecutablePath,
                ArgumentList = { "-version" },
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return null;
            }

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = string.Join(Environment.NewLine, await stderrTask.ConfigureAwait(false), await stdoutTask.ConfigureAwait(false));
            return JavaVersionInfo.TryParse(output);
        }
        catch
        {
            return null;
        }
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }
}

public sealed record JavaDetectionResult(
    string? JavaExecutablePath,
    JavaVersionInfo? Version,
    bool IsUsable,
    string? Problem);

public sealed record JavaVersionInfo(
    int MajorVersion,
    string RawVersion,
    string VendorText)
{
    public static JavaVersionInfo? TryParse(string versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput))
        {
            return null;
        }

        var lines = versionOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var versionLine = lines.FirstOrDefault(line => line.Contains("version", StringComparison.OrdinalIgnoreCase));
        if (versionLine is null)
        {
            return null;
        }

        var rawVersion = ExtractQuotedVersion(versionLine) ?? versionLine;
        var majorVersion = ParseMajorVersion(rawVersion);
        return majorVersion > 0
            ? new JavaVersionInfo(majorVersion, rawVersion, string.Join(" ", lines.Skip(1)).Trim())
            : null;
    }

    private static string? ExtractQuotedVersion(string line)
    {
        var firstQuote = line.IndexOf('"');
        if (firstQuote < 0)
        {
            return null;
        }

        var secondQuote = line.IndexOf('"', firstQuote + 1);
        return secondQuote > firstQuote
            ? line[(firstQuote + 1)..secondQuote]
            : null;
    }

    private static int ParseMajorVersion(string rawVersion)
    {
        var parts = rawVersion.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return 0;
        }

        if (parts[0] == "1" && parts.Length > 1 && int.TryParse(parts[1], out var legacyMajor))
        {
            return legacyMajor;
        }

        var leadingDigits = new string(parts[0].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(leadingDigits, out var modernMajor) ? modernMajor : 0;
    }
}

