# 1.0.2.0

- Group alternate versions on the year parsed from the release name rather than the item's ProductionYear. A metadata provider writes a year onto absolute-numbered anime that carries none, after which the existing guard passed and distinct episodes merged into one film. Measured on a real account: 155 One Piece episodes folded into a single item with 154 versions.

# 1.0.1.0

- Require signed per-file playback URLs before using the Real-Debrid account. Existing library paths migrate on sync without changing item IDs.
- Preserve library entries during limited or failed syncs. Refuse cleanup when pagination is incomplete or changes during the listing.
- Stream through the server by default. Direct CDN redirects are an explicit option for players using the same public IP.
- Bound archive probes and stream reads, validate upstream ranges, reject unsupported archives, and retain cached links during player seeks.
- Validate settings, protect unrelated libraries, preserve provider IDs when merging versions, and refresh existing playback URLs after configuration changes.
- Produce versioned ZIP packages with SHA-256 checksums and verify package compatibility in CI. Version tags prepare draft releases.
