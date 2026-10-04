namespace Windsock.Core.Connectivity;

/// <summary>Why the internet is not reachable.</summary>
public enum OutageCause
{
    /// <summary>It is gone, but whether the router or the provider is at fault cannot be told.</summary>
    Unknown,

    /// <summary>Not connected to any network.</summary>
    NoNetwork,

    /// <summary>On a network, but the router does not answer.</summary>
    Router,

    /// <summary>The router answers, but nothing beyond it does: usually the internet provider.</summary>
    Provider,

    /// <summary>The network wants somebody to sign in.</summary>
    SignIn,
}

/// <summary>
/// Decides from one check whether the internet is reachable, and if not, why.
/// </summary>
/// <remarks>
/// <para>
/// Direct evidence wins. An answer from the internet target means online, full
/// stop. Without a target, Windows' own verdict is trusted for "online", because
/// plenty of routers are set never to answer a ping and would otherwise look
/// broken all the time.
/// </para>
/// <para>
/// A router that stays silent only counts against it on a network where it has
/// answered before. Otherwise its silence says nothing, and the cause is given as
/// unknown rather than blamed on a router that may simply ignore pings.
/// </para>
/// </remarks>
public static class ConnectionJudge
{
    /// <summary>
    /// Nothing when the internet is reachable; otherwise the likeliest cause.
    /// </summary>
    /// <param name="sample">The check.</param>
    /// <param name="routerAnswersPings">Whether this network's router has answered a ping before.</param>
    public static OutageCause? Judge(in ConnectionSample sample, bool routerAnswersPings)
    {
        ConnectionFacts facts = sample.Facts;

        if (!facts.HasNetwork && facts.Reach is not (WindowsReach.Internet or WindowsReach.SignInRequired))
        {
            return facts.Reach == WindowsReach.Unknown ? null : OutageCause.NoNetwork;
        }

        if (sample.InternetChecked && sample.Internet is not null)
        {
            return null;
        }

        if (facts.Reach == WindowsReach.SignInRequired)
        {
            return OutageCause.SignIn;
        }

        if (!sample.InternetChecked)
        {
            if (facts.Reach == WindowsReach.Internet)
            {
                return null;
            }

            // Windows could not be asked and nothing else was: a silent router it
            // has heard from before is the only evidence left.
            if (facts.Reach == WindowsReach.Unknown)
            {
                return sample.RouterChecked && sample.Router is null && routerAnswersPings
                    ? OutageCause.Router
                    : null;
            }
        }

        if (sample.Router is not null)
        {
            return OutageCause.Provider;
        }

        return sample.RouterChecked && routerAnswersPings ? OutageCause.Router : OutageCause.Unknown;
    }
}
