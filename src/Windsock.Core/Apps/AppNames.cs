using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Windsock.Core.Apps;

/// <summary>What to call an app Windows counted, and where to find its picture.</summary>
/// <param name="Id">Windows' identifier, which is also the key everything is stored under.</param>
/// <param name="Kind">What sort of thing the identifier names.</param>
/// <param name="Name">What to show.</param>
/// <param name="Path">The program's file, or a packaged app's folder; null for Windows' own buckets.</param>
/// <param name="IconPath">
/// For a packaged app, the image its manifest names; for a program, its own
/// file, which holds its icon. Null when there is none.
/// </param>
public sealed record AppDescription(string Id, AppKind Kind, string Name, string? Path, string? IconPath);

/// <summary>
/// Puts names to the identifiers Windows' data usage hands back, which carry
/// none of their own.
/// </summary>
/// <remarks>
/// A program is named the way the Apps page names one - the description in
/// its version resource, or failing that its file name - so the same program
/// reads the same on both pages. A packaged app is named from its manifest,
/// resolved in the user's language through the package's own resources.
/// Everything is read locally; nothing here touches the network.
/// </remarks>
public sealed class AppNames
{
    private const int NameLength = 512;

    private readonly DevicePaths _paths;
    private readonly ConcurrentDictionary<string, AppDescription> _known = new(StringComparer.Ordinal);

