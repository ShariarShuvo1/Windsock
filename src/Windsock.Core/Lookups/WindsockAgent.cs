using System.Net;
using System.Reflection;

namespace Windsock.Core.Lookups;

/// <summary>How Windsock introduces itself to the services it contacts on request.</summary>
public static class WindsockAgent
{
    /// <summary>The project's home, given in every User-Agent so an operator can see what is calling.</summary>
    public const string Home = "https://github.com/ShariarShuvo1/Windsock";

    /// <summary>"Windsock/0.2.0 (+https://github.com/ShariarShuvo1/Windsock)".</summary>
    public static string UserAgent { get; } = string.Concat("Windsock/", Version(), " (+", Home, ")");

    /// <summary>
    /// An HTTP client for one outside service: no cookies, no redirects
    /// followed (a redirect would be a contact the person was not told of),
    /// a short timeout, and the honest User-Agent.
    /// </summary>
    public static HttpClient Client(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        HttpMessageHandler used = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        HttpClient client = new(used, disposeHandler: handler is null) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static string Version()
    {
        string? informational = typeof(WindsockAgent).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational))
        {
            return typeof(WindsockAgent).Assembly.GetName().Version?.ToString(3) ?? "0";
        }

        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
