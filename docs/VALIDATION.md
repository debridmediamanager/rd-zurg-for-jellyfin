# Release validation: 1.0.1.0

Validated on 2026-09-09 (Europe/Berlin) against an isolated Jellyfin 12.0 container on zen,
using the Real-Debrid test account. Production Jellyfin, Plex, zurg and provider torrents were
not modified. Jellyfin 12.0 was checked against the [official release](https://github.com/jellyfin/jellyfin/releases/tag/v12.0).

## Defects reproduced before fixing

- An anonymous HEAD request to an unsigned media URL returned a working CDN redirect.
- A real 32-torrent sample imported 799 entries. Reducing the configured limit to one torrent
  removed 798 entries even though the other torrents still existed in the account.
- Seven added regression cases failed: unsigned access, invalid ranges, an upstream ignoring
  ranges, split/encrypted RAR4 members, and names extending beyond their declared archive header.
- A player cancelling reads during probing/seeking invalidated usable links and triggered fresh
  provider resolutions. An archive seek/decode run failed before cancellation handling was corrected.

## Passing checks

| Area | Evidence |
|---|---|
| Build | .NET 10 Release build: zero warnings and zero errors. |
| Automated tests | 74 passed, zero failed, zero skipped. Includes interrupted/limited sync, incomplete/overlapping pagination, configuration/signature validation, byte ranges, cancellation and RAR4/RAR5 safety. |
| Package | ZIP contains only the plugin DLL and `meta.json`; version, GUID, ABI, content and SHA-256 verified. Invalid version input is rejected before building. |
| Installation | Installed the ZIP into an isolated server. Jellyfin reported version 1.0.1.0 Active, and the installed DLL matched the final build byte for byte. |
| Configuration UI | Loaded settings, saved an API interval, reloaded and confirmed persistence. Invalid duplicate library names returned HTTP 400 and a visible error message. Direct redirects defaulted off. |
| Library safety | Final 32-torrent sample: 799 entries. Reducing the limit retained all entries. Repeat sync, a full Jellyfin library scan and a server restart retained all 799 item IDs. |
| URL migration | Changed the server URL to a separate reverse proxy under `/jellyfin`, synced, verified all item IDs and signed archive playback, then restored the original URL and verified IDs again. |
| Streaming | Real plain MKV and stored-RAR media passed signed HEAD, initial/middle/suffix range and Matroska signature checks. Unsigned/tampered URLs returned 401; unsatisfiable ranges returned 416. |
| Decoder | ffprobe detected video/audio from both real sources. ffmpeg decoded three seconds of audio/video from the archive after seeking to 120 seconds. |
| Real player | Jellyfin Web played Tears of Steel, advanced playback and sought successfully. The task-owned player was stopped and active test playback sessions returned to zero. |
| Restart | Final installed package retained configuration/signing key, item IDs and a manual metadata field lock after cold sync. Both plain and archive playback passed afterward. |
| Release workflow | actionlint passed. Optional catalog generation produced matching version, ABI, ZIP URL and checksum in an isolated fixture. No public catalog was published. |

The locally validated release ZIP has SHA-256:

```text
f402904059f7fc8ad9875fd84446f86de17278f98837843e2a13bee36ca5546c
```

CI repackages the source with its build timestamp, so its ZIP checksum can differ. Always use the
checksum distributed alongside the exact ZIP being installed.

## Release scope

Ready for private ZIP distribution on Jellyfin 12.0 within the documented naming and archive scope.
The repository remains private. Public catalog installation and automatic updates require a separately
chosen, reachable artifact host; they were not represented as tested public distribution. See
[release operations](RELEASING.md) and [the README](../README.md) for configuration, migration,
bearer-URL handling and limitations. No version tag or public release was created by this review.
