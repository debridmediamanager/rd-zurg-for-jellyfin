# Release validation: 1.0.3.0

Validated on 2026-09-12 (UTC) against Jellyfin 12.0 on **zen**. The run used a second instance of
zen's own Jellyfin 12.0 binary with separate data, config, cache and log directories on port 18099,
holding a copy of the rig's database and only this plugin. Real-Debrid was read with the workspace
test account; nothing was deleted from the account.

## Defects reproduced before fixing

- Jellyfin 12 leaves an item with a PrimaryVersionId out of every query that does not set
  IncludeOwnedItems (`BaseItemRepository.ApplyAccessFiltering`), so the sync never saw an alternate
  version. On the rig only 25 of 623 versions were still named by their film and 19 of 191 films named
  any; the merge pass had found each film alone and saved it with none. A 1.0.2.0 pass recognised
  2,740 of 3,364 torrents and took 3m20s.
- 327 films were held twice. 1.0.0.0 derived an item's id from its playback URL and 1.0.1.0 from the
  link, and a pass that could not see the older item added the file again. The older copy stayed on
  an unsigned URL: 5 of 5 probed answered `401` while its twin answered `206`. Each of 151 sampled
  rows matched exactly one of the two ids, computed with Jellyfin's own item-id formula.
- `AlternateVersionSyncTests` replays a fixture read from that library and from the account. Against
  the 1.0.2.0 source all six replay tests fail; with this release they pass.

## Passing checks

| Area | Evidence |
|---|---|
| Build | .NET 10 Release build: zero warnings and zero errors. |
| Automated tests | 86 passed, zero failed, zero skipped. |
| Package | `scripts/verify-package.py` verified `rd-zurg_1.0.3.0.zip`. |
| Installation | The isolated server loaded `RD zurg 1.0.3.0`. |
| First sync | 3,047 of 3,364 torrents recognised. Removed 327 copies added twice by an earlier build and 0 items gone from the account; added nothing. 1,925 films and 3,274 episodes remain, the counts the rig's first full sync produced. |
| Library state | 0 duplicate stream paths, 0 unsigned playback URLs, 0 items without their link id. 296 versions under 167 films, every film naming exactly the versions filed under it and no version naming any; 0 yearless releases filed as or with versions. |
| Playback | Five versions: signed `206` starting with an EBML header, unsigned `401`, forged `401`. |
| Second sync | Idempotent: 0 folded, 0 released, 0 removed, in 1m48s. |

317 torrents are still listed on every pass, by design and unchanged by this release: 244 single-file
torrents whose file the library already holds under another torrent, and 73 multi-file torrents
holding files that never become items.

## Release scope

**Not covered by this run.** The rig had no torrent gone from the account, so deleting a film with
linked versions was exercised only by the replay test, against a model of Jellyfin's
`LibraryManager.DeleteItem`. A real player was not driven end to end; playback was exercised with range
requests against the plugin's own endpoint.
