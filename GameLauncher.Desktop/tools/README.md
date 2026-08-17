# Bundled external tools

Anything placed in `win-x64/` here is copied to `tools/` beside the built and
published application, where the launcher looks for it before falling back to
`PATH`.

The folder is empty in the repository on purpose, and the build succeeds without
it. Everything the launcher shells out to is optional: with nothing here, aria2
is simply unavailable and downloads use the built-in HTTP engine.

## Adding aria2c

aria2 is the only tool the launcher currently runs. It buys two things the
built-in engine cannot do: several connections to one server, and BitTorrent —
which is the only way to use the `.torrent` files the Internet Archive publishes
for its items.

1. Download the Windows 64-bit build from the project's own releases:
   <https://github.com/aria2/aria2/releases>
2. Take `aria2c.exe` out of the archive.
3. Put it here:

```
GameLauncher.Desktop/tools/win-x64/aria2c.exe
```

4. Build or publish as usual. The build prints which tools it bundled.
5. Turn aria2 on in Settings. The launcher finds the bundled copy without a path
   being configured.

Check what you downloaded before you ship it:

```bash
certutil -hashfile aria2c.exe SHA256
```

and compare against the checksum on the release page.

## Why the build does not fetch this for you

A launcher that downloaded an executable and ran it, without being asked, would
be doing the single most abusable thing a desktop application can do. That the
binary in question is well known does not change what the mechanism is — and a
mechanism like that is worth more to an attacker than the convenience is worth
to anyone else.

So the decision is yours and it is made once, at build time, with a checksum you
can verify. At run time the launcher only ever *looks* for a program; it never
installs one.

## What is deliberately not here

**Ludusavi.** The launcher reads the Ludusavi community save manifest directly
and never runs the program, so bundling the binary would add megabytes and buy
nothing. If a future feature genuinely needs to shell out to it, drop it in this
folder and ask `IExternalToolLocator` for `"ludusavi"` — the mechanism is already
generic.

## Where the launcher looks, in order

1. The path configured in Settings, if there is one.
2. Beside the executable.
3. `tools/` beside the executable — where this folder lands.
4. `%LOCALAPPDATA%\GameLauncher\tools\` — for adding a tool without write access
   to Program Files.
5. `PATH`.

Each candidate is run with `--version` before it is accepted, because a file
existing is not the same as a file working: a zero-byte placeholder, a copy for
the wrong architecture, or something quarantined by security software all pass
an existence check and fail to start.
