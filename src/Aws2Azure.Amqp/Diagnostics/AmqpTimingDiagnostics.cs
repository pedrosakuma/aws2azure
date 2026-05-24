using System.Diagnostics;
using System.Globalization;

namespace Aws2Azure.Amqp.Diagnostics;

/// <summary>
/// Lightweight optional timing breadcrumbs for AMQP send paths, gated
/// behind <c>AWS2AZURE_AMQP_TIMING=1</c>. When disabled the static
/// <see cref="Enabled"/> flag short-circuits every call site with a
/// single field read (no allocations, no formatting). When enabled,
/// rows are written to <see cref="Console.Error"/> as TSV:
/// <code>
/// [AMQP-TIMING]\tts=&lt;unix-ms&gt;\tlink=&lt;name&gt;\top=&lt;op&gt;\tkey=val\t...
/// </code>
/// Stays in tree because issue #129 needs it on demand for any future
/// throughput regression. NEVER enable in production — formatting +
/// Console.Error.WriteLine is allocating and synchronously blocking.
/// </summary>
internal static class AmqpTimingDiagnostics
{
    public static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("AWS2AZURE_AMQP_TIMING"), "1", StringComparison.Ordinal);

    public static void LogSend(
        string link,
        long totalUs,
        long creditUs,
        long writeUs,
        long dispositionUs,
        bool settled,
        int payloadBytes)
    {
        if (!Enabled) return;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"[AMQP-TIMING]\tts={Stopwatch.GetTimestamp()}\tlink={link}\top=Send\ttotalUs={totalUs}\tcreditUs={creditUs}\twriteUs={writeUs}\tdispUs={dispositionUs}\tsettled={settled}\tbytes={payloadBytes}");
        Console.Error.WriteLine(line);
    }

    public static long ElapsedMicros(long startTimestamp)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        return elapsedTicks * 1_000_000L / Stopwatch.Frequency;
    }
}
