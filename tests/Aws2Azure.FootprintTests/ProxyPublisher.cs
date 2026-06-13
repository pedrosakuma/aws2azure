using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aws2Azure.FootprintTests;

/// <summary>
/// Publishes <c>Aws2Azure.Proxy</c> as a self-contained Native-AOT binary for a
/// given build-time module selection and reports the published binary's path
/// and size. Results are cached on disk (keyed by the module selection) so a
/// repeated measurement in the same run does not re-link.
///
/// <para>The publish is the expensive part of footprint measurement (several
/// minutes of AOT linking). In CI the binary is typically published once up
/// front; set <c>AWS2AZURE_FOOTPRINT_PUBLISH_DIR</c> to a persistent location
/// (or pre-publish into it) to reuse it across test invocations.</para>
/// </summary>
internal sealed class ProxyPublisher
{
    private const string DefaultModulesKey = "all";

    /// <summary>
    /// Publishes the proxy for the given module selection. <paramref name="modules"/>
    /// is the value passed to the <c>Modules</c> MSBuild property (a
    /// semicolon/comma list, e.g. <c>"s3"</c> or <c>"sqs;sns"</c>). <c>null</c>
    /// publishes the default (all modules) binary without overriding the
    /// property — this keeps the harness usable before #273 introduces the
    /// property.
    /// </summary>
    public static PublishedBinary Publish(string? modules = null)
    {
        var rid = ResolveRid();
        var key = ModulesKey(modules);
        var outDir = Path.Combine(PublishRoot(), $"{key}-{rid}");
        var binaryPath = Path.Combine(outDir, BinaryFileName());

        // Cache hit: a previous publish (this run or a pre-seeded CI step) left
        // the binary in place. Re-publishing is a multi-minute no-op otherwise.
        if (!File.Exists(binaryPath))
        {
            PublishInternal(modules, rid, outDir);
            if (!File.Exists(binaryPath))
            {
                throw new InvalidOperationException(
                    $"AOT publish completed but no binary was produced at {binaryPath}.");
            }
        }

        var size = new FileInfo(binaryPath).Length;
        return new PublishedBinary(key, rid, binaryPath, size);
    }

    private static void PublishInternal(string? modules, string rid, string outDir)
    {
        var repoRoot = RepoRoot.Find();
        var args = new List<string>
        {
            "publish", "src/Aws2Azure.Proxy/Aws2Azure.Proxy.csproj",
            "-c", "Release",
            "-r", rid,
            "--self-contained", "true",
            "-o", outDir,
            "--nologo",
        };
        if (!string.IsNullOrWhiteSpace(modules))
        {
            // The dotnet/MSBuild property parser treats ',' and ';' as
            // property-assignment separators even when passed as a single argv
            // element, so a multi-module selection must use '+' on the command
            // line. The csproj normalizes '+' back to the canonical separator.
            var cliModules = modules.Replace(';', '+').Replace(',', '+');
            args.Add($"-p:Modules={cliModules}");
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) startInfo.ArgumentList.Add(a);

        using var process = new Process { StartInfo = startInfo };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // AOT linking is slow; allow a generous ceiling before giving up.
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(20).TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException("AOT publish timed out after 20 minutes.");
        }
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"AOT publish failed (exit {process.ExitCode}). Modules='{modules ?? "(default)"}'.\n{output}");
        }
    }

    private static string ModulesKey(string? modules)
    {
        if (string.IsNullOrWhiteSpace(modules)) return DefaultModulesKey;
        var parts = modules
            .Replace(',', ';')
            .Replace('+', ';')
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .OrderBy(p => p, StringComparer.Ordinal);
        return string.Join('-', parts);
    }

    private static string PublishRoot()
    {
        var overrideDir = Environment.GetEnvironmentVariable("AWS2AZURE_FOOTPRINT_PUBLISH_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
        // Default to a process-unique root so the on-disk cache never serves a
        // stale binary from a previous run after source changes. Reuse across
        // runs is opt-in via AWS2AZURE_FOOTPRINT_PUBLISH_DIR (CI sets it to a
        // per-job workspace path so a re-run within one job is a cache hit).
        return Path.Combine(Path.GetTempPath(), "aws2azure-footprint", _processCacheKey);
    }

    private static readonly string _processCacheKey =
        Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string ResolveRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant(),
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
        throw new PlatformNotSupportedException("Unsupported OS for footprint publish.");
    }

    private static string BinaryFileName()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Aws2Azure.Proxy.exe"
            : "Aws2Azure.Proxy";
}

/// <summary>A published AOT proxy binary and its on-disk size.</summary>
internal sealed record PublishedBinary(string ModulesKey, string Rid, string BinaryPath, long SizeBytes)
{
    public double SizeMb => SizeBytes / (1024.0 * 1024.0);
}
