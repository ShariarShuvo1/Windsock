namespace Windsock.Core.Asn;

/// <summary>An autonomous system: one network on the internet, run by one organisation.</summary>
/// <param name="Number">Its AS number.</param>
/// <param name="Name">Its registered name, as the routing data gives it ("CLOUDFLARENET", "GTELECOM-AS-AP Gtelecom Pty Ltd").</param>
/// <param name="Country">The two-letter country it is registered in, or empty.</param>
public sealed record AutonomousSystem(uint Number, string Name, string Country)
{
    /// <summary>The organisation, as a person would name it: "Cloudflare", "Gtelecom Pty Ltd".</summary>
    public string Organisation => AsnNames.Organisation(Number, Name);

    /// <summary>"AS13335".</summary>
    public string Label => string.Concat("AS", Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Turns registry AS names into organisation names people recognise.</summary>
/// <remarks>
/// <para>
/// The routing data names a network by its registry handle, sometimes followed
/// by the organisation: "SUMMITCOMMUNICATIONS-BD Summit Communications Ltd".
/// Where an organisation follows a handle it is used. A first word counts as a
/// handle only when it has a hyphen, an underscore or a digit in it: plain
/// capitals are often the organisation's own first word ("EQUINIX BRASIL"),
/// and taking them for a handle dropped it from 7,019 of 87,125 names.
/// </para>
/// <para>
/// The biggest networks - the ones most connections go to - are named here by
/// AS number, never by the start of a name: "APPLEBEE-NETWORKS" is not Apple
/// and "VALVERA" is not Valve. Each number was checked against the routing
/// data the table is built from (iptoasn, 2026-10-04).
/// </para>
/// </remarks>
public static class AsnNames
{
    private static readonly Dictionary<uint, string> Known = new()
    {
        [13335] = "Cloudflare",
        [209242] = "Cloudflare",
        [15169] = "Google",
        [19527] = "Google",
        [36040] = "Google",
        [396982] = "Google Cloud",
        [16509] = "Amazon",
        [14618] = "Amazon",
        [8987] = "Amazon",
        [8075] = "Microsoft",
        [8068] = "Microsoft",
        [8069] = "Microsoft",
        [12076] = "Microsoft",
        [20940] = "Akamai",
        [16625] = "Akamai",
        [21342] = "Akamai",
        [33905] = "Akamai",
        [34164] = "Akamai",
        [35994] = "Akamai",
        [32787] = "Akamai",
        [63949] = "Akamai (Linode)",
        [32934] = "Meta",
        [63293] = "Meta",
        [54115] = "Meta",
        [714] = "Apple",
        [6185] = "Apple",
        [54113] = "Fastly",
        [2906] = "Netflix",
        [40027] = "Netflix",
        [55095] = "Netflix",
        [22822] = "Edgio",
        [13414] = "X",
        [35995] = "X",
        [14061] = "DigitalOcean",
        [16276] = "OVHcloud",
        [24940] = "Hetzner",
        [213230] = "Hetzner",
        [31898] = "Oracle",
        [45102] = "Alibaba",
        [37963] = "Alibaba",
        [132203] = "Tencent",
        [45090] = "Tencent",
        [32590] = "Valve",
        [396986] = "ByteDance",
        [138699] = "ByteDance (TikTok)",
        [30103] = "Zoom",
        [36459] = "GitHub",
    };

    /// <summary>The organisation behind a network, from its AS number and registry name.</summary>
    public static string Organisation(uint number, string? name) =>
        Known.TryGetValue(number, out string? known) ? known : Organisation(name);

    /// <summary>The organisation behind a registry name, when the AS number is not to hand.</summary>
    public static string Organisation(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string trimmed = name.Trim();

        // "HANDLE - Organisation, Inc." keeps what follows the dash.
        int dash = trimmed.IndexOf(" - ", StringComparison.Ordinal);

        if (dash > 0 && dash + 3 < trimmed.Length)
        {
            return trimmed[(dash + 3)..].Trim();
        }

        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        string first = space < 0 ? trimmed : trimmed[..space];

        if (space > 0 && IsHandle(first))
        {
            string rest = trimmed[(space + 1)..].Trim();

            // "AWS-GOVCLOUD AWS-GOVCLOUD": the organisation only repeats the handle.
            return rest.Length > 0 && !rest.Equals(first, StringComparison.OrdinalIgnoreCase) ? rest : first;
        }

        return trimmed;
    }

    /// <summary>A registry handle: capitals with a hyphen, underscore or digit in them ("SUMMIT-BD", "AS9498").</summary>
    private static bool IsHandle(string word) =>
        word.Length > 1
        && word.Any(char.IsAsciiLetter)
        && word.Any(each => char.IsAsciiDigit(each) || each is '-' or '_')
        && word.All(each => char.IsAsciiLetterUpper(each) || char.IsAsciiDigit(each) || each is '-' or '_' or '.');
}
