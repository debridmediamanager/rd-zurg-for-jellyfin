# 1.0.6.0

- Stop reading a resolution as a film's year. Jellyfin's name parser reads any number from 1900 to 2099 as a year, so the width in `1920x1080` or `2048x858` became one, and it wins over a real year earlier in the name. `[Aenianos] Fruits Basket 2nd Season - 03 [BD 1920x1080 ...]` was filed as a film from 1920, which TMDb never matches, and `Forbidden.Zone.1980.1920x1080` as one from 1920 rather than 1980. A frame size whose width looks like a year is now read as `1080p` before Jellyfin sees the name. Replayed over the 28,509 names in DMM's RD and AllDebrid availability that carry the shape, 24,965 no longer take a width for their year, no other year changed, and no series name changed. An episode such as `Formula.1.2025x126` and a year glued to its codec such as `2006x264` read as before.
- Correct films an earlier build filed under that year. A film whose name and year are still exactly what the old parse read, that no metadata provider has matched and whose name is not locked, gets the name and year the new parse reads, and its metadata is looked up again. Its id, watch state and versions stay. On a copy of a real library of 6,497 items, only the Fruits Basket release changed, its watch state stayed, and a second pass changed nothing.

# 1.0.5.0

- Refile a film an earlier build filed as an episode. A sync never reads a torrent whose links are all in the library again, so a film that 1.0.4.0 and earlier read as a one-episode show stayed one. A torrent whose only item is such an episode is now let go of and added again as a film, and the season and show it leaves empty are removed. An item counts only when Jellyfin's stock expressions reproduce its exact season and episode from the release and file name and the current parser reads none, so a real episode is never touched and a refiled film cannot come back as an episode. A pack of films filed as episodes keeps its episodes, because a torrent without episodes publishes only its biggest file. Replayed on a real library: BTCC and The Odyssey became films, both Matrix packs and 163 real episodes stayed, and a second pass changed nothing.

# 1.0.4.0

- Stop reading a film's audio, frame-rate or resolution tag, or a collection's year range, as a season and episode. Jellyfin's bare `([0-9]+)-([0-9]+)` expression read `AC3-2.0`, `4.17-60fps`, `2026-1080p` and `1999-2021` as episodes, and its `NxNN` expressions read `5.1x265` as season 1 episode 265, so a film became a one-episode show. Replayed over 4.24 million release and file names from DMM's RD and AllDebrid availability, 11,541 files of films no longer read as episodes and no episode number changed. An item a sync already filed this way stays where it is.

# 1.0.3.0

- Reach alternate versions during sync. Jellyfin 12 leaves an item with a PrimaryVersionId out of every query that does not ask for owned items, so the sync never saw a film's other versions: it asked Real-Debrid about each of their torrents again on every pass, never refreshed their playback URLs, never removed them when their torrent went, and saved each film with no versions at all. Measured on a real library: 19 of 191 films still named any of their 623 versions, and a pass recognised 2,740 of 3,364 torrents in 3m20s. With this release it recognises 3,047 and a steady pass takes 1m48s.
- Remove the second copy of a file that 1.0.0.0 left behind. It derived an item's id from the playback URL, and a later pass that could not see such an item added the file again under the id derived from the link. Measured on a real library: 327 films held twice, the older copy still on an unsigned URL that answers 401.
- Rebuild each film's versions on every pass, and let go of a release an earlier build filed as a version when the current rules would not merge it. The grouping reads the release name from the path this plugin wrote, which no metadata refresh touches, so this cannot flap.
- Let go of a film's versions before deleting it. Jellyfin 12 deletes a film's linked versions along with it when their path is not a file on disk, and every path here is a URL, so a release still in the account went with the film it was filed under.

# 1.0.2.0

- Group alternate versions on the year parsed from the release name rather than the item's ProductionYear. A metadata provider writes a year onto absolute-numbered anime that carries none, after which the existing guard passed and distinct episodes merged into one film. Measured on a real account: 155 One Piece episodes folded into a single item with 154 versions.

# 1.0.1.0

- Require signed per-file playback URLs before using the Real-Debrid account. Existing library paths migrate on sync without changing item IDs.
- Preserve library entries during limited or failed syncs. Refuse cleanup when pagination is incomplete or changes during the listing.
- Stream through the server by default. Direct CDN redirects are an explicit option for players using the same public IP.
- Bound archive probes and stream reads, validate upstream ranges, reject unsupported archives, and retain cached links during player seeks.
- Validate settings, protect unrelated libraries, preserve provider IDs when merging versions, and refresh existing playback URLs after configuration changes.
- Produce versioned ZIP packages with SHA-256 checksums and verify package compatibility in CI. Version tags prepare draft releases.
