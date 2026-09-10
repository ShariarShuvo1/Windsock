# Before you install

**Windows 10 version 1809 or later.** Windows 11 recommended.

**Nothing else is needed.** The .NET runtime is included, so there is no
prerequisite to install first.

## Where it goes

Choosing **only for me** installs without administrator rights and keeps
Windsock out of everybody else's way. Choosing **for everyone** installs into
Program Files and needs an administrator.

Your history and settings live in `%LOCALAPPDATA%\Windsock` either way, and
are left alone by an uninstall.

## Two features that ask for more

**Per-process rates** need a kernel trace session, which needs administrator
rights. Windsock does not ask for them at install time. There is a button in
the app that restarts it elevated when you want them, and everything else
works without.

**Processor temperature** additionally needs PawnIO, a small signed driver you
install yourself from https://pawnio.eu. Windsock ships no driver and installs
none. Without it that one reading shows a dash and nothing else changes.
