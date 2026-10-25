# SPDX-FileCopyrightText: 2026 Ktclat
# SPDX-License-Identifier: AGPL-3.0-only
"""Build a new G304 resource ZIP without copying profiles or live installations.

Input is a separately obtained, locally authorized resource archive. This does
not download resources or confer redistribution rights for vendor components.
"""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

from validate_payload import validate, validate_name, DEPOTS, ROOT_FILES, FILE_LIMIT, scan_private


def keep_resource(name):
    if name.startswith('App/'):
        return True
    if name in ('app.offline.asar', 'backup/app.original.asar', 'Seed/System/version.json'):
        return True
    parts = name.split('/')
    return len(parts) > 5 and parts[:4] == ['Seed', 'System', 'depots', '634218'] and parts[4] in DEPOTS


def templates(current, installation):
    current['depots'] = [d for d in current['depots'] if d['name'] in DEPOTS]
    found = {d['name'] for d in current['depots']}
    if found != DEPOTS:
        raise ValueError('A required G304/core depot is missing')
    for depot in current['depots']:
        if not set(depot.get('dependsOn', [])).issubset(found):
            raise ValueError('Missing core dependency')
        if not depot.get('localFolder') or depot.get('key') or depot.get('cipherSuite'):
            raise ValueError('Only already expanded, unencrypted resource depots are supported')
    installation['extensions'] = {
        'driver_hid_virtual': installation['extensions']['driver_hid_virtual']
    }
    # Source-machine extension status is not installation evidence for the new PC.
    for item in installation['extensions']['driver_hid_virtual']['extensions']:
        item['state'] = {'error': '', 'state': 'EXTENSION_NOT_INSTALLED'}
    for key in ('files', 'folders'):
        installation[key] = [item for item in installation[key]
                             if item['path'].replace('\\\\', '\\').startswith('${ROOT}\\LGHUB')]
    return {
        'Seed/System/current.template.json': current,
        'Seed/System/installation.template.json': installation,
    }


def build(source, tools, output, markers=()):
    if output.exists():
        raise ValueError('Output must be new; input archives are never overwritten')
    output.parent.mkdir(parents=True, exist_ok=True)
    manifest = {'version': '2025.9.814156', 'packageKind': 'g304-generic',
                'profilePolicy': 'new-user', 'depots': sorted(DEPOTS), 'files': {}}
    omitted = 0
    with zipfile.ZipFile(source) as src, zipfile.ZipFile(output, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as dst:
        names = src.namelist()
        if len({n.casefold() for n in names}) != len(names):
            raise ValueError('Duplicate input paths')
        original = json.loads(src.read('payload-manifest.json'))
        if original.get('version') != manifest['version']:
            raise ValueError('Unsupported source version')

        def read_verified(name):
            if src.getinfo(name).file_size > FILE_LIMIT:
                raise ValueError('Individual source resource size limit exceeded')
            data = src.read(name)
            if hashlib.sha256(data).hexdigest() != original['files'].get(name):
                raise ValueError('Source file hash mismatch: ' + name)
            return data

        def put(name, data):
            validate_name(name)
            scan_private(name, data, markers)
            # Normalize ZIP timestamps/permissions; never propagate user file metadata.
            info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            dst.writestr(info, data)
            manifest['files'][name] = hashlib.sha256(data).hexdigest()

        for name in names:
            if keep_resource(name):
                put(name, read_verified(name))
            else:
                omitted += 1
        for name, value in templates(
            json.loads(read_verified('Seed/System/current.template.json')),
            json.loads(read_verified('Seed/System/installation.template.json')),
        ).items():
            put(name, json.dumps(value, ensure_ascii=False, separators=(',', ':')).encode('utf-8'))
        for name in sorted(ROOT_FILES - {'app.offline.asar', 'backup/app.original.asar'}):
            put(name, (tools / name).read_bytes())
        raw = json.dumps(manifest, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode('utf-8')
        dst.writestr('payload-manifest.json', raw)
    result = validate(output, markers)
    result['omittedSourceEntries'] = omitted
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--tools', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--private-marker', action='append', default=[], help='Reject a known private identifier in UTF-8/UTF-16; never stored in reports')
    args = parser.parse_args()
    result = build(args.source, args.tools, args.output, args.private_marker)
    args.report.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result))
