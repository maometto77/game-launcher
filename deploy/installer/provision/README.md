# Provisioning

What a freshly installed copy of Don starts out with.

Everything in `adapters/` here is copied into `%LOCALAPPDATA%\Don\adapters\` when
someone installs, so a friend who has never opened the application already has
the sources you chose for them. Combined with `build-release.ps1 -RelayUrl` and
`-CatalogUrl`, the archive you hand over is configured before it is opened:
nothing to paste, nothing to explain.

```powershell
.\deploy\installer\build-release.ps1 `
    -RelayUrl   https://don.example.com `
    -CatalogUrl https://don.example.com/feed/catalog.json
```

---

## The two rules

Provisioning is **first-run only**, enforced by the installer, and both halves
matter:

- **An adapter that already exists is left alone.** Someone who edited a shipped
  manifest keeps their edit through every upgrade.
- **`settings.json` is written only when there is none.** It holds the relay
  token, which the relay stores as a hash and cannot reissue — overwriting it on
  upgrade would sign the person out permanently with no way back.

So this folder decides what a *new* install begins with. It cannot reconfigure an
existing one, by design. `Install-Don.ps1 -NoProvision` skips it entirely.

---

## adapters/

Sourcing feed manifests, as described in
[`docs/sourcing-adapters.md`](../../../docs/sourcing-adapters.md). Anything with
a `.yaml`, `.yml` or `.json` extension is picked up.

Be deliberate about what goes here. These are read on other people's machines,
and a manifest naming a host is an instruction to fetch from it.

`zenodo.yaml` ships by default because it is the one example that works
unmodified — a public API, no key, no account, and a path chosen to stay inside
what that site's `robots.txt` permits. Delete it if you would rather ship
nothing, or add your own.

Worth knowing about what these *are*: a sourcing adapter answers "given a listing
from this host, what can be downloaded". It does nothing on its own — it needs
the catalogue to already hold listings from a host it claims. **It is not how
your shared catalogue reaches people.** That is `-CatalogUrl`, which is a
setting, and the two are easy to confuse.

---

## What `-RelayUrl` and `-CatalogUrl` write

A `settings.defaults.json` staged beside this folder in the archive:

```json
{
  "relayUrl": "https://don.example.com",
  "sharedCatalogUrl": "https://don.example.com/feed/catalog.json",
  "discoveryEnabled": true
}
```

`discoveryEnabled` is set with `-CatalogUrl` because the catalogue is not read
while discovery is off, and someone who was handed a pre-configured launcher has
no reason to guess that a second switch exists. Passing only `-RelayUrl` leaves
it alone — the launcher's default is off, and that stays true for anyone who did
not deliberately ship a catalogue.

The file is a plain `AppSettings` document, so any field of it can be seeded by
editing what the build script writes. Keep it to things that are genuinely
deployment-wide: it is a starting point for a person, not a policy over them.

**Nothing secret belongs in it.** It ships inside an archive you hand out, and
the relay issues each person their own token on first contact — there is no
shared credential to embed, and that is deliberate.
