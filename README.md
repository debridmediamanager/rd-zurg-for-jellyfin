# RD zurg for Jellyfin

A standalone Real-Debrid library plugin for **Jellyfin 12.0** and its .NET 10 runtime.
No filesystem mount, rclone process or separate zurg service is required. Compatibility with
other Jellyfin major versions is not implied.

## Install

This is a standalone plugin. It needs no plugin repository, no zurg service and none of the other
zurg plugins, so it can be installed on its own.

1. Download `rd-zurg_<version>.zip` and `rd-zurg_<version>.zip.sha256` from the
   [latest release](https://github.com/debridmediamanager/rd-zurg-for-jellyfin/releases/latest),
   then check the download with `sha256sum -c rd-zurg_<version>.zip.sha256`
   (`shasum -a 256 -c` on macOS).
2. Stop Jellyfin and back up its data directory.
3. Extract the ZIP into `<Jellyfin data>/plugins/rd-zurg_<version>/`. `Jellyfin.Plugin.RdZurg.dll`,
   `meta.json` and `thumb.png` must sit directly inside that directory, not in a subdirectory.
4. Start Jellyfin. Open **Dashboard → Plugins → RD zurg → Settings**.
5. Enter the private token from [Real-Debrid](https://real-debrid.com/apitoken) and your server URL.
6. Run **Dashboard → Scheduled Tasks → Sync Real-Debrid library**. The default schedule is every six hours.

For the official Docker image, the data directory is `/config`; native packages commonly use
`/var/lib/jellyfin`. Use the actual data path shown in your Jellyfin dashboard.

A plugin installed this way shows "Unknown" for Developer and Repository and a notice that its
details could not be read from a repository. Jellyfin only fills those fields from a plugin
catalog, so the notice is cosmetic and the Settings page works.

To upgrade, stop Jellyfin, delete the old `plugins/rd-zurg_<old version>/` directory, extract the
new release in its place and start Jellyfin again. Settings live outside that directory and are kept.

To build and install from source instead, see [Build and verification](#build-and-verification);
`./build.sh <Jellyfin data>` installs the plugin it builds.

Upgrading from 1.0.0.0 requires a sync before playback: old unsigned URLs are deliberately rejected.
The sync updates existing items in place, retaining their IDs and associated watch history.

## Configure

| Setting | Production behavior |
|---|---|
| API token | Use a private API token. OAuth access-token renewal is not implemented. |
| Server URL | An HTTP(S) address reachable by Jellyfin and its players, including any reverse-proxy base path. Use HTTPS outside a trusted network. Run a sync after changing it. |
| Torrent limit | `0` lists the whole account. A positive limit imports recent torrents and preserves entries outside that window. Start with a small limit to check naming and playback. |
| API interval | At least 300 ms between API calls. Link generation is separately spaced by five seconds. A 429 pauses new requests for ten minutes; it is not retried within a request. |
| Library names | Two distinct names, used on first creation. Existing libraries are identified by their owned directories; rename them through Jellyfin's library settings. Name collisions with unrelated libraries fail safely. |
| Remove vanished items | Cleanup only follows an unlimited, successful listing and sync. Incomplete, overlapping or changing pagination cannot authorize cleanup. Files and provider torrents are never deleted. |
| Merge releases | Movies with the same title and known year become selectable versions. Review metadata matches when names are ambiguous. |
| Look inside RAR archives | Supports complete, unencrypted stored video members whose headers are available in the initial 64 KiB. Compressed, split and encrypted archives are rejected. |
| Redirect plain files | **Off by default.** All bytes pass through Jellyfin's server connection. Enable only when server and players use the same public IP; archives still use the server. |

Configure media-scanning features deliberately: chapter images, trickplay and realtime filesystem
monitoring are disabled when these libraries are created. Enabling expensive extraction or running
many cold playback probes can increase bandwidth and provider requests.

## Design and access control

The plugin creates ordinary Jellyfin movie, series, season and episode records. Media paths point
to the plugin's HTTP endpoint, which resolves fresh provider URLs at playback time. Filesystem
scans leave the remote media records in place. Library anchor directories are owned by the plugin;
local media libraries are not used as import targets.

Playback URLs carry an HMAC-SHA256 signature scoped to one content key and the current account.
This lets ffmpeg and external players fetch an authorized file without a Jellyfin session and
prevents anonymous callers from spending the account token on arbitrary Real-Debrid links.
A signed URL is a bearer credential: anyone who receives it can play that file. Keep media URLs,
configuration backups and playback logs private. Changing `StreamSecret` to a new 32-byte random
hex value revokes old capabilities; changing the API token also revokes them. Run a sync afterward.
Removing an item from a library alone does not revoke an already shared URL.

Provider links are cached for 30 minutes, bounded to 1,024 entries and cleared when account or
playback settings change. Cold resolutions are serialized; warm reads do not wait for other
files to resolve. A player cancelling a read to seek retains its cached source. Provider failures
invalidate the affected source for the next request.

For an archive, the plugin translates media byte ranges to archive offsets. It validates the
upstream status, range and length before sending headers, bounds probe memory and response copies,
and applies a 30-second inactivity timeout. Invalid ranges return 416. Unsupported archives return
422; unavailable providers return 502. Missing setup returns 503 and invalid signatures return 401.

## Scope and limitations

- Real-Debrid only; no automatic torrent repair, re-addition or provider-account management.
- Episodes need recognizable season/episode naming. Absolute-numbered anime and ambiguous release
  names can require manual metadata correction; the plugin does not promise perfect classification.
- Archive compatibility is checked when a file is played, not during every library sync. Unsupported
  releases can appear in the library and return a controlled playback error.
- A complete sync costs listing calls plus detail calls for unknown contents. Season packs containing
  subtitles or other unimported files may need detail calls on subsequent passes.
- Disabling or uninstalling the plugin does not delete its library records. Their playback endpoints
  stop working; remove the plugin's libraries separately in Jellyfin if they are no longer wanted.

## Build and verification

Requires the .NET 10 SDK, Python 3 and Bash for packaging. Jellyfin host assemblies are compile-time
references and are not bundled inside the plugin ZIP.

```bash
dotnet test tests/Jellyfin.Plugin.RdZurg.Tests -c Release
./build.sh
python3 scripts/verify-package.py
```

`./build.sh /path/to/jellyfin/data` also installs the built DLL and metadata. Restart Jellyfin afterward.
`VERSION=1.0.2.0 ./build.sh` overrides both the assembly and package versions together. The project
file is the default version source. CI builds, runs tests, packages and verifies every main push.
See [release operations](docs/RELEASING.md) for distribution and the required live release checks.

## License

[GPL-3.0](LICENSE). The plugin builds against Jellyfin's own GPL-3.0 packages, so it carries the same
license.
