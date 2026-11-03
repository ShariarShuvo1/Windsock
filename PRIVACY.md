# Privacy policy

**Windsock has no accounts, no telemetry, and no analytics. Nothing about you or
your machine is ever sent to the author, and nothing is sold or shared with
anyone.**

There is no server behind Windsock. There is no service to sign in to. The
project has no way to find out that you installed it, let alone what your
traffic looks like.

Last updated: 5 October 2026.

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

Some alerts, each off until you turn it on on the Alerts page, read a little
more, and only while they are on: **the system proxy settings** Windows keeps
for your account, so a change can be noticed the moment it is written; and,
for the network-change alerts, **your adapters' addresses and DNS servers** and
**your router's hardware address** from the neighbour table Windows already
keeps. Reading these sends nothing.

To say what each program is, Windsock also reads **each program's signature**
(checked on this PC with Windows' own WinVerifyTrust, told never to fetch
anything, so no certificate or revocation server is asked), **which services
run in each service host** from the service manager, **which program started
each process**, **which ports programs listen on**, and **Delivery
Optimization's own count of this month's downloads**, the figures Windows
shows under Windows Update › Delivery Optimization › Activity monitor. In the
full edition, while it runs as administrator, it also hears **the names each
program looks up**, from the DNS client's own events; those are kept in memory
only, never written to disk, and are gone when Windsock closes.

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
  address you chose, if you chose one) took to answer. Each minute also notes
  which network it was measured on - its name as Windows gives it (for example
  your Wi-Fi's name), its kind, and Windows' own id for it - so usage and data
  plans can be kept per network; and any notes you write on History. No
  addresses, no content, no names of files. Recording can be paused on the
  History page or under Settings › Data and privacy, which stops all of it being
  kept, and any period can be erased from there, all of it together - its notes,
  and the name of any network no other minute was on, included.
- `Backups\` — once a week, a copy of `Windsock.db`, so a damaged file does not
  cost your history; the newest few are kept (three unless you choose
  otherwise) and the copies can be switched off. Erasing any history deletes
  every copy too, so nothing erased lives on in a backup; a fresh copy of what
  is left is made soon after.
- `Outages.db` — the outage log: when the internet went away, for how long,
  the likely cause, and the name of the network it happened on (for example
  your Wi-Fi's name). Cleared with **Clear log** on the Connection › Outages
  page. Beside it, the rest of the connection's record: every speed test you
  run or schedule (when, which server, the speeds, the response times quiet and
  under load, and the network's name; never your public address), and the
  connection checks added up a quarter of an hour at a time - how many ran, how
  many were answered, and how long the answers took - so a report for your
  provider can cover any period. Speed tests are deleted one at a time or all
  together on the Connection › Speed test page. When each automatic test
  started is kept apart from the results, for two days, so deleting tests
  cannot lift the limit of four a day; it holds a time and nothing else.
- `Alerts.db` — the record of alerts: each alert's time, kind, its two lines of
  text and what became of it (shown, held for quiet hours, not shown and why,
  forwarded or not and why), so a notification you missed is not lost; and a
  row for each network or VPN change, for the charts to mark. Kept for the time
  you choose on the Alerts page (90 days unless you change it), cleared with
  **Clear** there, and deleted with everything else by **Delete history**
  under Settings › Data and privacy. Also in it: for each network you have been on while the
  network-change alerts were on, its name, this PC's address there, the DNS
  servers it handed out and the router's address and hardware address, so the
  next visit can be compared with the last; the system proxy as last seen,
  while the proxy alert is on, so a change made while Windsock was closed can
  be told; which alerts have already been told, so a drive's wear is told once
  however often the list is cleared; the end of a snooze, if one is
  running; and where alerts are forwarded, if you set that up - kept here
  rather than in `settings.json`, so a webhook address or ntfy topic never goes
  into a settings file you export.
- `seen-apps.txt` — the apps Windsock has seen use the network, one Windows
  id a line (a program's path or a Store app's package name), so a program
  using the network for the first time can be pointed out once - by the
  **New app online** alert, off until you switch it on on the Alerts page.
  Windsock looks for new apps only while that alert is on, and nothing is
  sent for it. Erasing all history clears it.
- `Timeline.db` — full edition only, created the first time it keeps
  something, which happens only while Windsock runs as administrator; reading
  or erasing never creates it. Always: each Windows service host's bytes per
  day (for example Delivery Optimization's), kept for about a year. Only once
  you switch on the **connection timeline**: which app talked to which
  address and port, when, how much, the name the address was reached by where
  the DNS cache or the app's own lookup gave one, and its country and network
  owner from the tables shipped with Windsock; and each day's top places and
  countries. Kept for the time you choose (30 days unless you change it) and
  never more than 250,000 conversations, the oldest going first; pruned hourly
  whenever Windsock runs, elevated or not and whether or not the timeline is
  still on. Switching the timeline off offers to delete what it kept at once.
  Erased with your history - a period you erase from History goes from the
  timeline too - and what is erased or pruned is overwritten in the file, not
  merely unlinked.
- `blocklists\` — only while you have a blocklist switched on: the copy last
  fetched from its publisher, and when. Switching a list off deletes its copy;
  for the rest of that hour only the time it was last asked for stays, so
  switching it back on cannot ask the publisher more than once an hour.
- `virustotal.key` and `reputation.json` — only if you add a VirusTotal key:
  the key, protected with Windows' DPAPI so only your Windows account on this
  PC can read it back (it is never put in `settings.json`, so an exported
  settings file never carries it), VirusTotal's answers for a few days so a
  program is not looked up twice, and `virustotal-usage.txt`, today's date and
  how many lookups were made, so a restart does not reset the daily allowance.
  Removing the key deletes all three.
- `Home.db` — only once you switch on **Find devices on my home network** or
  the **router check**: for each home network, the devices seen on it - each
  one's hardware address, its last IPv4 and IPv6 addresses, the name it was
  found to have and where that name came from, any name you gave it, whether it
  is the router, and when it was first and last seen - and the router check's
  last report: the router's maker and model, its own internet-side address, and
  the ports it has opened to the internet with the devices they lead to. Erased
  with your history, and a device can be forgotten on its own.
- `feed.json` — only once you switch on the **local feed**: the port it listens
  on and its token, kept here rather than in `settings.json` so an exported
  settings file never carries the token.
- `settings.json` — your preferences.
- `logs\` — diagnostic logs, kept locally.

Delete that folder and nothing of Windsock's remains. Uninstalling does not
delete it automatically, so that reinstalling does not lose your history. That
is why it sits beside the program rather than inside it: the installer owns
`%LOCALAPPDATA%\Windsock` and removes that folder entirely when you uninstall,
and your history is deliberately not kept there.

Windsock never uploads any of it. Exporting history or the outage log to CSV,
or your settings to a file (Settings › Advanced; the settings only, never
history), writes a file where you choose; what happens to that file afterwards
is entirely up to you. The same goes for scheduled export, which is off until
you turn it on: it writes each finished day or month of history to the folder
you pick, by itself, and never overwrites a file already there. Erasing history
does not reach into that folder.

A settings file carries your choices and nothing that belongs to this PC: not
the adapters it measures, its monitors and graphics cards or where its windows
stand, not what it records and how long it keeps it - the history switch, how
long detail is kept, the timeline, the weekly copies of history, how long alerts
are kept, your names for its networks, the folder it exports to - and not one of
the switches or addresses under Settings › Data and privacy, nor a meter feed's
address. Importing one never switches anything in the next section on or off,
and never shortens what this PC keeps; this PC keeps its own.

## What leaves your machine

Every item below is listed under **Settings › Data and privacy**, each with its
own switch, and each switch is the same setting wherever else it appears.
Nothing is sent to the internet by a switch you have not turned on, except the
update check, which the full edition has made at start-up since its first
release and which can be turned off there.

### Both editions: connection checks

To tell whether the internet is there, Windsock asks Windows, which already
checks by itself; Windsock sends nothing to get that answer. It also sends an
ICMP echo (a ping) to **your own router** every few seconds. That packet stays
on your local network and never reaches the internet.

Nothing is sent to the internet for these checks unless you choose it. Under
**How to check the internet** on the Connection › Status page, or with
**Internet check** under Settings › Data and privacy, you can also ping a
public address — Cloudflare's 1.1.1.1, Google's 8.8.8.8, Quad9's 9.9.9.9, or
one you type in. If what you type is a name rather than an address, Windows
first looks that name up through the DNS server you already use. Beyond that it
is a ping and nothing more: no data about you, your machine or your traffic is
in it.

The checks run while a Connection page is open, and in the background while
**Keep watching with these pages closed** is on (it is by default). Turn that
off and they run only while a Connection page is on screen.

### Both editions: connection tools, only when you ask

Each of these is off until you turn it on, names where it goes before it
sends anything, and runs only when you press its button - apart from the
automatic speed tests, which run only once you switch their schedule on. The
public address, NAT and DNS switches are on the Connection › Status page; the
speed tests have a page of their own, Connection › Speed test.

**Public address lookup** (**Allow looking it up**, then **Look up**). Asks a
service you choose for the address the internet sees: ipify (api.ipify.org
and api6.ipify.org, the default), icanhazip.com, or an HTTPS address you
type. The service learns your public address, as any website does. The answer is kept in memory for five minutes and
never written down. Who runs the network behind that address - your provider,
or your VPN's - is looked up in a table shipped with Windsock, so nothing more
is asked of anyone.

**Speed tests.** Measure your connection against one of two kinds of server:
- **Measurement Lab (M-Lab)**, whose NDT7 test needs no account. Before the
  first test Windsock asks you to agree to what M-Lab does with it: **M-Lab
  publishes every test, with your public address, and keeps it for good**, as
  open data for research (see <https://www.measurementlab.net/privacy/>). A
  test asks `locate.measurementlab.net` for the nearest server, then sends and
  receives data with that server for about twenty seconds.
- **Your own LibreSpeed server**, at an address you type. Nothing goes anywhere
  else, and Windsock never sends LibreSpeed's optional telemetry.

While a test runs, Windsock also pings the internet address you chose for the
connection checks, if you chose one, to measure response time under load.

**Automatic speed tests.** Off until you switch them on. Then up to four tests
a day run at random times against the server above - M-Lab asks for no more -
and none run on a connection that is, or may be, metered unless you allow it:
one Windows treats as metered, one it says nothing about, or a VPN over a
metered link such as a phone's hotspot. Setting the clock back does not free
any of the four.

**NAT test** (**Also ask a STUN server**, then **Test**). Asks a STUN server
how your router and provider translate addresses: stun.nextcloud.com by
default, Cloudflare's or Google's, or one you type. The server learns your public address and port. Before asking it, the
test asks your own router for its outside address (NAT-PMP), which stays on
your network.

**DNS timing** (**Allow timing**, then **Time them**). Times the DNS servers
your PC already uses with a few queries for the root of the DNS - a question
that names no website.

These have nothing to switch, because they never leave your PC or your own
network: the Network › Adapters and Wi-Fi page reads Windows' own figures and
Wi-Fi's log of connections and disconnections; the Connection › Status page
adds Windows' verdict on IPv6, the response-time figures from the checks above
and the report for your provider, which is saved only where you choose and
leaves out the network's and the provider's names unless you include them; and
the Wi-Fi signal under the Network › Overview chart is read from your own
adapter. None of them sends anything. A scan for nearby Wi-Fi networks, which you start, has your Wi-Fi
adapter listen and ask nearby access points to announce themselves, as Windows
does by itself every minute or so.

### Both editions: forwarding alerts

Off until you set it up on the Alerts page, where the destination is shown;
**Forwarding alerts** under Settings › Data and privacy is the same switch.
Then each alert, as it is raised, is posted to the one place you chose: a
webhook address you type, or a topic on an [ntfy](https://ntfy.sh) server -
ntfy.sh itself, or your own. What is sent is the alert and nothing else: its
kind, how much it matters, its title and message, the time it was raised, and
the name you gave this PC if you gave one. An ntfy access token, if you enter
one, goes only to that ntfy server. Plain `http://` is accepted only for an
address on this PC or your own network - checked on the address actually
connected to, so a name that turns out to lead to the internet is refused -
and anywhere else must be `https://`.

An alert that cannot be delivered is tried at most three times over a few
minutes and then recorded as not forwarded, with the reason, in the record of
alerts; nothing is retried for ever. The webhook address and the topic are
never written to the logs, which name only the server.

### Both editions: the taskbar meter's feeds and public address

The meter on the taskbar shows what Windsock measures on this PC. Two of the
readings a place on it can show reach further, and both are off until you
switch them on under **Settings › Data and privacy**:

**Meter feeds** (**Read feeds**). A place can show one value from a JSON
document at an address you type on the Taskbar page: an `https://` address
anywhere, `http://` only on this PC or your own network, or a file on this PC's
own drives - never a network share, which would sign you in to another computer.
With the switch on, Windsock asks exactly those addresses, at the interval each
place says (every 15 seconds at the most often), and nothing else. The request
carries nothing about you beyond what any web request does; redirects are not
followed, an answer larger than 64 KB is refused, and a request that takes more
than five seconds is dropped. The address is shown on the Taskbar page and in
the same Settings row. A layout file you load names any addresses it reads from,
asks first if feeds are already on, and never switches feeds on itself; nor
does importing a settings file, which carries no feed's address either way.

**Public address on the meter** (**Keep it current**). A place can show the
address the internet sees, with its country, to check a VPN at a glance. It
shows what the Connection page's **Look up** last found and asks nothing of its
own. With this switch on - and the public address lookup's own switch, above -
it asks the same service again, at most every five minutes, while the meter
shows it.

### Both editions: blocklists

Off until you switch one on. Two lists are offered, each with its own switch:
**Spamhaus DROP** (netblocks run by or for criminals, from
<https://www.spamhaus.org/drop/>) and **abuse.ch Feodo Tracker** (botnet control
servers, from <https://feodotracker.abuse.ch/>). Neither is shipped with
Windsock. While one is on, Windsock downloads it from its publisher: never more
than once an hour, normally twice a day, as a plain HTTPS request that carries
Windsock's name and version and the date of the copy it already holds, so an
unchanged list costs only a "not modified". The publisher sees what any web
server sees - your public address and that request - and nothing else.

**Your connections are never sent anywhere.** Each address is checked against
the copy on your PC, and only public addresses are checked at all, so your own
network and your provider's are never marked. A marked connection says "listed
in" the list, with the list's date: a list is advice, not proof.

### Both editions: reputation checks on VirusTotal

Off until you add your own VirusTotal API key, and even then only when you ask
about one program. Then Windsock sends that program's **SHA-256 hash** - never
the file, its name or its path - to `www.virustotal.com` with your key, and
shows what VirusTotal's engines said, or that VirusTotal has never seen it.
Nothing is ever looked up by itself. Windsock keeps to the free allowance of 4
requests a minute and 500 a day, counted across restarts, and keeps only a key
shaped like one (64 letters and digits). VirusTotal's own terms apply to your
key and what it receives, and VirusTotal records that the key asked for that
hash.

### Both editions: your home network, when you switch it on

The switches are **Find devices on my home network**, **Router details** and
**Household traffic**, under Settings › Data and privacy (the first two also on
Network › Home devices). Each is off until you switch it on, and only ever on a network Windows calls
**private** - never on one it calls public, such as a café's or a hotel's
Wi-Fi, where it would list strangers' devices, and never on a workplace's
domain network. It works only through a wired or Wi-Fi connection that has its
own router on it, never through a VPN or any other tunnel, even one Windows
calls private, so nothing is asked of whoever is at the tunnel's other end.
Every packet Windsock sends for these goes to a device on your own network or
to your router, and nowhere else.

**Find devices on my home network.** Reads the list of neighbours Windows
already keeps - the addresses and hardware addresses this PC has talked to
lately - which sends nothing. Each device's maker is looked up in a table of
the IEEE's hardware address assignments shipped with Windsock, on your PC. To
name the devices it finds, Windsock asks the home network, at most every
fifteen minutes while you are looking at the list and hourly otherwise:
- a **multicast DNS browse** (the queries AirPlay, Chromecast and printers
  answer), sent to the multicast group on your own network, 224.0.0.251 and
  ff02::fb, through Windows' own DNS client;
- for a device still without a name, at most every six hours, a **NetBIOS
  name question** (UDP 137) to that device, and a **reverse lookup** (DNS, UDP
  53) put to your router about that device's address. The lookup is sent
  with recursion switched off, which tells the router to answer only from the
  names it already holds and not to pass the question on to your internet
  provider's DNS servers. A router that ignores that instruction could still
  pass it on; the question would then carry the device's address on your home
  network, nothing more.

**Look for every device**, a button, sends one ARP request - "who has this
address?" - to each address on your home network, which never leaves it. It
runs only when you press it.

**New device alerts.** Off until you turn on **New device on the home network**
on the Alerts page. Then an alert says when a device is seen on your home
network for the first time. Nothing more is sent for it.

**Wake a device.** A Wake-on-LAN packet, broadcast on your home network, only
when you ask for it, and only while this PC is still on the network the device
was seen on.

**Router check.** Off until you turn it on. Then, when you press **Check** and
once a week, Windsock asks your router over UPnP for its maker and model, its
own internet-side address and the ports it has opened to the internet: a
search sent to your router's address alone (UDP 1900), then HTTP requests to
the router and nowhere else. It only ever asks; it never opens, closes or
changes a port, and an answer pointing anywhere but the router is ignored.

**The household's traffic.** Off until you turn it on. Then, while a page or
the local feed is showing it, Windsock asks your router for its own traffic
counters every five seconds, over the same UPnP, to show what every device used
together. It stops asking the moment this PC moves to another network or the
router's address changes, before the next request.

### Full edition only

In the full edition, three more things can reach the network, all of them in
service of something you asked to see:

**Reverse DNS lookups.** Off until you turn on **Reverse lookups** under
Settings › Data and privacy. Then, when the network map or an app's own window
shows a connection whose name is not already in the DNS cache, Windsock asks
your computer's configured DNS resolver what name the address has. This is the
same lookup any browser makes and it goes to whichever DNS server you already
use — not to the author. Off, an address the cache cannot name stays an
address. The lookups are made only while the map or an app's own window is
showing names. The Store edition never makes them.

**ICMP echo (ping / traceroute).** Off until you turn it on: with **Routes** in
the map's settings or in an app's own window, or **Route tracing** under
Settings › Data and privacy, or **Route tracing** beside the hop-by-hop table
on the Connection › Status page - four places for one switch, which stays as
you leave it. Then the map traces the path to an address by sending ICMP packets
along it, exactly as the built-in `tracert` does, only for the places it draws
and only while the map or an app's window is on screen or open in a window of
its own. The Store edition has the map but never traces a route, and offers no
such switch.

The **hop-by-hop table** on the Connection › Status page uses the same switch:
while you have it running (**Start**, then **Stop**), it repeats that walk once a second towards the
internet address you chose for the connection checks, one ICMP echo to each
router on the way, to show where along the path packets go missing. It stops
when you close it.

**Update checks.** When it starts, Windsock asks the GitHub Releases page for
this project whether a newer version exists, and **Check now** under
Settings › About asks once more. That request tells GitHub only what any web
request tells a web server. A Windsock left running asks again once a day, the
same request, so the alert that a new version is ready still comes. The
automatic check can be switched off with **Update check** under
Settings › Data and privacy, which stops both, and it is inactive in the Store
edition, where Windows handles updating.

No other request is made by Windsock, ever. The Store edition makes none of
these three; its only packets are the connection checks above, and, if you set
them up, the alerts you forward, the blocklists you switch on, the
VirusTotal lookups you ask for, the connection tools you use, and the home
network features you switch on, which stay on your own network.

## Other programs on this PC: the local feed, the command line and AI tools

**The local feed** is off until you switch it on. Then Windsock answers
requests from programs on this PC - Rainmeter, Stream Deck, Home Assistant, an
OBS overlay, the `windsock` command line - with what it shows: the rates, the
connection, today's and this month's use, the data plan, the machine's load and,
if you read it, the household's traffic. It listens on this PC's loopback
addresses (127.0.0.1 and ::1) alone, which no other computer can reach, and it
sends nothing anywhere: other programs ask it, and it only answers. Every
request must carry its token, so other accounts on this PC cannot read it, and
it refuses requests made by web pages, so a website you visit cannot read it
either. Another program running as you cannot put itself in front of the feed
to catch those requests and their token: if any part of the feed's address is
already taken, the feed does not start, and says so. The feed does not offer
your device list or your apps.

**The command line** (`windsock`) reads the feed, and reads history and the
device list from their files on your PC. It changes nothing; `windsock wake`
sends a Wake-on-LAN packet on your home network, as above, when you run it.
**Add to PATH**, a button in Settings, adds Windsock's own folder to your
account's PATH setting so that a new console finds `windsock`; **Remove from
PATH**, or uninstalling Windsock, takes it out again. Nothing else in the
setting is touched.

**Tools for AI assistants** (`Windsock.Mcp.exe`, or `windsock-mcp` in the Store
edition) are a small program an AI assistant you set up can run to ask the
local feed the same questions. It does nothing unless you add it to an
assistant and the feed is on. **What an assistant reads, it may send to the
company that runs it**, as with anything else you show it: that is governed by
the assistant's own privacy policy, not this one. Windsock itself sends nothing.

## Location data is looked up offline

The world map turns addresses into approximate places using a copy of the
DB-IP City Lite database that ships inside the application. The lookup happens
entirely on your machine.

**Nothing is sent to DB-IP or to anyone else when an address is looked up.**
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the attribution that
data carries.

The same goes for naming the network an address belongs to - your provider
behind your public address, for instance. That comes from a copy of
iptoasn.com's public routing table, also shipped inside the application and
read on your machine alone.

## Permissions

Windsock does not need administrator rights to install or to run.

In the full edition, two optional features ask for more, per session, and only
when you choose them: live per-process rates need a kernel trace session, and
processor temperature reads a sensor through [PawnIO](https://pawnio.eu) if you
have installed that separately. Both are used to read figures shown to you on
screen, and neither changes what leaves your machine — which is still nothing.
The Store edition never asks for administrator rights and contains neither.

Running as administrator, the full edition can also act for you, each time only
when you ask and confirm: **close one connection** (the far end is sent a
reset), **end a program** that belongs to another account, and **block a
program or service in Windows Defender Firewall** with an outbound rule. It
never blocks a part of Windows, a service Windows needs to reach any network,
or a Windows service host as a whole - a rule on that one file would cut off
every service at once - so a service is blocked only by its own name, and only
where its host runs it alone. Those rules are Windows' own, belong to a group
named Windsock, and stay in force whether or not Windsock runs. **Uninstalling Windsock cannot remove them**,
because they were made with administrator rights and the uninstaller has none:
remove them from Windsock first (Network › Activity, **Remove all**), or in
Windows Defender Firewall with Advanced Security, where the group Windsock
lists them all. Without administrator
rights Windsock can end only your own programs, and changes nothing else.

The full edition's quick fixes on the Network › Adapters and Wi-Fi page - clear the DNS
cache, renew an address, restart an adapter - each ask Windows for
administrator permission for that one command when it needs it, and only when
you press the button. **Share as QR code** reads the connected network's saved
password from Windows, on that one press, to draw the code on screen; the
password is not kept anywhere, written down or copied.

Since Windows 11 24H2, Windows asks your permission (Settings › Privacy &
security › Location) before any app may read the name of your Wi-Fi network or
see nearby networks, because both can reveal where you are. Windsock reads the
signal, band, channel and speed of your Wi-Fi without that permission, as
Windows allows. It reads the network's name only on the Network › Adapters and
Wi-Fi page - once that permission is on, or when you press **Show the
network's name** - and nearby networks only when you start a scan; never at
start-up. Nothing it reads this way leaves your PC.

The Store package declares two capabilities beyond running as a desktop app.
`networkDataUsageManagement`, which Microsoft lists for reading Windows' data
usage per app, reads; it changes nothing and sends nothing. `location` is
there because a packaged app must declare it before Windows will even ask you
the location question above, which is what the Wi-Fi network's name and the
scan for nearby networks need. Windsock never asks Windows where you are, and
Windows still asks you before it gives the permission. (This has not yet been
tried in an installed Store package.)

## Children

Windsock is a system utility with no social features, no accounts and no
content. It collects nothing from anyone, including children.

## Changes

Any change to this policy will be a commit in this repository, so its full
history is public and auditable.

## Contact

Raise an issue at
<https://github.com/ShariarShuvo1/Windsock/issues>.
