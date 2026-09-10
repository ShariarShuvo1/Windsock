# Third-party notices

Windsock itself is MIT licensed (see [LICENSE](LICENSE)). It ships two data files
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
| Serilog, Serilog.Extensions.Hosting, Serilog.Sinks.Debug, Serilog.Sinks.File | Apache-2.0 |
| xunit.v3, Microsoft.Testing.Extensions.TrxReport *(test only)* | Apache-2.0 / MIT |

SQLite itself, reached through Microsoft.Data.Sqlite, is in the
[public domain](https://www.sqlite.org/copyright.html).
