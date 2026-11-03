#:property TargetFramework=net10.0-windows
#:property PublishAot=false
#:project ../../src/Windsock.Core/Windsock.Core.csproj

// Builds src/Windsock.Core/Data/asn.dat from iptoasn.com's combined table.
// See README.md beside this file for where the source comes from and how to run it.

using System.Globalization;
using System.IO.Compression;
using Windsock.Core.Asn;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run BuildAsnTable.cs -- <ip2asn-combined.tsv.gz> <asn.dat> [yyyy-mm-dd]");
    return 1;
}

string source = args[0];
string target = args[1];
DateOnly date = args.Length > 2
    ? DateOnly.ParseExact(args[2], "yyyy-MM-dd", CultureInfo.InvariantCulture)
    : DateOnly.FromDateTime(File.GetLastWriteTimeUtc(source));

using FileStream input = File.OpenRead(source);
using Stream text = source.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(input, CompressionMode.Decompress) : input;
using StreamReader reader = new(text);

string temporary = target + ".tmp";

(int four, int six, int systems) counts;

using (FileStream output = File.Create(temporary))
{
    counts = AsnTableWriter.Write(AsnTableWriter.ReadIptoasn(reader), output, date);
}

File.Move(temporary, target, overwrite: true);

using AsnTable? table = AsnTable.Open(target);

if (table is null)
{
    Console.Error.WriteLine("The table written could not be read back.");
    return 2;
}

Console.WriteLine($"{target}: {new FileInfo(target).Length:N0} bytes, {counts.four:N0} IPv4 rows, {counts.six:N0} IPv6 rows, {counts.systems:N0} networks, data of {date:yyyy-MM-dd}");

foreach (string probe in new[] { "1.1.1.1", "8.8.8.8", "13.107.42.14", "2606:4700:4700::1111" })
{
    AutonomousSystem? found = table.Find(probe);
    Console.WriteLine($"  {probe} -> {(found is null ? "-" : $"{found.Label} {found.Organisation} ({found.Country})")}");
}

return 0;
