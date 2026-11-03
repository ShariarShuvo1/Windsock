#if !STORE
namespace Windsock.Core.Wifi;

/// <summary>Why a network cannot be shared as a QR code, or that it can.</summary>
public enum WifiShareOutcome
{
    /// <summary>The QR text is ready.</summary>
    Ready,

    /// <summary>Wi-Fi is not connected.</summary>
    NotConnected,

    /// <summary>Windows needs location permission to say which network this is.</summary>
    LocationNeeded,

    /// <summary>Windows kept the password encrypted; running as administrator lets it hand the password over.</summary>
    KeyWithheld,

    /// <summary>The network signs in with an account (802.1X), which a QR code cannot carry.</summary>
    Enterprise,

    /// <summary>Anything else.</summary>
    Failed,
}

/// <summary>The outcome of asking to share the current network.</summary>
/// <param name="Outcome">Whether it worked.</param>
/// <param name="Network">The network's name, when known.</param>
/// <param name="Payload">The text to encode as a QR code, only when <see cref="WifiShareOutcome.Ready"/>.</param>
public sealed record WifiShareResult(WifiShareOutcome Outcome, string? Network, string? Payload);

/// <summary>
/// Reads the connected network's saved password, on one explicit request, to
/// make the text of a QR code a phone can join with. Full edition only.
/// </summary>
/// <remarks>
/// The password is returned inside the QR text and kept nowhere: not in a
/// field, a log, a setting or the clipboard. Microsoft documents the plain key
/// as administrators only; on the development machine an unelevated process
/// was handed it anyway for an all-users profile, so the reader always asks
/// and then looks at the <c>protected</c> mark rather than assuming either way.
/// </remarks>
public sealed class WifiKeyReader(IWlanClient client)
{
    /// <summary>Makes the QR text for the network Wi-Fi is connected to now.</summary>
    public WifiShareResult ShareCurrent()
    {
        (WlanStatus status, IReadOnlyList<WlanInterface> adapters) = client.Interfaces();
        WlanInterface? adapter = adapters.FirstOrDefault(each => each.IsConnected);

        if (status != WlanStatus.Ok || adapter is null)
        {
            return new WifiShareResult(WifiShareOutcome.NotConnected, null, null);
        }

        (WlanStatus asked, byte[]? data) = client.Query(adapter.Id, WlanClient.CurrentConnectionOpcode);

        if (asked == WlanStatus.Denied)
        {
            return new WifiShareResult(WifiShareOutcome.LocationNeeded, null, null);
        }

        if (asked != WlanStatus.Ok || WifiDecoders.ConnectionAttributesOf(data) is not { ProfileName.Length: > 0 } association)
        {
            return new WifiShareResult(asked == WlanStatus.NotConnected ? WifiShareOutcome.NotConnected : WifiShareOutcome.Failed, null, null);
        }

        (WlanStatus read, string? xml) = client.Profile(adapter.Id, association.ProfileName);

        if (read == WlanStatus.Denied)
        {
            return new WifiShareResult(WifiShareOutcome.KeyWithheld, association.Name, null);
        }

        if (read != WlanStatus.Ok || WifiShare.ReadProfile(xml) is not { } profile)
        {
            return new WifiShareResult(WifiShareOutcome.Failed, association.Name, null);
        }

        if (profile.Security == WifiShareSecurity.Enterprise)
        {
            return new WifiShareResult(WifiShareOutcome.Enterprise, profile.Ssid, null);
        }

        if (profile.KeyProtected)
        {
            return new WifiShareResult(WifiShareOutcome.KeyWithheld, profile.Ssid, null);
        }

        return WifiShare.Payload(profile) is { } payload
            ? new WifiShareResult(WifiShareOutcome.Ready, profile.Ssid, payload)
            : new WifiShareResult(WifiShareOutcome.Failed, profile.Ssid, null);
    }
}
#endif
