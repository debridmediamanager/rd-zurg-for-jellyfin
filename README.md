# rd-zurg-for-jellyfin

Your Real-Debrid library in Jellyfin. No mount, no rclone, no second service.

The plugin injects your account into two Jellyfin libraries as ordinary movies and episodes. Each
item points at an endpoint the plugin itself serves, which mints a fresh Real-Debrid link at the
moment you press play. Nothing time-limited is stored, so links cannot rot in the database.

## What it does

- Builds **Movies** and **Shows** libraries from your account. Series, seasons and episodes, not a
  flat pile of files.
- **Redirects** for a plain release, so the bytes go straight from Real-Debrid's CDN to your player
  and the server carries none of them.
- **Reads through RAR archives.** Real-Debrid regularly serves a release as a RAR without saying so.
  Its file list claims `X.mkv` and the link serves `X.mkv.rar`. On a sample of 32 torrents this was
  10 of them. Those are served from inside the archive instead of being handed to a player that
  cannot open them.
- **One entry per film.** Byte-identical re-adds are dropped; genuinely different releases of the
  same title become selectable versions of one item.
- **Incremental.** A resync only fetches details for new arrivals. On a 400 torrent account a first
  pass takes about two minutes and every pass after that takes thirty seconds.
- **Tidies up.** Items whose torrent is gone from the account are removed.
- **Paced.** Real-Debrid publishes 250 requests a minute and counts refused ones against the
  allowance, so every call goes through one gate and a 429 stops the pass rather than retrying.

## Install

Requires Jellyfin 12.0 or newer.

```bash
./build.sh /path/to/jellyfin/config
```

Then restart Jellyfin, open **Dashboard → Plugins → RD zurg**, and set:

| Setting | What it is |
|---|---|
| API token | From [real-debrid.com/apitoken](https://real-debrid.com/apitoken) |
| Server URL | How this server is reached, e.g. `http://192.168.1.10:8096`. Your players resolve through it, so `localhost` is not enough |
| Torrent limit | How many of the newest torrents to take. 0 is all of them |

Then run **Dashboard → Scheduled Tasks → Sync Real-Debrid library**. It also runs every six hours.

## How it works

Jellyfin's library layer is filesystem-bound but its playback layer is not: a media source whose
protocol is not `File` is handed to ffmpeg, and to clients, as a URL. The plugin creates items whose
path is an `http://` URL rather than a file, which the scanner then leaves alone, because it only
enumerates and only reaps items whose protocol is `File`.

```
Jellyfin item path
  -> http://<server>/RdZurg/Stream/<link key>/<release>.mkv
       -> plain release      302 to the Real-Debrid CDN
       -> RAR-wrapped        served from inside the archive, range by range
```

A stored RAR member is a contiguous run of bytes inside the archive, so serving it is offset
arithmetic and every seek stays a range request against the CDN. Compressed and multi-volume
archives are detected and left out of the library rather than published as items that will not play.

## What it is not

- **Not multi-provider.** Real-Debrid only. TorBox, AllDebrid and Usenet are not here.
- **Not a mount.** Nothing outside Jellyfin can read this library. If you want Infuse, rclone, or an
  \*arr stack pointed at the same files, you want [zurg](https://github.com/debridmediamanager/zurg).
- **Not a repair tool.** A link that has aged out fails at playback and is retried on the next
  request. There is no background re-verification.
- **No compressed archives.** Only stored ones, which is what Real-Debrid actually serves.

## Development

```bash
dotnet build src/Jellyfin.Plugin.RdZurg -c Release
dotnet test tests/Jellyfin.Plugin.RdZurg.Tests
./build.sh
```

The RAR reader's tests run against the first 200 bytes of an archive exactly as Real-Debrid served
it, so the parser is pinned to real output rather than to a fixture someone generated.
