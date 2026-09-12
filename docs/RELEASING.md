# Release operations

Pushing `main` runs verification and uploads a build artifact; it does not install the plugin on
any server. Tags matching `v<project Version>` run the same checks and create a **draft** GitHub
release with a ZIP and SHA-256 checksum. A mismatched tag fails. Publishing a draft is a separate step.

## Distribution

This repository is private. Authorized users can download ZIPs from its Actions artifacts or
releases and install them manually. Jellyfin's catalog downloader cannot authenticate to private
GitHub release assets. A manually installed plugin without a registered catalog can show
“repository unknown” or a repository-details error in Jellyfin 12; its Settings link remains usable.

For catalog distribution, first choose an HTTPS host that Jellyfin can access without interactive
authentication, then build with `RELEASE_BASE_URL=https://your-host.example/plugins ./build.sh`.
This produces `artifacts/manifest.json` with the exact ZIP URL, version, ABI and Jellyfin-required MD5
checksum. SHA-256 remains available alongside the ZIP. Host that manifest and the unchanged ZIP
together, verify their URLs from the target server, then add the manifest URL in Jellyfin's plugin
repositories. Rebuilding the ZIP changes its checksum; regenerate the manifest whenever doing so.
The generated manifest contains only the current version; retain earlier entries when maintaining
a catalog across Jellyfin ABI versions. No public hosting or repository-visibility change is implicit.

## Plugin identity in the dashboard

The packaged `thumb.png` is what Jellyfin shows on the plugin's page: `meta.json` names it as
`imagePath`, the server reports `HasImage`, and it is served from
`/Plugins/{guid}/{version}/Image` without needing a catalog.

**Developer** and **Repository** are different. Jellyfin reads those only from a plugin repository's
manifest, so a manually installed plugin shows "Unknown" for both and a warning that the plugin
details could not be read from the repository. That is cosmetic and the plugin works, but the only
way to fill those fields is to publish a catalog and register its URL - see Distribution above.

## Publishing to the DMM catalog

A version tag publishes the built package to DMM's sponsor-gated Jellyfin plugin repository, so a
sponsor's Jellyfin offers the new version in its catalog. The release job posts the ZIP and its card
image to `/api/plugins/publish` with the `DMM_PUBLISH_TOKEN` repository secret; DMM stores them,
computes the checksum from the bytes it stored, and merges this plugin's entry into the shared
catalog without touching the other three.

Publishing runs **before** the draft release is created, because an unpublished draft is easy to
recover from and a release that never reached the catalog is the failure worth catching. A push to
`main` publishes nothing.

To publish by hand, from a checkout of the DMM repository:

```bash
DMM_PUBLISH_TOKEN=… npx tsx scripts/publish-jellyfin-plugins.ts --apply path/to/<slug>_<version>.zip
```

Either way the unpacked `artifacts/<slug>_<version>/` directory has to sit beside the ZIP, because
that is where `meta.json` and the image are read from.

## Before tagging a version

1. Update the project `Version` and `CHANGELOG.md`. Run the Release test suite, `./build.sh` and
   `python3 scripts/verify-package.py`; inspect skipped tests and failures.
2. Install the **ZIP contents** into an isolated Jellyfin 12.0 server, with a test account, separate
   data/cache directories and a dedicated port. Verify plugin version and Active status after restart.
3. Use the dashboard to load, save and reload configuration. Sync a bounded real account sample,
   reduce the limit, and verify existing entries remain. Resync and run a normal library scan;
   compare item IDs and provider associations, including movie versions and season packs.
4. Verify unsigned/tampered URLs fail, signed HEAD and range/suffix requests work, invalid ranges
   return 416, and ffmpeg can probe and decode a real plain file and stored RAR after a seek.
   Exercise actual Jellyfin playback, then stop the task-owned player/session.
5. Confirm configuration and item IDs survive restart, review the diff for secrets, commit as
   `Ben Adrian Sarmiento <me@bensarmiento.com>`, and push `main` only after all required checks pass.
   Confirm CI success for that exact commit before creating a version tag or publishing a release.

Workspace testing uses isolated targets on zen. Its regular Jellyfin, Plex and zurg services are
production and are not test targets. Never clear a provider library to test cleanup; inject
failure responses in the automated tests and manipulate only task-owned Jellyfin test data.

## Upgrade and rollback

From 1.0.3.0 the sync rebuilds every film's versions on each pass from the release names in the
paths it wrote. A merge an earlier build made that the current rules refuse - absolute-numbered anime
folded into one film, say - is let go of on the first sync after upgrading, and a file 1.0.0.0 added
twice loses its older copy. Builds before 1.0.3.0 never undid a merge.

From 1.0.5.0 the first sync after upgrading refiles a film an earlier build filed as a one-episode show,
when that film is its torrent's only item: the episode, and the season and show it leaves empty, go, and
the same pass adds the film. A pack of films filed as episodes keeps its episodes, because a torrent with
no episodes publishes only its biggest file. Check the sync's log line for how many were refiled.

Back up Jellyfin data and plugin configuration before upgrading. Install the new version alongside
the old version while Jellyfin is stopped; Jellyfin selects the newer compatible plugin. Restart,
confirm the loaded version, and run the plugin sync to migrate paths. Keep the backup until playback
and library identity checks pass. Jellyfin server version changes need their own database migration
plan; this plugin package does not upgrade the server.

To roll back plugin code, stop Jellyfin and move the new plugin directory outside `plugins`, then
restore the previous plugin and any required configuration/data backup. Do not use force-push to
rewrite a released main commit; use a revert and a newly verified release. The original 1.0.0.0
build has unsigned playback and unsafe cleanup defects and is not a production rollback target.
