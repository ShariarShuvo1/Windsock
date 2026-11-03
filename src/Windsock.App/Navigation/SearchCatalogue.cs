using Windsock.Core.Navigation;

namespace Windsock.App.Navigation;

/// <summary>
/// The settings and readings the search box can find, one line each.
/// </summary>
/// <remarks>
/// <para>
/// Pages need no line here: every page in the sidebar is found by its own
/// name, and by the words in its <c>Terms</c> in <c>MainWindow.Pages</c>.
/// This list is for what is on a page. A line names what it is called on the
/// page, the page's link name, and other words somebody might type:
/// </para>
/// <code>Setting("Data limit", "plan", "cap allowance quota"),</code>
/// <para>
/// The search takes the reader to the page and then brings into view, and
/// focuses, the control whose automation name (or <c>x:Name</c>) is the
/// title - pass <c>target</c> when they differ. Where on the page it is comes
/// from the sidebar, so it is never written here. A line for something the
/// Store edition is built without goes inside <c>#if !STORE</c>.
/// </para>
/// </remarks>
internal static class SearchCatalogue
{
    /// <summary>
    /// Other words a page answers to, besides its name in the sidebar, by the
    /// page's link name. A page added later adds its own line here.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PageTerms { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["taskbar"] = "meter taskbar slots readings",
        ["overview"] = "network speed bandwidth throughput chart download upload",
        ["apps"] = "programs processes per-app usage sockets",
        ["map"] = "world connections countries places graph",
        ["adapters"] = "wifi wi-fi wireless ethernet adapter interface ip address dns vpn metered signal",
        ["activity"] = "listening ports open server firewall blocked timeline connections lookups dns countries",
        ["devices"] = "home network lan devices router neighbours wake wol upnp ports",
        ["status"] = "connection online offline internet ping router",
        ["outages"] = "connection drops disconnections downtime log",
        ["speedtest"] = "speed test bandwidth mbps m-lab ndt librespeed bufferbloat latency under load",
        ["history"] = "usage past minutes records chart",
        ["plan"] = "data limit cap allowance billing",
        ["reports"] = "report summary month week year compare calendar heatmap lifetime digest",
        ["processor"] = "cpu load cores temperature utility performance efficiency watts power",
        ["graphics"] = "gpu video card npu neural ai accelerator",
        ["memory"] = "ram processes",
        ["storage"] = "disk drives ssd file activity",
        ["battery"] = "power charge laptop ups health cycles energy saver power mode",
        ["alerts"] = "notifications notify toast",
        ["general"] = "settings window startup",
        ["appearance"] = "settings look theme accent density",
        ["privacy"] = "settings data privacy history outside contacts",
        ["advanced"] = "settings administrator export import",
        ["about"] = "settings version updates licence",
    };

    /// <summary>Everything on a page that can be searched for.</summary>
    public static IReadOnlyList<SearchEntry> Entries { get; } =
    [
        // ---------------------------------------------------------------- Taskbar
        Setting("Show the meter", "taskbar", "meter taskbar on off enable speed"),
        Setting("Position", "taskbar", "edge top left right bottom floating dock"),
        Setting("Which taskbars", "taskbar", "monitor display screen second every"),
        Setting("Full-screen apps", "taskbar", "games video hide fullscreen"),
        Setting("Remote Desktop", "taskbar", "rdp remote session"),
        Setting("Keep a steady width", "taskbar", "width digits jumping steady"),
        Setting("Soft outline", "taskbar", "shadow legible outline"),
        Setting("Background", "taskbar", "colour color transparent backdrop", target: "Choose a background"),
        Setting("Lock the floating meter", "taskbar", "drag move lock"),
        Setting("Readings on the bar", "taskbar", "slots cells ping latency graph sparkline levels colours rotate cycle turns ip address json feed gpu card battery today month total plan windsock"),
        Setting("Layouts", "taskbar", "presets gamer laptop compact network template one click"),
        Setting("Save layout", "taskbar", "share export layout file", target: "Save the layout to a file"),
        Setting("Load layout", "taskbar", "share import layout file", target: "Load a layout from a file"),
        Setting("Click", "taskbar", "click action open", target: "On click"),
        Setting("Double-click", "taskbar", "double click action", target: "On double-click"),
        Setting("Middle-click", "taskbar", "middle click wheel action", target: "On middle-click"),
        Setting("Keyboard shortcuts", "taskbar", "hotkeys hotkey global keys shortcut", target: "Shortcut to show the window"),
        Setting("Speeds in the tray icon", "taskbar", "tray notification area icon figures"),
        Setting("Compact window", "taskbar", "mini small always on top overlay floating", target: "Open or close the compact window"),

        // ---------------------------------------------------------------- Network
        Reading("Download rate", "overview", "speed down receive incoming"),
        Reading("Upload rate", "overview", "speed up send outgoing"),
        Setting("Bytes or bits", "overview", "units mbps megabits megabytes", target: "Unit family"),
        Setting("Chart scale", "overview", "units scale kilo mega giga", target: "Unit scale"),
        Reading("Network adapters", "adapters", "ethernet wifi wi-fi adapter interface ip address mac dhcp", target: "Adapters"),
        Setting("Show every adapter", "adapters", "virtual hidden down disconnected adapters"),
        Setting("Copy details", "adapters", "support helpdesk clipboard diagnostics paste"),
        Reading("VPN", "adapters", "vpn tunnel wireguard openvpn"),
        Reading("Metered", "adapters", "metered data limit cost windows"),
        Reading("Wi-Fi signal", "adapters", "wifi wireless rssi dbm bars strength", target: "Wi-Fi signal over the last five minutes"),
        Setting("Scan for nearby networks", "adapters", "wifi channels crowding neighbours interference congestion"),
        Reading("Wi-Fi drops", "adapters", "wifi disconnections dropped wireless log reasons"),
        Setting("Search apps", "apps", "find filter program process"),
        Setting("Columns", "apps", "show hide columns pid packets", target: "Choose columns"),
        Setting("Period", "apps", "today week month total"),
#if !STORE
        Setting("Apps or processes", "apps", "process pid", target: "Show apps or processes"),
        Setting("Routes", "map", "traceroute hops path trace"),
#endif
        Setting("World map", "map", "countries cities geography"),
        Setting("One dot per city", "map", "cities cluster"),
        Setting("Loopback", "map", "localhost 127.0.0.1 local"),
        Reading("Waiting for connections", "activity", "listening ports open listeners server", target: "ListeningBand"),
        Setting("Only what other machines can reach", "activity", "listening open ports network reachable"),
        Reading("Publisher", "apps", "signed signature unsigned certificate publisher", target: "Choose columns"),
        Reading("Windows Update and Delivery Optimization this month", "apps", "windows update delivery optimization updates", target: "UpdateBand"),
        Setting("Check the router", "devices", "upnp ports router model maker forwarding"),

        // ------------------------------------------------------------- Connection
        Reading("Response time", "status", "ping latency delay milliseconds ms", target: "Response time over the last hour"),
        Reading("Router", "status", "gateway local network ping"),
        Reading("Internet", "status", "online offline reachable"),
        Reading("Lost pings", "status", "packet loss dropped"),
        Setting("How to check the internet", "status", "ping target cloudflare check"),
        Setting("Address to ping", "status", "ping target ip host server"),
        Setting("Watch the connection in the background", "status", "monitor background outage"),
        Setting("How often to check", "status", "interval seconds frequency ping"),
        Setting("When to call it an outage", "status", "outage threshold seconds"),
        Reading("Verdict for calls", "status", "calls video voice quality grade teams zoom"),
        Reading("Verdict for games", "status", "games gaming quality grade"),
        Reading("Jitter", "status", "jitter variation stability"),
        Reading("IPv6", "status", "ipv6 v6 address"),
        Reading("Carrier-grade NAT", "status", "cgnat nat shared address port forwarding"),
        Setting("Allow looking up the public address", "status", "public ip what is my ip wan external provider asn"),
        Setting("Allow asking a STUN server", "status", "nat type stun open strict"),
        Setting("Allow timing the DNS servers", "status", "dns speed lookup time resolver"),
        Setting("Save a report for your provider", "status", "isp complaint report evidence outages"),
        Setting("Run a speed test", "speedtest", "test speed now bandwidth"),
        Setting("Test against", "speedtest", "server m-lab measurement lab librespeed choose"),
        Setting("Agree to M-Lab publishing my tests", "speedtest", "m-lab measurement lab consent publish public address", target: "Agree to M-Lab publishing my tests with my public address"),
        Setting("Run speed tests automatically", "speedtest", "schedule automatic daily scheduled"),
        Reading("Speed tests", "speedtest", "history results past tests trend"),
        Setting("Plan speeds", "speedtest", "plan isp paid mbps compare getting what i pay for", target: "Plan download speed in megabits a second"),
        Setting("Export outages", "outages", "csv save file outage log"),
        Setting("Clear the outage log", "outages", "erase delete clear outages"),

        // ------------------------------------------------------------------ Usage
        Setting("Export history", "history", "csv json save file", target: "Export"),
        Setting("Import history", "history", "csv load restore", target: "Import"),
        Setting("Resolution", "history", "minute hour day week month"),
        Setting("Period", "history", "range today yesterday week month year"),
        Reading("Usage over the period", "history", "chart bars downloaded uploaded total"),
        Reading("The machine over the period", "history", "processor graphics memory disk ping lanes"),
        Setting("Measure a stretch", "history", "measure drag select total between"),
        Setting("Add a note", "history", "note annotate comment label"),
        Setting("Time a download", "history", "stopwatch timer download how long"),
        Reading("Networks, events and notes over the period", "history", "network wifi outages alerts notes lanes"),
        Setting("Add a plan", "plan", "data plan limit cap allowance new", target: "Add a data plan"),
        Reading("Your plans", "plan", "plans cycle left forecast"),
        Setting("Networks", "plan", "hotspot phone tethering network name"),
        Setting("Compare periods", "reports", "compare last month last year before", target: "Report views"),
        Reading("Year calendar", "reports", "calendar heatmap days year", target: "Report views"),
        Reading("Week by hour", "reports", "heatmap hours week busiest", target: "Report views"),
        Setting("Save the month as a picture", "reports", "share image png card picture"),
        Setting("Internet only", "reports", "lan local network nas exclude", target: "Leave out what stayed on the local network"),

        // ----------------------------------------------------------------- System
        Reading("Logical processors", "processor", "cores threads cpu load"),
        Reading("Display adapters", "graphics", "gpu graphics card video temperature"),
        Reading("Memory modules", "memory", "ram dimm slots speed"),
        Reading("Drives", "storage", "disk ssd hdd nvme"),
        Reading("Busiest processes", "processor", "top processes cpu task manager programs"),
        Reading("Busiest by file activity", "storage", "disk io read write processes programs"),
        Reading("Utility", "processor", "processor utility task manager boost turbo frequency"),
        Reading("Performance cores", "processor", "p-cores e-cores efficiency hybrid big little"),
        Reading("History chart", "processor", "last day week past load temperature"),
        Reading("Efficiency cores", "processor", "e-cores p-cores hybrid little"),
        Reading("Power mode", "battery", "power plan balanced best performance efficiency energy saver battery saver"),
        Reading("Battery health", "battery", "wear capacity design full charge"),
        Reading("Charge cycles", "battery", "cycle count wear"),
        Reading("Time left", "battery", "remaining runtime to full charging"),
        Setting("Power settings", "battery", "windows power and battery sleep", target: "Open Windows' power settings"),

        // ----------------------------------------------------------------- Alerts
        Setting("Show notifications", "alerts", "notify toast notification"),
        Setting("Play a sound", "alerts", "sound chime notification"),
        Setting("Send a test notification", "alerts", "test notify toast"),
        Setting("Notify when the internet drops and comes back", "alerts", "outage offline online alert lost restored", target: "Alert: Internet lost and back"),
        Setting("Notify when the network or VPN changes", "alerts", "vpn wifi network change alert", target: "Alert: Network or VPN changed"),
        Setting("Windows notification settings", "alerts", "focus do not disturb", target: "Open Windows notification settings"),
        Reading("Recent alerts", "alerts", "alerts sent missed history log list unread"),
        Setting("Keep alerts for", "alerts", "retention days keep alerts list"),
        Setting("Quiet hours", "alerts", "night do not disturb mute overnight sleep hold"),
        Setting("Snooze alerts", "alerts", "snooze mute pause hour morning", target: "Snooze alerts for an hour"),
        Setting("Forward alerts", "alerts", "ntfy webhook phone push forward slack discord home assistant"),
        Setting("Slow responses or lost pings", "alerts", "ping latency packet loss alert", target: "Alert: Slow responses or lost pings"),
        Setting("Download slows down", "alerts", "speed drop slow download alert", target: "Alert: Download slows down"),
        Setting("Unusual upload", "alerts", "upload backup sync runaway alert", target: "Alert: Unusual upload"),
        Setting("Data plan levels", "alerts", "plan cap allowance 80% 100% alert", target: "Alert: Data plan levels"),
        Setting("Graphics too hot", "alerts", "gpu temperature hot alert", target: "Alert: Graphics too hot"),
        Setting("Drive too hot", "alerts", "disk ssd temperature hot alert", target: "Alert: Drive too hot"),
        Setting("Drive nearly full", "alerts", "disk space full storage alert", target: "Alert: Drive nearly full"),
        Setting("Drive wearing out", "alerts", "ssd nvme wear life health alert", target: "Alert: Drive wearing out"),
        Setting("Address changed", "alerts", "ip address dhcp alert", target: "Alert: Address changed"),
        Setting("DNS servers changed", "alerts", "dns leak hijack vpn alert", target: "Alert: DNS servers changed"),
        Setting("Router changed", "alerts", "router mac spoofing rogue hotspot evil twin alert", target: "Alert: Router changed"),
        Setting("Proxy changed", "alerts", "proxy pac wpad alert", target: "Alert: Proxy changed"),
        Setting("While you were away", "alerts", "lock unlock away summary", target: "Alert: While you were away"),
        Setting("Forwarding alerts", "privacy", "ntfy webhook forward phone push", target: "Forward alerts"),
        Setting("Meter feeds", "privacy", "json feed web address taskbar meter"),
        Setting("Public address on the meter", "privacy", "vpn ip address taskbar meter current", target: "Keep the meter's public address current"),

        // --------------------------------------------------------------- Settings
        Setting("When the window is closed", "general", "quit close tray background keep running"),
        Setting("Start with Windows", "general", "login startup boot sign in autostart"),
        Setting("Open the window on", "general", "start page remember last landing"),
        Setting("Adapter", "general", "measure ethernet wifi interface"),
        Setting("Theme", "appearance", "dark light mode colours colors"),
        Setting("Use the Windows accent colour", "appearance", "accent colour color highlight"),
        Setting("Density", "appearance", "compact spacing rows tight dense"),
        Setting("Temperatures", "appearance", "celsius fahrenheit degrees units scale"),
        Setting("Record history", "privacy", "pause recording history", target: "RecordHistoryButton"),
        Setting("Keep minute detail", "privacy", "retention compact minutes keep"),
        Setting("Reclaim space", "privacy", "vacuum shrink disk space size"),
        Setting("Delete history", "privacy", "erase clear wipe remove", target: "Delete the history"),
        Setting("Keep copies", "privacy", "backup copy restore weekly", target: "Keep a weekly copy of the history file"),
        Setting("Export on a schedule", "privacy", "automatic export daily monthly folder csv json"),
        Setting("Weekly digest", "privacy", "summary weekly notification report"),
        Reading("History file", "privacy", "database file location newer version", target: "Show the history file in Explorer"),
        Setting("Ping an internet address", "privacy", "cloudflare internet check privacy"),
        Setting("Check for updates automatically", "privacy", "update version automatic"),
        Setting("Find devices on my home network", "privacy", "lan devices home network mdns netbios"),
        Setting("Router details", "privacy", "upnp router ports model maker"),
        Setting("Spamhaus DROP blocklist", "privacy", "blocklist block list malicious bad addresses spamhaus"),
        Setting("Feodo Tracker blocklist", "privacy", "blocklist botnet abuse.ch feodo"),
        Setting("VirusTotal lookups", "privacy", "virustotal reputation malware scan hash api key", target: "Add a VirusTotal key"),
        Setting("New app online", "alerts", "new program first time notify alert", target: "Alert: New app online"),
        Setting("Household traffic", "privacy", "router counters whole home family"),
        Setting("Public address lookup", "privacy", "public ip ipify icanhazip outside contacts"),
        Setting("Speed tests with M-Lab", "privacy", "m-lab measurement lab speed test consent publish"),
        Setting("Automatic speed tests", "privacy", "speed test schedule automatic"),
        Setting("NAT test", "privacy", "stun nat server"),
        Setting("DNS timing", "privacy", "dns timing root queries"),
        Setting("Export settings", "advanced", "backup move another pc file"),
        Setting("Import settings", "advanced", "restore load file"),
        Setting("Local feed", "advanced", "api json http rainmeter stream deck home assistant token"),
        Setting("Stream overlay", "advanced", "obs overlay streaming browser source", target: "Copy the overlay address"),
        Setting("Command line", "advanced", "cli terminal console windsock path powershell cmd"),
        Setting("AI assistants", "advanced", "mcp ai tools model context protocol", target: "Copy the MCP settings"),
        Setting("Check for updates now", "about", "update version new"),
        Setting("Privacy policy", "about", "privacy data policy", target: "Read the privacy policy"),
        Setting("Source code", "about", "github code licence", target: "Open the source code on GitHub"),
#if !STORE
        Setting("Reverse lookups", "privacy", "dns names hostnames resolver"),
        Setting("Route tracing", "privacy", "traceroute routes hops"),
        Setting("Hop by hop", "status", "mtr traceroute hops path loss per router", target: "Route tracing"),
        Setting("Clear the DNS cache", "adapters", "flush dns ipconfig fix"),
        Setting("Share as QR code", "adapters", "wifi password qr share join guest"),
        Setting("Keep a connection timeline", "activity", "timeline history connections where when record"),
        Reading("Countries and places", "activity", "countries places sites destinations per day", target: "PlacesBand"),
        Reading("Names looked up", "activity", "dns lookups names queries per app"),
        Setting("Remove all of Windsock's firewall rules", "activity", "firewall block blocked rules unblock remove uninstall"),
        Setting("Restart as administrator", "advanced", "elevate admin elevated per-process trace"),
        Setting("PawnIO", "advanced", "processor temperature cpu driver", target: "Open the PawnIO website"),
        Reading("Package power", "processor", "watts power rapl energy cpu"),
        Setting("Processor too hot", "alerts", "cpu temperature hot alert", target: "Alert: Processor too hot"),
        Setting("Update ready", "alerts", "update new version alert", target: "Alert: Update ready"),
#endif
    ];

    private static SearchEntry Setting(string title, string link, string terms, string? target = null) =>
        new(title, link) { Terms = terms, Target = target ?? title, Kind = SearchKind.Setting };

    private static SearchEntry Reading(string title, string link, string terms, string? target = null) =>
        new(title, link) { Terms = terms, Target = target ?? title, Kind = SearchKind.Reading };
}
