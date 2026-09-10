# Privacy policy

**Windsock has no accounts, no telemetry, and no analytics. Nothing about you or
your machine is ever sent to the author, and nothing is sold or shared with
anyone.**

There is no server behind Windsock. There is no service to sign in to. The
project has no way to find out that you installed it, let alone what your
traffic looks like.

Last updated: 10 September 2026.

## What Windsock reads

Windsock is a network meter, so it necessarily looks at things about your
machine: network interface counters, which processes hold sockets open, the
addresses those sockets connect to, and hardware sensors such as processor load
and temperatures.

**All of that stays on your computer.** It is read, shown to you, and in the
case of throughput, written to a local database so the History tab can chart
it later.

## What is stored, and where

Everything Windsock keeps lives in one folder on your machine:

    %LOCALAPPDATA%\Shariar Shuvo\Windsock\

- `Windsock.db` — one row per minute of total upload and download bytes.
  No addresses, no process names, no content. Recording can be switched off in
  the History tab or in Settings, and any period can be erased from there.
- `settings.json` — your preferences.
- `logs\` — diagnostic logs, kept locally.

Delete that folder and nothing of Windsock's remains. Uninstalling does not
delete it automatically, so that reinstalling does not lose your history. That
is why it sits beside the program rather than inside it: the installer owns
`%LOCALAPPDATA%\Windsock` and removes that folder entirely when you uninstall,
and your history is deliberately not kept there.

Windsock never uploads any of it. Exporting history to CSV writes a file where
you choose; what happens to that file afterwards is entirely up to you.

## What leaves your machine

The Store edition sends **nothing at all** — the features below are not built
into it.

In the full edition, three things reach the network, all of them in service of
something you asked to see:

**Reverse DNS lookups.** When the process table or the network map shows a
connection, Windsock asks your computer's configured DNS resolver what name an
address has. This is the same lookup any browser makes and it goes to whichever
DNS server you already use — not to the author.

**ICMP echo (ping / traceroute).** The network map traces the path to an address
by sending ICMP packets along it, exactly as the built-in `tracert` does. This
is on by default and can be switched off with **Follow routes** in Settings.

**Update checks.** Windsock asks the GitHub Releases page for this project
whether a newer version exists. That request tells GitHub only what any web
request tells a web server. It can be switched off with **Check for updates**
in Settings, and it is inactive in the Store edition, where Windows handles
updating.

No other request is made by Windsock, ever.

## Location data is looked up offline

The world map turns addresses into approximate places using a copy of the
DB-IP City Lite database that ships inside the application. The lookup happens
entirely on your machine.

**Nothing is sent to DB-IP or to anyone else when an address is looked up.**
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the attribution that
data carries.

## Permissions

Windsock does not need administrator rights to install or to run.

Two optional features ask for more, per session, and only when you choose them:
per-process attribution needs a kernel trace session, and processor temperature
reads a sensor through [PawnIO](https://pawnio.eu) if you have installed that
separately. Both are used to read figures shown to you on screen, and neither
changes what leaves your machine — which is still nothing.

## Children

Windsock is a system utility with no social features, no accounts and no
content. It collects nothing from anyone, including children.

## Changes

Any change to this policy will be a commit in this repository, so its full
history is public and auditable.

## Contact

Raise an issue at
<https://github.com/ShariarShuvo1/Windsock/issues>.