    public AppNames(DevicePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>What to call an identifier. Cached, and safe from any thread.</summary>
    public AppDescription Describe(string? attributionId)
    {
        AppAttribution attribution = AppAttribution.Classify(attributionId);
        return _known.GetOrAdd(attribution.Id, _ => ReadOrPlain(attribution));
    }

    /// <summary>
    /// Reads what an identifier is called, or settles for the identifier itself.
    /// </summary>
    /// <remarks>
    /// Naming reads files, package manifests and resource strings that belong
    /// to other programs. Whatever goes wrong with one of them costs that app
    /// its proper name, never the page listing it.
    /// </remarks>
    private AppDescription ReadOrPlain(AppAttribution attribution)
    {
        try
        {
            return Read(attribution);
        }
#pragma warning disable CA1031 // See remarks: any failure here means only a plainer name.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new AppDescription(
                attribution.Id,
                attribution.Kind,
                attribution.Kind == AppKind.Package ? attribution.Id.Split('_')[0] : FileName(attribution.Id),
                null,
                null);
        }
    }

    private AppDescription Read(AppAttribution attribution) => attribution.Kind switch
    {
        AppKind.Program => Program(attribution.Id),
        AppKind.Package => Package(attribution.Id),
        AppKind.Unattributed => new AppDescription(attribution.Id, attribution.Kind, "Unattributed", null, null),
        _ => new AppDescription(
            attribution.Id,
            attribution.Kind,
            attribution.SystemBucket is { } bucket ? "System · " + bucket : "System",
            null,
            null),
    };

    private AppDescription Program(string id)
    {
        string? path = _paths.ToDosPath(id);
        string fallback = FileName(path ?? id);

        if (path is null)
        {
            return new AppDescription(id, AppKind.Program, fallback, id, null);
        }

        string? onDisk = CasedAsOnDisk(path);
        string name = Description(onDisk) ?? FileName(onDisk ?? path);

        return new AppDescription(id, AppKind.Program, name, onDisk ?? path, onDisk);
    }

    /// <summary>
    /// A file name without its extension, which is what a program is called
    /// when its file says nothing better.
    /// </summary>
    internal static string FileName(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path.TrimEnd('\\'));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    /// <summary>
    /// The path as the file system spells it. Windows' data usage lower-cases
    /// every path, which would otherwise show as a lower-case file name.
    /// </summary>
    private static string? CasedAsOnDisk(string path)
    {
        try
        {
            FileInfo file = new(path);

            if (!file.Exists || file.Directory is null)
            {
                return null;
            }

            FileInfo? cased = file.Directory.EnumerateFiles(file.Name).FirstOrDefault();
            return cased?.FullName ?? file.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Description(string? path)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            return string.IsNullOrEmpty(description) ? null : description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static AppDescription Package(string family)
    {
        string fallback = family.Split('_')[0];

        // The calls that find a package arrived with Windows 8.1. Asked as 10,
        // because the analyser reads platform names as version numbers and 8.1
        // reports itself as 6.3; .NET 10 runs on nothing older than 10 anyway.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            return new AppDescription(family, AppKind.Package, fallback, null, null);
        }

        string? fullName = FullNameOf(family);

        if (fullName is null || FolderOf(fullName) is not { } folder)
        {
            return new AppDescription(family, AppKind.Package, fallback, null, null);
        }

        (string? display, string? logo) = ReadManifest(folder);
        string? name = display is null ? null : Resolve(fullName, display);

        return new AppDescription(
            family,
            AppKind.Package,
            string.IsNullOrWhiteSpace(name) ? fallback : name.Trim(),
            folder,
            logo is null ? null : Logo(folder, logo));
    }

    [SupportedOSPlatform("windows8.1")]
    private static string? FullNameOf(string family)
    {
        uint count = 0;
        uint length = 0;
        WIN32_ERROR first = PInvoke.GetPackagesByPackageFamily(family, ref count, default, ref length, default);

        if (count == 0 || length == 0 || (first != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER && first != WIN32_ERROR.NO_ERROR))
        {
            return null;
        }

        // Asked twice: a package installed or removed in between leaves the
        // second answer short, and that is simply no answer.
        if (count > 64 || length > 32767)
        {
            return null;
        }

        PWSTR[] names = new PWSTR[count];
        char[] buffer = new char[length];
        WIN32_ERROR second = PInvoke.GetPackagesByPackageFamily(family, ref count, names, ref length, buffer);

        if (second != WIN32_ERROR.NO_ERROR || count == 0)
        {
            return null;
        }

        // Each name points into the buffer, one after another, each ended by a
        // nul; the first is the one wanted.
        int end = Array.IndexOf(buffer, '\0');
        return end > 0 ? new string(buffer, 0, end) : null;
    }

    /// <summary>Where a package is installed, or null when Windows will not say.</summary>
    [SupportedOSPlatform("windows8.1")]
    internal static string? FolderOf(string fullName)
    {
        uint length = 0;
        _ = PInvoke.GetPackagePathByFullName(fullName, ref length, default);

        if (length == 0 || length > 32767)
        {
            return null;
        }

        char[] buffer = new char[length];

        if (PInvoke.GetPackagePathByFullName(fullName, ref length, buffer) != WIN32_ERROR.NO_ERROR)
        {
            return null;
        }

        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>
    /// Reads the package's name and logo from its manifest. Only the manifest's
    /// own properties are read, and nothing it refers to outside the package.
    /// </summary>
    private static (string? Display, string? Logo) ReadManifest(string folder)
    {
        string manifest = System.IO.Path.Combine(folder, "AppxManifest.xml");

        try
        {
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
            };

            using XmlReader reader = XmlReader.Create(manifest, settings);
            XDocument document = XDocument.Load(reader);
            XElement? properties = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "Properties");

            string? display = properties?.Elements().FirstOrDefault(element => element.Name.LocalName == "DisplayName")?.Value;
            string? logo = properties?.Elements().FirstOrDefault(element => element.Name.LocalName == "Logo")?.Value;

            return (string.IsNullOrWhiteSpace(display) ? null : display.Trim(), string.IsNullOrWhiteSpace(logo) ? null : logo.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The indirect string that resolves a manifest's <c>ms-resource:</c> name,
    /// or null when the value is a plain name that needs no resolving.
    /// </summary>
    internal static string? IndirectSource(string fullName, string value)
    {
        const string Scheme = "ms-resource:";

        if (!value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            || fullName.Contains('{', StringComparison.Ordinal)
            || fullName.Contains('}', StringComparison.Ordinal)
            || value.Contains('}', StringComparison.Ordinal))
        {
            return null;
        }

        string packageName = fullName.Split('_')[0];
        string key = value[Scheme.Length..];
        string uri;

        if (key.StartsWith("//", StringComparison.Ordinal))
        {
            uri = Scheme + key;
        }
        else if (key.Contains('/', StringComparison.Ordinal))
        {
            uri = string.Concat(Scheme, "//", packageName, "/", key.TrimStart('/'));
        }
        else
        {
            uri = string.Concat(Scheme, "//", packageName, "/resources/", key);
        }

        return string.Concat("@{", fullName, "?", uri, "}");
    }

    private static unsafe string? Resolve(string fullName, string value)
    {
        if (IndirectSource(fullName, value) is not { } source)
        {
            return value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : value;
        }

        char* output = stackalloc char[NameLength];
        output[0] = '\0';

        fixed (char* input = source)
        {
            HRESULT result = PInvoke.SHLoadIndirectString(new PCWSTR(input), new PWSTR(output), NameLength, null);

            if (result.Failed)
            {
                return null;
            }
        }

        // Read no further than the buffer, whether or not Windows ended the
        // name inside it; a name that filled it without an end is no name.
        ReadOnlySpan<char> written = new(output, NameLength);
        int end = written.IndexOf('\0');

        if (end < 0)
        {
            return null;
        }

        string resolved = new(written[..end]);
        return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
    }

    /// <summary>
    /// The file a manifest's logo names. Packages usually ship only scaled
    /// copies, <c>StoreLogo.scale-100.png</c> and the like, which Windows picks
    /// between; the plainest of them is taken. Never anything outside the
    /// package's folder, whatever the manifest says.
    /// </summary>
    internal static string? Logo(string folder, string relative)
    {
        try
        {
            string root = System.IO.Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            string wanted = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));

            if (!wanted.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (File.Exists(wanted))
            {
                return wanted;
            }

            string? directory = System.IO.Path.GetDirectoryName(wanted);

            if (directory is null || !Directory.Exists(directory))
            {
                return null;
            }

            string stem = System.IO.Path.GetFileNameWithoutExtension(wanted);
            string extension = System.IO.Path.GetExtension(wanted);

            foreach (string qualifier in new[] { ".scale-100", ".scale-125", ".scale-150", ".scale-200", ".targetsize-48", ".targetsize-32", ".scale-400" })
            {
                string candidate = System.IO.Path.Combine(directory, stem + qualifier + extension);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Directory.EnumerateFiles(directory, stem + ".*" + extension).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
