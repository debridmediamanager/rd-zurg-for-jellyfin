#!/usr/bin/env python3
"""Verify the installable archive, manifest compatibility and SHA-256 digest."""
import hashlib
import json
import pathlib
import sys
import xml.etree.ElementTree as ET
import zipfile

root = pathlib.Path(__file__).resolve().parents[1]
project = ET.parse(root / 'src/Jellyfin.Plugin.RdZurg/Jellyfin.Plugin.RdZurg.csproj')
version = sys.argv[1] if len(sys.argv) > 1 else project.findtext('.//Version')
archive = root / 'artifacts' / ('rd-zurg_' + version + '.zip')
with zipfile.ZipFile(archive) as bundle:
    assert sorted(bundle.namelist()) == ['Jellyfin.Plugin.RdZurg.dll', 'meta.json', 'thumb.png'], 'Unexpected packaged dependency or file'
    assert bundle.testzip() is None, 'Corrupt ZIP entry'
    metadata = json.loads(bundle.read('meta.json'))
    assert metadata['version'] == version
    assert metadata['imagePath'] == 'thumb.png'
    assert bundle.read('thumb.png')[:8] == b'\x89PNG\r\n\x1a\n', 'The plugin image is not a PNG'
    assert metadata['guid'] == '4d0b1a37-1f1c-4a3e-9f5c-2e6a7b8c9d01'
    abi = project.find('.//PackageReference[@Include="Jellyfin.Controller"]').get('Version') + '.0'
    assert metadata['targetAbi'] == abi
    assert bundle.read('Jellyfin.Plugin.RdZurg.dll') == (root / 'src/Jellyfin.Plugin.RdZurg/bin/Release/net10.0/Jellyfin.Plugin.RdZurg.dll').read_bytes()
assert hashlib.sha256(archive.read_bytes()).hexdigest() == archive.with_suffix('.zip.sha256').read_text().split()[0]
print('Verified package: ' + archive.name)
