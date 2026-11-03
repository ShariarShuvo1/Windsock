namespace Windsock.Core.Insight;

/// <summary>Where the step 7 features keep their files, all inside the data root.</summary>
public static class InsightPaths
{
    /// <summary>The apps Windsock has seen use the network.</summary>
    public static string SeenApps => Path.Combine(WindsockPaths.Root, "seen-apps.txt");

    /// <summary>The blocklists fetched, each with a note of when.</summary>
    public static string Blocklists => Path.Combine(WindsockPaths.Root, "blocklists");

    /// <summary>The person's VirusTotal key, protected for their Windows account.</summary>
    public static string VirusTotalKey => Path.Combine(WindsockPaths.Root, "virustotal.key");

    /// <summary>VirusTotal's answers, kept so a program is not looked up twice in a few days.</summary>
    public static string Reputation => Path.Combine(WindsockPaths.Root, "reputation.json");

    /// <summary>The connection timeline (full edition, administrator).</summary>
    public static string Timeline => Path.Combine(WindsockPaths.Root, "Timeline.db");
}
