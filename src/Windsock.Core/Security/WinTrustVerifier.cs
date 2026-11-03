using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography.Catalog;
using Windows.Win32.Security.WinTrust;

namespace Windsock.Core.Security;

/// <summary>Checks one file's signature.</summary>
public interface ISignatureVerifier
{
    /// <summary>What Windows says about the file's signature. Never throws.</summary>
    SignatureInfo Verify(string path);
}

/// <summary>
/// Checks a program's Authenticode signature with WinVerifyTrust, embedded
/// first and then through Windows' catalogues, without touching the network.
/// </summary>
/// <remarks>
/// <para>
/// Most of Windows' own files carry no signature of their own: they are listed
/// by hash in a signed catalogue. <c>cmd.exe</c> answers "no signature" checked
/// on its own and "signed" through its catalogue, so a check that stopped at
/// the file would call half of Windows unsigned.
/// </para>
/// <para>
/// Nothing leaves the machine. Revocation is not checked
/// (<c>WTD_REVOKE_NONE</c>, <c>WTD_REVOCATION_CHECK_NONE</c>), and
/// <c>WTD_CACHE_ONLY_URL_RETRIEVAL</c> keeps WinVerifyTrust to what it already
/// has: Microsoft documents that flag, not the other two, as the one that stops
/// every retrieval, intermediate certificates included. A certificate revoked
/// since this PC last heard is therefore still called valid.
/// </para>
/// </remarks>
public sealed unsafe class WinTrustVerifier : ISignatureVerifier
{
    private const int NoSignature = unchecked((int)0x800B0100);        // TRUST_E_NOSIGNATURE
    private const int SubjectFormUnknown = unchecked((int)0x800B0003); // TRUST_E_SUBJECT_FORM_UNKNOWN
    private const int ProviderUnknown = unchecked((int)0x800B0001);    // TRUST_E_PROVIDER_UNKNOWN
    private const int FileError = unchecked((int)0x80092003);          // CRYPT_E_FILE_ERROR
    private const int UntrustedRoot = unchecked((int)0x800B0109);      // CERT_E_UNTRUSTEDROOT
    private const int Chaining = unchecked((int)0x800B010A);           // CERT_E_CHAINING
    private const int RevocationOffline = unchecked((int)0x80092013);  // CRYPT_E_REVOCATION_OFFLINE

    /// <summary>The most catalogues tried for one file; Windows lists a file in a handful at most.</summary>
    private const int MostCatalogs = 32;

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2.</summary>
    private static readonly Guid GenericVerify = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    /// <inheritdoc />
    public SignatureInfo Verify(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return SignatureInfo.Unreadable();
        }

        try
        {
            SignatureInfo embedded = VerifyEmbedded(path);

            // A signature in the file that fails stands: a catalogue entry for
            // the same bytes would not make a broken embedded one sound.
            if (embedded.State != SignatureState.NoSignature)
            {
                return embedded;
            }

            // The catalogue calls are Windows 8 and later (asked as 10, the
            // floor Windsock runs on); otherwise the embedded answer is all.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            {
                return embedded;
            }

