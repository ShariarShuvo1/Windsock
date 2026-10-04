<div align="center">

<img src="assets/windsock.png" alt="Windsock" width="112" height="112">

# Windsock

Network speed meter for the Windows 11 taskbar.

[![Build](https://github.com/ShariarShuvo1/Windsock/actions/workflows/build.yml/badge.svg)](https://github.com/ShariarShuvo1/Windsock/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/ShariarShuvo1/Windsock?display_name=tag&sort=semver)](https://github.com/ShariarShuvo1/Windsock/releases/latest)
[![Licence](https://img.shields.io/badge/licence-MIT-blue)](LICENSE)

<a href="https://apps.microsoft.com/detail/9NWQF5ZF6DQK">
  <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Get it from Microsoft" height="52">
</a>
&nbsp;
<a href="https://github.com/ShariarShuvo1/Windsock/releases/latest">
  <img src="https://img.shields.io/badge/Download-for%20Windows-2DC1FC?style=for-the-badge&logo=github&logoColor=white" alt="Download" height="52">
</a>

<br>

<img src="assets/screenshots/taskbar.png" alt="The Windsock meter in the Windows 11 taskbar">

<sub>Live rates and temperatures, in the taskbar itself.</sub>

</div>

---

Live upload and download rates on the taskbar itself, a table of which programs
are using the network, how much each app has used, a map of where the traffic
goes, and per-minute history you can chart and export.

<img src="assets/screenshots/world-map.png" alt="Traffic drawn on a world map">

## Features

**Taskbar meter.** Up to six slots on the Windows 11 taskbar, either docked
inside it or floating above it. Each slot shows network rates, processor,
graphics, memory or storage readings, with independent units and colour.

**Live view.** Download and upload against a rolling 30 minute chart, with the
peak and average across it and what has moved today. Zoom the time axis with the
wheel, hover for values, and pause the chart to read it while measuring carries
on.

**History.** Every minute is recorded to a local SQLite database and kept for
good at full resolution. Query any period by minute, hour or day. Point at a bar
to see which apps made it up: measured by Windsock itself while the full edition
runs as administrator, and from Windows' own count otherwise, each in its own
shade. Under the usage chart the processor, graphics, memory, disk and the
connection's response time and outages run on the same time axis, so a busy
minute, a hot processor and a dropped connection line up. Export and import CSV.
Time Windsock was closed is filled in, drawn fainter, from what Windows itself
counted, for as far back as Windows keeps it. Minute detail of each app can be
folded into quarter hours after a chosen time, in Settings, if disk is short.

**Apps.** Every app on the network, live: its rate, its open sockets and the
remote address of each connection, named from the DNS cache, under a map of where
the traffic goes. An app is what Windows counts - every process running one
program, or one Store package - so without administrator rights, and always in
the Store edition, each app's rate is Windows' own count over the last five
minutes. Run as administrator, the full edition measures every second and can
show each process instead. Loopback connections are resolved to the app at the
other end instead of showing `127.0.0.1`, and never counted as network use.

Pick a period instead of Live and the same page totals each app over the last
hour, a day, a week or a month: the figures Settings shows under Data usage, kept
in Windsock's history so they last, covering every network the PC has used and
time Windsock was not running.

**Connection.** Says plainly whether the internet is there and, when it is not,
whether the router, the internet provider or a sign-in page is the likely cause.
Notifies when it drops and comes back, logs every outage with CSV export, and
charts router and internet response times over the last hour. The taskbar meter
shows a dash while the internet is gone.

**Network map.** Every remote address the machine is talking to, drawn on a
world map using an offline geolocation database, with this PC in the middle. An
app talking to hundreds of places, such as a torrent client, shows its busiest
and gathers the rest into one node. Ask for routes and the full edition traces
the way to each place with ICMP and merges shared hops, so the graph shows which
branch of the network carries the traffic.

**System.** Network, processor, graphics, memory and storage each have an entry
in the sidebar with their live figure beside it, and a page of their own: a chart
of the last few minutes, then per-core bars, adapters, drives and memory modules. Graphics temperatures for NVIDIA, AMD and Intel adapters.

<table>
<tr>
<td width="50%"><img src="assets/screenshots/system.png" alt="System tab"><br><sub><b>System</b>: processor, graphics, memory and storage, with per-core bars and temperatures.</sub></td>
<td width="50%"><img src="assets/screenshots/processes.png" alt="Apps page"><br><sub><b>Apps</b>: rates and sockets for every app, over a graph of where the traffic goes.</sub></td>
</tr>
<tr>
<td width="50%"><img src="assets/screenshots/history.png" alt="History tab"><br><sub><b>History</b>: any period by minute, hour or day. Gaps are drawn as gaps, not zeroes.</sub></td>
<td width="50%"><img src="assets/screenshots/process-window.png" alt="Per-process window"><br><sub><b>One app</b>: double-click a row for its own rates, sockets and routes.</sub></td>
</tr>
<tr>
<td colspan="2"><img src="assets/screenshots/taskbar-settings.png" alt="Taskbar tab"><br><sub><b>Taskbar</b>: each slot picks its own reading, units, size, alignment and colour.</sub></td>
</tr>
</table>

## Install

| | Edition | Updates |
| --- | --- | --- |
| [Microsoft Store](https://apps.microsoft.com/detail/9NWQF5ZF6DQK) | Store | Automatic |
| [`Windsock-win-Setup.exe`](https://github.com/ShariarShuvo1/Windsock/releases/latest) | Full | Automatic |
| [`Windsock-win.msi`](https://github.com/ShariarShuvo1/Windsock/releases/latest) | Full | Automatic |
| [`Windsock-win-Portable.zip`](https://github.com/ShariarShuvo1/Windsock/releases/latest) | Full | Manual |

`Windsock-win-Setup.exe` installs for you alone and needs no administrator
rights. `Windsock-win.msi` installs the same thing through a wizard and is the
only one that can install for everyone on the machine, which does need an
administrator. Every download includes the .NET runtime.

The MSI takes `VELOPACK_INSTALLDIR` if you want it somewhere specific:

```powershell
msiexec /i Windsock-win.msi VELOPACK_INSTALLDIR="D:\Apps\Windsock"
```

### Editions

| | Full | Store |
| :-- | :--: | :--: |
| Live rates, history, taskbar meter, system readings | Yes | Yes |
| Connection watching, outage log and alerts | Yes | Yes |
| Apps: every app live, with its sockets and the network map | Yes | Yes |
| Apps: each app's usage over a period, as Windows counts it | Yes | Yes |
| Each app's rate over the last five minutes, from Windows | Yes | Yes |
| Rates measured each second, and a view of each process | As administrator | No |
| Route tracing on the network map, reverse DNS | Yes | No |
| Processor temperature | As administrator | No |
| Code signed | No | Yes, by Microsoft |

Live per-process rates and processor temperature need administrator rights,
which the Store edition never asks for. They are removed from the Store build
rather than disabled in it: neither the kernel trace behind the rates nor the
code that talks to PawnIO is in the package. Route tracing and reverse DNS are
left out of the Store edition, whatever its settings say, so that its only
packets are the connection checks.

Windsock installs no kernel driver and contains none.

### Verifying a download

Releases are built by GitHub Actions and carry a `SHA256SUMS.txt` plus a build
provenance attestation.

```powershell
Get-FileHash Windsock-win-Setup.exe -Algorithm SHA256
gh attestation verify Windsock-win-Setup.exe --repo ShariarShuvo1/Windsock
```

Windsock is not code signed, so Windows will warn that the publisher is
unrecognised. The Store build is signed by Microsoft.

## The taskbar meter

Windows 11 removed deskbands, so the meter is offered two ways:

- **Docked** makes the meter a child of Explorer's `Shell_TrayWnd` and places it
  against the notification area. It moves and hides with the taskbar. This
  relies on undocumented Explorer internals and a Windows update could break it.
- **Floating** is a top-level window held in the same position. Less tidy, but
  it cannot break.

The meter is a grid of up to two rows by three columns, off by default and
removed when Windsock closes.

| Group | Slots |
| --- | --- |
| Network | Download, upload, total |
| Processor | Usage, clock, [temperature](#processor-temperature) |
| Graphics | Usage, temperature, memory, power |
| Memory | Usage percentage, amount used |
| Storage | Activity, temperature, space used |
| System | Board temperature |

Units, labels, text size, alignment, click action and colour are configurable
per slot. An optional background can be painted behind the readings for taskbars
where the text would otherwise be illegible.

Board temperature comes from the firmware's thermal zone. It is not a processor
reading: on a desktop it may report the chipset, or a value that never changes.

## Processor temperature

Full edition only. Requires setup.

Processor temperature lives in a model-specific register that only kernel code
can read, and Windows exposes no API for it. Windsock ships no driver. It reads
through [PawnIO](https://pawnio.eu) if you install it yourself.

1. Install PawnIO from <https://pawnio.eu> and choose the official signed build.
2. Run Windsock as an administrator. PawnIO's device only admits administrators.

The reading then appears on the Processor page and as a
taskbar slot. Until then:

| Shown | Meaning |
| --- | --- |
| `PawnIO is not installed` | Step 1 not done |
| `Needs elevation` | Step 2 not done |
| `This processor is not supported` | Intel and AMD Zen or later are supported |

Most tools read this register through WinRing0, which grants any caller
arbitrary kernel register access. It is listed on Microsoft's vulnerable driver
blocklist as CVE-2020-14979 and will not load on Windows 11 with memory
integrity enabled. PawnIO loads signed sandboxed modules instead, and Windsock's
module can read about thirty named registers and nothing else.

## Privacy

Windsock has no accounts, no telemetry and no analytics. Nothing is sent to the
author, and there is no server behind the application.

Data is written to `%LOCALAPPDATA%\Shariar Shuvo\Windsock`:

```
Windsock.db        usage history, one row per minute: the network, each app, the machine, the connection
Outages.db         the outage log
settings.json      settings
logs\              rolling logs, seven days
```

Per-app usage is read from Windows, which already counts it, and addresses are
named from the DNS client's cache; neither sends anything. Both editions ping
your own router every few seconds to watch the connection, which never leaves
your local network, and ping a public address only if you choose one in the
Connection section. Background watching can be turned off. The full edition also
performs reverse DNS lookups for addresses the cache cannot name, ICMP route
traces when a graph requires one, and update checks. Route traces and update
checks can be turned off in Settings. Reverse DNS happens only while the network
map or an app's own window is naming addresses, and each has a names switch
that stops it there. Geolocation is resolved offline
from a database shipped with the application.

See [PRIVACY.md](PRIVACY.md).

## Building

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
10.0.400 or later on Windows 10 1809 or later.

```bash
dotnet build
dotnet run --project src/Windsock.App

dotnet build Windsock.slnx -c Release                       # full edition
dotnet build Windsock.slnx -c Release -p:StoreBuild=true    # Store edition
dotnet test  Windsock.slnx -c Release
```

Both editions are built and tested on every push.

### Layout

```
src/Windsock.App     WPF application
src/Windsock.Core    Headless logic, no UI dependencies
packaging/msix       Microsoft Store package manifest and tile images
tests/               Unit tests over Windsock.Core
```

<details>
<summary>Implementation notes</summary>

<br>

**Interface counters** are read through the IP Helper `GetIfTable2` API rather
than `NetworkInterface.GetAllNetworkInterfaces()`. On the development machine
the managed call cost 5.3 ms and 58 KB of allocation per read against 0.36 ms
and no allocation for the native path.

**Collection stops when nothing is watching.** Walking every TCP and UDP
connection once a second, and reading the machine's performance counters, both
stop when no view needs them. Because the underlying counters keep running, the
first reading after a view reopens is discarded rather than shown.

**The chart downsamples with max-pooling** rather than averaging, which would
flatten the bursts the chart exists to show. Its vertical scale follows the
visible range.

**Gaps in history are drawn as gaps, not zeroes.** A period with no traffic and
a period with Windsock not running are different facts.

**At launch the chart is seeded** with a minute of backdated zero samples, so the
axes and zoom range work from the first frame.

**Colours** come from the icon: cyan `#2DC1FC` at hue 197 for download, its
counterpoint at hue 36 for upload. The pair stays distinguishable under common
forms of colour blindness. `Styles/Palette.Light.xaml` and `Palette.Dark.xaml`
define the same keys at different lightness and `Theming/ThemeManager` swaps
them with the system theme.

Icons are Microsoft Fluent System Icons via `FluentIcons.Wpf`.

</details>

## Licence

MIT. Copyright (c) 2026 Shariar Shuvo. See [LICENSE](LICENSE).

| Component | Licence |
| --- | --- |
| DB-IP City Lite geolocation data | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |
| Natural Earth country outlines | Public domain |
| PawnIO modules | LGPL-2.1 |

Attribution for DB-IP and Natural Earth is shown in the application under the
world map. Full notices are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

<div align="center">

[Issues](https://github.com/ShariarShuvo1/Windsock/issues) ·
[Releases](https://github.com/ShariarShuvo1/Windsock/releases) ·
[Privacy](PRIVACY.md)

Built by [Shariar Shuvo](https://github.com/ShariarShuvo1)

</div>
