using System.CommandLine;
using System.CommandLine.Help;
using Windsock.Cli;

// windsock: Windsock's command line. Every command reads; none changes
// anything in Windsock. `wake` is the one that sends something, and only to
// the home network, because it was asked to.

Option<bool> json = new("--json", "-j") { Description = "Print JSON instead of text." };
Option<string?> period = new("--period", "-p") { Description = "A named period: today, yesterday, week, last-week, month, last-month or year." };
Option<string?> month = new("--month", "-m") { Description = "A month, such as 2026-09." };
Option<string?> day = new("--day", "-d") { Description = "A day, such as 2026-09-14." };
Option<string?> from = new("--from") { Description = "The first day (2026-09-01) or moment (ISO 8601) of a period; give --until too." };
Option<string?> until = new("--until") { Description = "The last day of a period, included, or the moment it ends." };
Option<string?> by = new("--by") { Description = "Break the period up by minute, hour or day. Chosen from its length when left out." };
Option<string> format = new("--format", "-f") { Description = "csv or json.", DefaultValueFactory = _ => "csv" };
Option<FileInfo?> output = new("--output", "-o") { Description = "Write to this file instead of the console." };
Option<int> count = new("--count", "-n") { Description = "Stop after this many readings; 0 follows until Ctrl+C.", DefaultValueFactory = _ => 0 };
Option<bool> showToken = new("--show-token") { Description = "Also print the feed's token. Anyone with it can read the feed." };
Argument<string> device = new("device") { Description = "The device's hardware address (AA-BB-CC-DD-EE-FF) or its name on the device list." };

format.AcceptOnlyFromAmong("csv", "json");
count.Validators.Add(result =>
{
    if (result.GetValueOrDefault<int>() < 0)
    {
        result.AddError("--count cannot be negative.");
    }
});

Command status = new("status", "What Windsock shows now: rates, connection, today's and this month's use, the plan, the machine. Needs Windsock running with its local feed on.") { json };
status.SetAction((result, cancel) => Commands.StatusAsync(result.GetValue(json), cancel));

Command watch = new("watch", "Follow the rates and ping live, a line a second. Needs Windsock running with its local feed on.") { json, count };
watch.SetAction((result, cancel) => Commands.WatchAsync(result.GetValue(json), result.GetValue(count), cancel));

Command usage = new("usage", "How much was used in a period, read from the history file. Works whether Windsock is running or not.") { period, month, day, from, until, json };
usage.SetAction(result => Commands.Usage(
    Commands.Period(result.GetValue(period), result.GetValue(month), result.GetValue(day), result.GetValue(from), result.GetValue(until), "day"),
    result.GetValue(json)));

Command export = new("export", "Write a period's recorded use as CSV or JSON, read from the history file. Works whether Windsock is running or not.") { period, month, day, from, until, by, format, output };
export.SetAction(result => Commands.Export(
    Commands.Period(result.GetValue(period), result.GetValue(month), result.GetValue(day), result.GetValue(from), result.GetValue(until), result.GetValue(by)),
    result.GetValue(format) ?? "csv",
    result.GetValue(output)));

Command devices = new("devices", "The devices seen on your home networks, read from Windsock's list.") { json };
devices.SetAction(result => Commands.Devices(result.GetValue(json)));

Command wake = new("wake", "Send a Wake-on-LAN packet to a device on this home network. Sent only on a network Windows calls private, never through a VPN.") { device };
wake.SetAction((result, cancel) => Commands.WakeAsync(result.GetValue(device) ?? string.Empty, cancel));

Command feed = new("feed", "Where the local feed listens, whether it is running, and how to ask it.") { showToken };
feed.SetAction((result, cancel) => Commands.FeedAsync(result.GetValue(showToken), cancel));

// A plain command named "windsock" rather than a RootCommand, which names
// itself after the program's file (Windsock.Cli) in every help screen; it
// gets the root's help and version options by hand.
Command root = new("windsock", "Windsock's command line: status, live rates, usage, exports and home devices, for scripts.")
{
    status,
    watch,
    usage,
    export,
    devices,
    wake,
    feed,
};

root.Options.Add(new HelpOption());
root.Options.Add(new VersionOption());

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
