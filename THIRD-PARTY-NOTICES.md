# Third-party notices

Windsock itself is MIT licensed (see [LICENSE](LICENSE)). It ships data files
and two small binaries, and depends on a handful of packages, each under its own
terms. This file is the attribution those terms ask for.

## Data shipped with the application

### DB-IP City Lite

`src/Windsock.Core/Data/places.geo` is built from the **DB-IP City Lite** database,
which maps address ranges to places. It is used offline: nothing is sent to
DB-IP, or anywhere else, when an address is looked up.

- Source: <https://db-ip.com/db/download/ip-to-city-lite>
- Licence: **Creative Commons Attribution 4.0 International (CC BY 4.0)** —
  <https://creativecommons.org/licenses/by/4.0/>
- Attribution: *IP Geolocation by DB-IP* — <https://db-ip.com>

CC BY 4.0 requires that this attribution travel with the data. It is also shown
inside the application, under the world map, so a reader who never opens this
file still sees where the addresses came from.

The file is a repacked form of the original: the same ranges and places, in a
layout that can be memory mapped and searched in place. Repacking is a
modification, and CC BY 4.0 permits it provided the attribution above is kept.

### Natural Earth

`src/Windsock.Core/Data/world.map` holds the country outlines and label points,
derived from **Natural Earth** at 1:110,000,000 scale.

- Source: <https://www.naturalearthdata.com/>
- Licence: **Public domain.** Natural Earth asks for no permission and requires
  no attribution; the credit shown in the application and given here is
  courtesy, not obligation.

### iptoasn.com

`src/Windsock.Core/Data/asn.dat` maps address ranges to the autonomous system
announcing them - its number, registered name and country - built from
**iptoasn.com**'s combined IPv4 and IPv6 table of 4 October 2026 by
`tools/asn/BuildAsnTable.cs`. It is used offline: nothing is sent to
iptoasn.com, or anywhere else, when an address is looked up.

- Source: <https://iptoasn.com/> (`ip2asn-combined.tsv.gz`)
- Licence: **Public Domain Dedication and License (PDDL) 1.0** —
  <https://opendatacommons.org/licenses/pddl/1-0/>. No attribution is
  required; this credit is courtesy.

The file is a repacked form: neighbouring ranges of the same network joined,
IPv6 ranges keyed on their first 64 bits, in a layout searched in place.

### IEEE Registration Authority: hardware address assignments

`src/Windsock.Core/Data/makers.oui` names the maker of a network adapter from
the first bits of its hardware (MAC) address, for the home device list. It is
built from the **IEEE Registration Authority's public listings** - MA-L (OUI),
MA-M, MA-S and IAB - retrieved on 5 October 2026, by
`tools/oui/build-makers.cs`. It is used offline: nothing is sent to the IEEE, or
anywhere else, when a maker is looked up.

- Source: <https://standards-oui.ieee.org/oui/oui.csv>, `oui28/mam.csv`,
  `oui36/oui36.csv` and `iab/iab.csv` under the same host
- Terms: the IEEE's pages state no licence for the listings beyond a general
  copyright footer. Debian's `ieee-data` package, which redistributes the same
  files, records that the IEEE asserts no copyright in the public listing and
  does not restrict its distribution. Windsock relies on that, keeps only each
  block's prefix and the organisation's name (the postal addresses in the
  listings are left out), and credits the source here. Nothing in Windsock is
  endorsed by the IEEE. If the IEEE asks otherwise, the table can be dropped:
  the device list then shows no makers, and nothing else depends on it.

The file is a repacked form: the prefixes of each width sorted for searching in
place, the names stored once each.

### PawnIO modules

`src/Windsock.Core/Resources/PawnIo/IntelMSR.bin` and `AMDFamily17.bin` are
**official signed PawnIO modules**, embedded in the full edition and used to
read the processor's temperature. They are the kernel-side half of that
reading: PawnIO runs only modules its author signed, so these are carried
verbatim and cannot be rebuilt from source here.

- Source: <https://github.com/namazso/PawnIO.Modules>, release
  [0.2.11](https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.11)
- Licence: **LGPL-2.1**, in
  [`COPYING`](src/Windsock.Core/Resources/PawnIo/COPYING) beside them
- Source for the modules is at the URL above; they are separate works loaded by
  the driver, not linked into Windsock, and either may be replaced with another
  signed build of the same modules.

The **PawnIO driver itself is not shipped, bundled, or installed by Windsock**.
It is GPL-2.0 and the user installs it themselves from <https://pawnio.eu>; the
Store edition contains neither the modules nor the code that would load them.

## Data fetched at run time, only when switched on

Neither list below is shipped with Windsock or kept in this repository. Each is
downloaded from its publisher by the person's own PC, only after they switch it
on, at most once an hour, and used on that PC alone to mark connections. The
credit their terms ask for is given here and beside every mark in the app.

### Spamhaus DROP

- Source: <https://www.spamhaus.org/drop/> (`drop_v4.json`, `drop_v6.json`)
- Terms: <https://www.spamhaus.org/drop/terms/> - free for commercial and
  non-commercial use with credit to **The Spamhaus Project**, whose copyright
  line (for example "(c) 2026 The Spamhaus Project SLU") travels with each
  downloaded copy and is shown with the marks it produces. Not redistributed,
  since the terms do not address redistribution; the Spamhaus name is used only
  to say where a mark came from.

### abuse.ch Feodo Tracker

- Source: <https://feodotracker.abuse.ch/blocklist/>
  (`ipblocklist_recommended.json`)
- Licence: **CC0** - no rights reserved; credited as courtesy.

### VirusTotal

Not data but a service: when the person adds their own API key and asks,
a program's SHA-256 is looked up at <https://www.virustotal.com/> under
VirusTotal's terms for its public API, which apply to that person's key.

## Packages

Resolved through NuGet at build time and not redistributed in this repository.
Versions are pinned in [`Directory.Packages.props`](Directory.Packages.props).

| Package | Licence |
| --- | --- |
| CommunityToolkit.Mvvm | MIT |
| FluentIcons.Wpf (Microsoft Fluent System Icons) | MIT |
| Microsoft.Data.Sqlite | MIT |
| Microsoft.Diagnostics.Tracing.TraceEvent | MIT |
| Microsoft.Extensions.Hosting, .Hosting.Abstractions, .Options | MIT |
| Microsoft.Windows.CsWin32 | MIT |
| ModelContextProtocol, ModelContextProtocol.Core *(Windsock.Mcp.exe)* | Apache-2.0 |
| Microsoft.Extensions.AI.Abstractions *(through ModelContextProtocol)* | MIT |
| System.CommandLine *(the `windsock` command line)* | MIT |
| Serilog, Serilog.Extensions.Hosting, Serilog.Sinks.Debug, Serilog.Sinks.File | Apache-2.0 |
| xunit.v3, Microsoft.Testing.Extensions.TrxReport *(test only)* | Apache-2.0 / MIT |

SQLite itself, reached through Microsoft.Data.Sqlite, is in the
[public domain](https://www.sqlite.org/copyright.html).
