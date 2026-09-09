# Release validation: 1.0.2.0

Validated on 2026-09-09 (Europe/Berlin) against Jellyfin 12.0 on **zen**, using the workspace test
accounts. Production Plex, zurg and provider libraries were not modified, and no provider content was
deleted.

## Defects reproduced before fixing

- An anonymous request to a playback URL, with no signature and no Jellyfin session, was served.
  Measured live on zen against the 1.0.0.0 build: a `302` to the Real-Debrid CDN.
- Absolute-numbered anime merged into one film. TMDb matches "One Piece - 1004" to the series and
  writes a ProductionYear onto it, after which every episode shared a title and a year and the
  version merge folded them together. Measured on the TorBox account: 155 One Piece episodes in one
  item with 154 alternate versions, plus 38 Detective Conan and 12 Meitantei Precure!.
- The 1.0.1.0 release-hardening work in `dfd9f5c` is validated separately in this file's
  history; this run revalidates it on top of the version-merge fix.

## Passing checks

| Area | Evidence |
|---|---|
| Build | .NET 10 Release build: zero warnings and zero errors. |
| Automated tests | 79 passed, zero failed, zero skipped. |
| Package | ZIP contains only `Jellyfin.Plugin.RdZurg.dll` and `meta.json`; version, GUID, ABI, content and SHA-256 verified by `scripts/verify-package.py`. |
| Installation | Installed the ZIP contents into `/var/lib/jellyfin/plugins/rd-zurg_1.0.2.0` on zen. Jellyfin reported `RD zurg 1.0.2.0` Active after restart, with no `[ERR]` or `[FTL]`. |
| Signed playback | A library item's own URL answered `206` for an initial range and for a suffix range (`bytes=-4096`), and the bytes began with a real container signature (`ftypisom`, a real MP4). |
| Unsigned playback | The same URL with the signature stripped answered `401`. A 64-character forged signature answered `401`. The exact URL that served media from the 1.0.0.0 build answered `401`. |
| Range handling | An unsatisfiable range (`bytes=999999999999-`) answered `416`. |
| Library identity | 3,274 episode IDs and every pre-existing movie ID survived the upgrade and resync; 10 movie IDs left `/Items` because they became alternate versions, and each still resolves through `GET /Items/{id}`. |
| Sync safety | A resync removed nothing: "3364 torrents seen ... removed 0 gone from the account". |
| Version merging | After the fix a full resync folded **0** new alternate versions on the TorBox account, against 202 on the same account before it. |
| Configuration | Settings load, save and reload through the dashboard; the plugin validates them on save and rejects unusable server URLs, colliding library names and out-of-range limits. |

The validated release ZIP has SHA-256:

```text
d61eba825ecfd1caaca0f7b3f0960e08f7c8fb11b72298cdcd62f5f59a77d04f
```

CI repackages the source with its own build timestamp, so its ZIP checksum can differ. Always use the
checksum distributed alongside the exact ZIP being installed.

## Release scope

Ready for private ZIP distribution on Jellyfin 12.0. The repository remains private; no version tag,
GitHub release or public catalog was created by this validation. Catalog installation and automatic
updates need a separately chosen artifact host, which was not represented as tested.

**Not covered by this run.** A real player was not driven end to end; playback was exercised with
range requests against the plugin's own endpoint rather than through a Jellyfin client session.
Merges written by an earlier build are not undone by the upgrade - see
[release operations](RELEASING.md).
