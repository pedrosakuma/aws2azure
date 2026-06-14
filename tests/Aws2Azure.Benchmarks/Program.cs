using System.Globalization;
using System.Reflection;
using System.Text;
using Aws2Azure.Benchmarks.DynamoDb;
using BenchmarkDotNet.Running;

// The CosmosBinary decode benchmarks measure CPU + allocations; the wire-size
// delta (the reason CosmosBinary exists) is a static property of the payloads,
// so it is reported here directly rather than through BenchmarkDotNet.
PrintWireSizes();

// `--sizes` prints only the wire-size table and exits; any other args are
// forwarded to BenchmarkDotNet (e.g. `--filter *`, `--filter *DecodeThenParse*`).
string[] benchArgs = args.Where(a => !string.Equals(a, "--sizes", StringComparison.Ordinal)).ToArray();
if (args.Contains("--sizes") && benchArgs.Length == 0)
{
    return;
}

BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(benchArgs);

static void PrintWireSizes()
{
    var inv = CultureInfo.InvariantCulture;
    Console.WriteLine();
    Console.WriteLine("CosmosBinary vs text JSON — wire size (Cosmos → proxy payload)");
    Console.WriteLine("| Page            |   Text B |  Binary B | Binary/Text | Saving |");
    Console.WriteLine("|-----------------|----------|-----------|-------------|--------|");
    foreach (var page in CosmosBinaryDecodeBenchmarks.Pages)
    {
        string json = SyntheticCosmosPage.Build(page.DocumentCount, page.PayloadBytes, page.ExtraAttributes);
        int textBytes = Encoding.UTF8.GetByteCount(json);
        int binaryBytes = CosmosBinaryTestEncoder.Encode(json).Length;
        double ratio = textBytes == 0 ? 0 : (double)binaryBytes / textBytes;
        double saving = textBytes == 0 ? 0 : 1.0 - ratio;
        Console.WriteLine(string.Format(
            inv,
            "| {0,-15} | {1,8} | {2,9} | {3,10:0.0%} | {4,5:0.0%} |",
            page.Name,
            textBytes,
            binaryBytes,
            ratio,
            saving));
    }

    Console.WriteLine();
}
