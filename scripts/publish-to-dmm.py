#!/usr/bin/env python3
"""Post a built package to DMM's sponsor-gated Jellyfin plugin catalog.

    python3 scripts/publish-to-dmm.py artifacts/<slug>_<version>.zip

DMM owns the bucket and the catalog document, so this only hands over the
package: the server stores it, computes its checksum and merges the entry,
leaving the other three plugins alone. Standard library only, because a release
runner should not need to install anything to publish.

  DMM_PUBLISH_TOKEN  required, the publisher credential
  DMM_PUBLISH_URL    optional, for pointing at something other than production
"""
import base64
import json
import os
import pathlib
import sys
import urllib.error
import urllib.request

DEFAULT_URL = 'https://debridmediamanager.com'


def main() -> int:
    if len(sys.argv) != 2:
        print('Usage: publish-to-dmm.py <plugin.zip>', file=sys.stderr)
        return 2

    archive = pathlib.Path(sys.argv[1])
    if archive.suffix != '.zip':
        print(f'{archive} is not a .zip', file=sys.stderr)
        return 2

    # build.sh writes the unpacked directory beside the archive.
    unpacked = archive.with_suffix('')
    try:
        meta = json.loads((unpacked / 'meta.json').read_text())
    except OSError:
        print(f'No {unpacked.name}/meta.json beside {archive.name}', file=sys.stderr)
        return 1

    payload = {
        'file': archive.name,
        'zip': base64.b64encode(archive.read_bytes()).decode('ascii'),
        'meta': meta,
    }
    image = meta.get('imagePath')
    if image:
        payload['image'] = base64.b64encode((unpacked / image).read_bytes()).decode('ascii')

    token = os.environ.get('DMM_PUBLISH_TOKEN')
    if not token:
        print('DMM_PUBLISH_TOKEN is not set', file=sys.stderr)
        return 1

    base = os.environ.get('DMM_PUBLISH_URL', DEFAULT_URL).rstrip('/')
    request = urllib.request.Request(
        base + '/api/plugins/publish',
        data=json.dumps(payload).encode('utf-8'),
        headers={'Content-Type': 'application/json', 'x-publish-token': token},
        method='POST',
    )

    try:
        with urllib.request.urlopen(request, timeout=180) as response:
            print(f'{response.status} {response.read().decode("utf-8")[:600]}')
    except urllib.error.HTTPError as error:
        # The body says which check failed; the token is never echoed back.
        print(f'HTTP {error.code}: {error.read().decode("utf-8")[:600]}', file=sys.stderr)
        return 1
    except urllib.error.URLError as error:
        print(f'Could not reach {base}: {error.reason}', file=sys.stderr)
        return 1

    return 0


if __name__ == '__main__':
    raise SystemExit(main())