            return VerifyThroughCatalog(path, "SHA256")
                ?? VerifyThroughCatalog(path, null)
                ?? embedded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return SignatureInfo.Unreadable();
        }
    }

    private static SignatureInfo VerifyEmbedded(string path)
    {
        fixed (char* file = path)
        {
            WINTRUST_FILE_INFO info = new()
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = file,
            };

            WINTRUST_DATA data = Request(WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_FILE);
            data.pFile = &info;

            return Check(&data, SignatureSource.Embedded);
        }
    }

    /// <summary>
    /// The file's signature through the catalogue that lists its hash, or null
    /// when no catalogue does.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="algorithm">The hash to look it up by; null lets Windows choose (SHA-1 catalogues).</param>
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static SignatureInfo? VerifyThroughCatalog(string path, string? algorithm)
    {
        using SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (!PInvoke.CryptCATAdminAcquireContext2(out nint admin, null, algorithm, null))
        {
            return null;
        }

        try
        {
            uint size = 0;
            _ = PInvoke.CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, default);

            if (size is 0 or > 128)
            {
                return null;
            }

            byte[] hash = new byte[size];

            if (!PInvoke.CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hash))
            {
                return null;
            }

            // Every catalogue that lists the hash is tried, not only the first:
            // an old or superseded one can fail where a current one holds.
            SignatureInfo? best = null;
            nint catalog = 0;

            for (int tried = 0; tried < MostCatalogs; tried++)
            {
                // Passing the previous context moves to the next catalogue and
                // frees the previous one; none left gives zero.
                nint previous = catalog;

                fixed (byte* bytes = hash)
                {
                    catalog = PInvoke.CryptCATAdminEnumCatalogFromHash(admin, bytes, (uint)hash.Length, 0, previous == 0 ? null : &previous);
                }

                if (catalog == 0)
                {
                    return best;
                }

                SignatureInfo? answer = CheckCatalog(admin, catalog, path, file, hash);

                if (answer is { State: SignatureState.Trusted })
                {
                    PInvoke.CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                    return answer;
                }

                best = Better(best, answer);
            }

            PInvoke.CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
            return best;
        }
        finally
        {
            PInvoke.CryptCATAdminReleaseContext(admin, 0);
        }
    }

    /// <summary>Of two failed answers, the one that says most: not confirmed offline over broken.</summary>
    private static SignatureInfo? Better(SignatureInfo? kept, SignatureInfo? next) =>
        kept is null ? next
        : next is null ? kept
        : next.State == SignatureState.Unconfirmed && kept.State != SignatureState.Unconfirmed ? next
        : kept;

    /// <summary>The file's signature through one catalogue, or null when that catalogue cannot be read.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static SignatureInfo? CheckCatalog(nint admin, nint catalog, string path, SafeFileHandle file, byte[] hash)
    {
                CATALOG_INFO found = new() { cbStruct = (uint)sizeof(CATALOG_INFO) };

                if (!PInvoke.CryptCATCatalogInfoFromContext(catalog, ref found, 0))
                {
                    return null;
                }

                string catalogPath = found.wszCatalogFile.ToString();
                string tag = Convert.ToHexString(hash);

                fixed (char* catalogText = catalogPath)
                fixed (char* tagText = tag)
                fixed (char* member = path)
                fixed (byte* hashBytes = hash)
                {
                    WINTRUST_CATALOG_INFO info = new()
                    {
                        cbStruct = (uint)sizeof(WINTRUST_CATALOG_INFO),
                        pcwszCatalogFilePath = catalogText,
                        pcwszMemberTag = tagText,
                        pcwszMemberFilePath = member,
                        hMemberFile = (HANDLE)file.DangerousGetHandle(),
                        pbCalculatedFileHash = hashBytes,
                        cbCalculatedFileHash = (uint)hash.Length,
                        hCatAdmin = admin,
                    };

                    WINTRUST_DATA data = Request(WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_CATALOG);
                    data.pCatalog = &info;

                    return Check(&data, SignatureSource.Catalog);
                }
    }

    private static WINTRUST_DATA Request(WINTRUST_DATA_UNION_CHOICE choice) => new()
    {
        cbStruct = (uint)sizeof(WINTRUST_DATA),
        dwUIChoice = WINTRUST_DATA_UICHOICE.WTD_UI_NONE,
        fdwRevocationChecks = WINTRUST_DATA_REVOCATION_CHECKS.WTD_REVOKE_NONE,
        dwUnionChoice = choice,
        dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_VERIFY,
        dwProvFlags = WINTRUST_DATA_PROVIDER_FLAGS.WTD_REVOCATION_CHECK_NONE
            | WINTRUST_DATA_PROVIDER_FLAGS.WTD_CACHE_ONLY_URL_RETRIEVAL,
    };

    /// <summary>Runs one verification, reads the signer while the state is open, and always closes it.</summary>
    private static SignatureInfo Check(WINTRUST_DATA* data, SignatureSource source)
    {
        Guid action = GenericVerify;
        HWND noWindow = (HWND)(nint)(-1);

        int result = PInvoke.WinVerifyTrust(noWindow, ref action, data);

        try
        {
            if (result == 0)
            {
                return new SignatureInfo(SignatureState.Trusted, Signer(data->hWVTStateData), source, 0);
            }

            return result switch
            {
                NoSignature or SubjectFormUnknown or ProviderUnknown => new SignatureInfo(SignatureState.NoSignature, null, null, result),
                FileError => SignatureInfo.Unreadable(result),

                // Offline by design (cache-only retrieval), a chain Windows
                // could not finish from what it holds is not a broken one.
                UntrustedRoot or Chaining or RevocationOffline => new SignatureInfo(SignatureState.Unconfirmed, Signer(data->hWVTStateData), source, result),
                _ => new SignatureInfo(SignatureState.NotTrusted, null, null, result),
            };
        }
        finally
        {
            data->dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_CLOSE;
            _ = PInvoke.WinVerifyTrust(noWindow, ref action, data);
        }
    }

    /// <summary>The first signer's name, read from the verification's own state.</summary>
    private static string? Signer(HANDLE state)
    {
        if (state.IsNull)
        {
            return null;
        }

        CRYPT_PROVIDER_DATA_unmanaged* provider = PInvoke.WTHelperProvDataFromStateData((HANDLE)state);

        if (provider is null)
        {
            return null;
        }

        // CRYPT_PROVIDER_DATA holds only pointers and plain values, so the
        // managed and unmanaged forms share one layout.
        CRYPT_PROVIDER_SGNR* signer = PInvoke.WTHelperGetProvSignerFromChain(ref *(CRYPT_PROVIDER_DATA*)provider, 0, false, 0);

        if (signer is null)
        {
            return null;
        }

        CRYPT_PROVIDER_CERT* certificate = PInvoke.WTHelperGetProvCertFromChain(signer, 0);

        if (certificate is null || certificate->pCert is null)
        {
            return null;
        }

        using X509Certificate2 copy = new((nint)certificate->pCert);
        string name = copy.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
