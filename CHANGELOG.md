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
