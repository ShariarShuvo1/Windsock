#:project ../../src/Windsock.Core/Windsock.Core.csproj
#:property TargetFramework=net10.0-windows

// Builds src/Windsock.Core/Data/makers.oui, the table Windsock names an
// adapter's maker from, out of the IEEE Registration Authority's public
// listings. Run from the repository root:
//
//     dotnet run tools/oui/build-makers.cs
//     dotnet run tools/oui/build-makers.cs -- --from <folder holding the four CSVs> --date 2026-10-05
//
// Without --from it downloads the four listings below from standards-oui.ieee.org.
// The table keeps only each block's prefix and the organisation's name; the
// postal addresses in the listings are dropped. See OuiTableWriter for the
// layout and THIRD-PARTY-NOTICES.md for the attribution the data carries.
// Rebuild it when cutting a release, not at run time: Windsock itself never
// downloads it.

using System.Globalization;
using Windsock.Core.Home;

string[] listings = ["oui/oui.csv", "oui28/mam.csv", "oui36/oui36.csv", "iab/iab.csv"];
string? from = Option("--from");
string output = Option("--out") ?? Path.Combine("src", "Windsock.Core", "Data", OuiTable.FileName);
DateOnly retrieved = Option("--date") is { } day
    ? DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture)
    : DateOnly.FromDateTime(DateTime.UtcNow);

List<OuiAssignment> assignments = [];

using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Windsock-build-makers/1.0 (+https://github.com/ShariarShuvo1/Windsock)");

foreach (string listing in listings)
{
    string text;

    if (from is not null)
    {
        text = await File.ReadAllTextAsync(Path.Combine(from, Path.GetFileName(listing)));
    }
    else
    {
        Uri address = new("https://standards-oui.ieee.org/" + listing);
        Console.WriteLine($"Downloading {address}");
        using HttpResponseMessage response = await http.GetAsync(address);
        response.EnsureSuccessStatusCode();

        if (listing == listings[0] && response.Content.Headers.LastModified is { } modified)
        {
            retrieved = DateOnly.FromDateTime(modified.UtcDateTime);
        }

        text = await response.Content.ReadAsStringAsync();
    }

    int before = assignments.Count;
    assignments.AddRange(OuiTableWriter.ReadCsv(new StringReader(text)));
    Console.WriteLine($"{Path.GetFileName(listing)}: {assignments.Count - before} blocks");
}

byte[] table = OuiTableWriter.Build(assignments, retrieved);
OuiTable check = OuiTable.Load(table);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await File.WriteAllBytesAsync(output, table);

Console.WriteLine($"Wrote {output}: {table.Length:N0} bytes, {check.Blocks:N0} blocks, {check.Organisations:N0} organisations, retrieved {check.Retrieved:yyyy-MM-dd}");
return 0;

string? Option(string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
