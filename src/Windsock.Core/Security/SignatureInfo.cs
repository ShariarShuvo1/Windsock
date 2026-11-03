namespace Windsock.Core.Security;

/// <summary>What Windows says about a program's signature.</summary>
public enum SignatureState
{
    /// <summary>Signed, the signature checks out, and the chain ends at a root this PC trusts.</summary>
    Trusted,

    /// <summary>Carries no signature, embedded or through a Windows catalogue.</summary>
    NoSignature,

    /// <summary>
    /// Signed, but the signature does not hold: the file was changed after
    /// signing, the certificate expired or was distrusted, or the chain ends
    /// at a root this PC does not trust.
    /// </summary>
    NotTrusted,

    /// <summary>The file could not be read to check, so nothing is claimed either way.</summary>
    Unreadable,

    /// <summary>
    /// Signed, but Windows could not finish checking the certificate chain
    /// from what this PC already holds, and Windsock never lets it go online
    /// to fetch the rest. Nothing is claimed either way, so it is not marked.
    /// </summary>
    Unconfirmed,
}

/// <summary>Where a valid signature was found.</summary>
public enum SignatureSource
{
    /// <summary>In the file itself.</summary>
    Embedded,

    /// <summary>
    /// In a Windows catalogue that lists the file's hash, which is how most of
    /// Windows' own files are signed.
    /// </summary>
    Catalog,
}

/// <summary>Who signed a program, and whether the signature holds.</summary>
/// <param name="State">What Windows said.</param>
/// <param name="Publisher">The signer's name, such as "Microsoft Windows"; null when there is no valid signature.</param>
/// <param name="Source">Where the signature is, when it is valid.</param>
/// <param name="Code">Windows' answer as an HRESULT, for the details; 0 for a valid signature.</param>
/// <remarks>
/// A valid signature says who signed the file and that it has not changed
/// since. It does not say the program is safe.
/// </remarks>
public sealed record SignatureInfo(SignatureState State, string? Publisher, SignatureSource? Source, int Code)
{
    /// <summary>A file that could not be read.</summary>
    public static SignatureInfo Unreadable(int code = 0) => new(SignatureState.Unreadable, null, null, code);

    /// <summary>One sentence for a tooltip or the details.</summary>
    public string Summary => State switch
    {
        SignatureState.Trusted when Source == SignatureSource.Catalog =>
            string.Concat("Signed by ", Publisher ?? "an unnamed publisher", " through a Windows catalogue."),
        SignatureState.Trusted => string.Concat("Signed by ", Publisher ?? "an unnamed publisher", "."),
        SignatureState.NoSignature => "Not signed: nothing says who made this program.",
        SignatureState.NotTrusted => string.Concat("Signed, but the signature does not hold (", Reason(Code), ")."),
        SignatureState.Unconfirmed => string.Concat(
            "Signed",
            Publisher is null ? string.Empty : " by " + Publisher,
            ", but Windows could not confirm it without going online, which Windsock never does (",
            Reason(Code),
            ")."),
        _ => "The file could not be read to check its signature.",
    };

    /// <summary>
    /// Whether to mark a program: unsigned, or signed in a way that does not
    /// hold, and talking to the internet. Nothing is marked for a file that
    /// could not be read, since that claims nothing.
    /// </summary>
    public bool IsFlagged(bool talksToInternet) =>
        talksToInternet && State is SignatureState.NoSignature or SignatureState.NotTrusted;

    /// <summary>Plain words for the WinVerifyTrust answers a page may meet.</summary>
    internal static string Reason(int code) => unchecked((uint)code) switch
    {
        0x800B0101 => "the certificate has expired",
        0x800B0109 => "it ends at a root this PC does not trust",
        0x800B010A => "the certificate chain is incomplete",
        0x800B010C => "the certificate was revoked",
        0x80092013 => "the revocation check needs the network",
        0x800B0111 => "the signer is explicitly distrusted",
        0x80096010 => "the file was changed after it was signed",
        0x800B0004 => "the signer is not trusted",
        0x80092026 => "this PC's security settings refused it",
        _ => string.Concat("Windows answered 0x", unchecked((uint)code).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
    };
}
