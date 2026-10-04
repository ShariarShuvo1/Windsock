# Privacy policy

**Windsock has no accounts, no telemetry, and no analytics. Nothing about you or
your machine is ever sent to the author, and nothing is sold or shared with
anyone.**

There is no server behind Windsock. There is no service to sign in to. The
project has no way to find out that you installed it, let alone what your
traffic looks like.

Last updated: 15 September 2026.

## What Windsock reads

Windsock is a network meter, so it necessarily looks at things about your
machine: network interface counters, the name, link speed and local address of
each network adapter, which processes hold sockets open, the addresses those
sockets connect to, and hardware sensors such as processor load and
temperatures.

It also reads two things Windows already keeps: **its data usage** - how much
each app used the network, the figures shown in Settings under Network and
internet, Data usage - and **the DNS client's cache**, the names programs on
this machine have looked up, which is how an address on the Apps page gets
a name without Windsock sending a lookup of its own.

**All of that stays on your computer.** It is read, shown to you, and in the
case of throughput and each app's usage, written to a local database so the
History and Apps sections can show it later.

## What is stored, and where

Everything Windsock keeps lives in one folder on your machine:

    %LOCALAPPDATA%\Shariar Shuvo\Windsock\

- `Windsock.db` — one row per minute of total upload and download bytes, and
  each app's upload and download: as Windows counted it, and, while the full
  edition runs as administrator, as Windsock measured it each minute. The app is
  stored as Windows names it, the full path of a program's file or a Store
  app's package name. Beside those, a row a minute of the machine - processor,
  graphics and memory in use, their temperatures where known, and bytes read
  from and written to disk - and of the connection checks: how many ran, how
  many found the internet gone, and how long the router (and the internet
  address you chose, if you chose one) took to answer. No addresses, no content,
  no names of files. Recording can be switched off in the History section or in
  Settings, which stops all of it being kept, and any period can be erased from
  there, all of it together.
- `Outages.db` — the outage log: when the internet went away, for how long,
  the likely cause, and the name of the network it happened on (for example
  your Wi-Fi's name). Cleared with **Clear log** in the Connection section.
- `settings.json` — your preferences.
- `logs\` — diagnostic logs, kept locally.

Delete that folder and nothing of Windsock's remains. Uninstalling does not
delete it automatically, so that reinstalling does not lose your history. That
is why it sits beside the program rather than inside it: the installer owns
`%LOCALAPPDATA%\Windsock` and removes that folder entirely when you uninstall,
and your history is deliberately not kept there.

Windsock never uploads any of it. Exporting history or the outage log to CSV
writes a file where you choose; what happens to that file afterwards is
entirely up to you.

## What leaves your machine

### Both editions: connection checks

To tell whether the internet is there, Windsock asks Windows, which already
checks by itself; Windsock sends nothing to get that answer. It also sends an
ICMP echo (a ping) to **your own router** every few seconds. That packet stays
on your local network and never reaches the internet.

Nothing is sent to the internet for these checks unless you choose it. Under
**How to check the internet** in the Connection section you can also ping a
public address — Cloudflare's 1.1.1.1, Google's 8.8.8.8, Quad9's 9.9.9.9, or
one you type in. If what you type is a name rather than an address, Windows
first looks that name up through the DNS server you already use. Beyond that it
is a ping and nothing more: no data about you, your machine or your traffic is
in it.

The checks run while the Connection section is open, and in the background
while **Keep watching with this section closed** is on (it is by default). Turn
that off and they run only while the section is on screen.

### Full edition only

In the full edition, three more things reach the network, all of them in service
of something you asked to see:

**Reverse DNS lookups.** When the network map or an app's own window shows a
connection whose name is not already in the DNS cache, Windsock asks your
computer's configured DNS resolver what name the address has. This is the same
lookup any browser makes and it goes to whichever DNS server you already use —
not to the author. The Store edition does not do this; there, an address the
cache cannot name stays an address. In the full edition the lookups are made
only while the network map or an app's own window is showing names; turning
names off there, or closing them, stops them. There is no switch for them in
Settings.

**ICMP echo (ping / traceroute).** The network map traces the path to an address
by sending ICMP packets along it, exactly as the built-in `tracert` does. It
does this only once you tick **Routes** in the map's settings, which starts
unticked each time a map opens, and only for the places the map draws, while
that map is on screen or open in a window of its own. It stops if you fold the
map away, and can be switched off altogether with **Follow routes** in Settings.
The Store edition has the map but never traces a route.

**Update checks.** Windsock asks the GitHub Releases page for this project
whether a newer version exists. That request tells GitHub only what any web
request tells a web server. It can be switched off with **Check for updates**
in Settings, and it is inactive in the Store edition, where Windows handles
updating.

No other request is made by Windsock, ever. The Store edition makes none of
these three; its only packets are the connection checks above.

## Location data is looked up offline

The world map turns addresses into approximate places using a copy of the
DB-IP City Lite database that ships inside the application. The lookup happens
entirely on your machine.

**Nothing is sent to DB-IP or to anyone else when an address is looked up.**
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the attribution that
data carries.

## Permissions

Windsock does not need administrator rights to install or to run.

In the full edition, two optional features ask for more, per session, and only
when you choose them: live per-process rates need a kernel trace session, and
processor temperature reads a sensor through [PawnIO](https://pawnio.eu) if you
have installed that separately. Both are used to read figures shown to you on
screen, and neither changes what leaves your machine — which is still nothing.
The Store edition never asks for administrator rights and contains neither.

The Store package declares one capability beyond running as a desktop app,
`networkDataUsageManagement`, which Microsoft lists for reading Windows' data
usage per app. It reads; it changes nothing and sends nothing.

## Children

Windsock is a system utility with no social features, no accounts and no
content. It collects nothing from anyone, including children.

## Changes

Any change to this policy will be a commit in this repository, so its full
history is public and auditable.

## Contact

Raise an issue at
<https://github.com/ShariarShuvo1/Windsock/issues>.
