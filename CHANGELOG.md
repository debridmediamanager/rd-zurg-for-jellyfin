# 1.0.1.0

- Require signed per-file playback URLs before using the Real-Debrid account. Existing library paths migrate on sync without changing item IDs.
- Preserve library entries during limited or failed syncs. Refuse cleanup when pagination is incomplete or changes during the listing.
- Stream through the server by default. Direct CDN redirects are an explicit option for players using the same public IP.
- Bound archive probes and stream reads, validate upstream ranges, reject unsupported archives, and retain cached links during player seeks.
- Validate settings, protect unrelated libraries, preserve provider IDs when merging versions, and refresh existing playback URLs after configuration changes.
- Produce versioned ZIP packages with SHA-256 checksums and verify package compatibility in CI. Version tags prepare draft releases.
