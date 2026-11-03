using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography;

namespace Windsock.Core.Security;

/// <summary>
/// Keeps one secret the person gave Windsock - their VirusTotal key - in a
/// file only their Windows account can turn back into the key.
/// </summary>
/// <remarks>
/// Protected with DPAPI for the current user, so the file is useless copied to
/// another account or machine, and kept out of <c>settings.json</c>, so an
/// exported settings file never carries it. Clearing the key deletes the file.
/// </remarks>
public sealed unsafe class ApiKeyVault
{
    private const uint NoPrompt = 0x1;   // CRYPTPROTECT_UI_FORBIDDEN

    /// <summary>Ties the protection to this use, so another program's DPAPI blob is not taken for it.</summary>
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("Windsock VirusTotal key");

    private readonly string _path;

    public ApiKeyVault(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Whether a key is kept.</summary>
    public bool HasKey => File.Exists(_path);

    /// <summary>Keeps a key, replacing any before. False when it could not be protected or written.</summary>
    public bool Store(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!IsWellFormed(key))
        {
            return false;
        }

        byte[]? protectedBytes = Protect(Encoding.UTF8.GetBytes(key.Trim()));

        if (protectedBytes is null)
        {
            return false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(_path + ".tmp", protectedBytes);
            File.Move(_path + ".tmp", _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether text is shaped like a VirusTotal API key: 64 hexadecimal
    /// characters. Anything else is refused before it is kept, so nothing that
    /// could break a request header ever reaches one.
    /// </summary>
    public static bool IsWellFormed(string? key) =>
        key is not null && key.Trim() is { Length: 64 } trimmed && trimmed.All(char.IsAsciiHexDigit);

    /// <summary>The key, or null when none is kept or it cannot be unprotected here.</summary>
    public string? Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            byte[]? plain = Unprotect(File.ReadAllBytes(_path));
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Forgets the key.</summary>
    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static byte[]? Protect(byte[] plain) => Transform(plain, protect: true);

    private static byte[]? Unprotect(byte[] protectedBytes) => Transform(protectedBytes, protect: false);

    private static byte[]? Transform(byte[] input, bool protect)
    {
        if (input.Length == 0)
        {
            return null;
        }

        fixed (byte* data = input)
        fixed (byte* purpose = Purpose)
        {
            CRYPT_INTEGER_BLOB source = new() { cbData = (uint)input.Length, pbData = data };
            CRYPT_INTEGER_BLOB entropy = new() { cbData = (uint)Purpose.Length, pbData = purpose };
            CRYPT_INTEGER_BLOB result;

            bool done = protect
                ? PInvoke.CryptProtectData(source, null, entropy, null, NoPrompt, out result)
                : PInvoke.CryptUnprotectData(source, entropy, null, NoPrompt, out result);

            if (!done || result.pbData is null)
            {
                return null;
            }

            try
            {
                return new ReadOnlySpan<byte>(result.pbData, (int)result.cbData).ToArray();
            }
            finally
            {
                PInvoke.LocalFree(new HLOCAL(result.pbData));
            }
        }
    }
}
