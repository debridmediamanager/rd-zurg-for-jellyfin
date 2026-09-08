#!/usr/bin/env python3
"""Package only the plugin assembly; Jellyfin supplies its own host dependencies."""
import datetime
import hashlib
import json
import os
import pathlib
import shutil
import sys
import urllib.parse
import zipfile

root, version = pathlib.Path(sys.argv[1]), sys.argv[2]
out = root / 'artifacts' / ('rd-zurg_' + version)
out.mkdir(parents=True, exist_ok=True)
assembly = 'Jellyfin.Plugin.RdZurg.dll'
shutil.copy2(root / 'src/Jellyfin.Plugin.RdZurg/bin/Release/net10.0' / assembly, out / assembly)
metadata = {
    'category': 'General',
    'changelog': 'Signed playback URLs, safe sync cleanup, bounded archive streaming and configuration validation.',
    'description': 'Your Real-Debrid library in Jellyfin, without a mount.',
    'guid': '4d0b1a37-1f1c-4a3e-9f5c-2e6a7b8c9d01',
    'name': 'RD zurg',
    'overview': 'Serves a Real-Debrid account as a Jellyfin library',
    'owner': 'debridmediamanager',
    'targetAbi': '12.0.0.0',
    'timestamp': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
    'version': version,
    'status': 'Active',
    'autoUpdate': False,
}
(out / 'meta.json').write_text(json.dumps(metadata, indent=2) + '\n')
archive = root / 'artifacts' / ('rd-zurg_' + version + '.zip')
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as bundle:
    for name in (assembly, 'meta.json'):
        bundle.write(out / name, name)
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix('.zip.sha256').write_text(digest + '  ' + archive.name + '\n')

# Private GitHub assets cannot be fetched by Jellyfin's anonymous catalog client.
# Supply an HTTPS host reachable by Jellyfin only when preparing catalog distribution.
base = os.environ.get('RELEASE_BASE_URL')
if base:
    uri = urllib.parse.urlsplit(base)
    if uri.scheme != 'https' or not uri.netloc or uri.username or uri.query or uri.fragment:
        raise SystemExit('RELEASE_BASE_URL must be an HTTPS URL without credentials, query or fragment')
    release = {key: metadata[key] for key in ('version', 'changelog', 'targetAbi', 'timestamp')}
    release.update(sourceUrl=base.rstrip('/') + '/' + archive.name,
                   checksum=hashlib.md5(archive.read_bytes(), usedforsecurity=False).hexdigest())
    catalog = {key: metadata[key] for key in ('category', 'description', 'guid', 'name', 'overview', 'owner')}
    catalog['versions'] = [release]
    (root / 'artifacts/manifest.json').write_text(json.dumps([catalog], indent=2) + '\n')
print('Packaged ' + str(archive))
