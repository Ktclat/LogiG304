# SPDX-FileCopyrightText: 2026 Ktclat
# SPDX-License-Identifier: AGPL-3.0-only
"""Validate a separately authorized, non-personal resource ZIP. Never installs it."""
import hashlib
import json
from pathlib import PurePosixPath, Path
import re
import sys
import zipfile

LIMIT = 5 * 1024**3
FILE_LIMIT = 512 * 1024**2
DEPOTS = {'core', 'core_apps', 'core_assets', 'core_runtime_win', 'core_qt_win',
          'core_systray_win', 'gl', 'gl_resources', 'release_notes',
          'driver_hid_virtual', 'lua_scripting', 'g305'}
ROOT_FILES = {'app.offline.asar', 'backup/app.original.asar', 'LICENSE', 'NOTICE.md', 'THIRD_PARTY.md'}
for _tool in ('G HUB Offline.exe', '网络设置.exe', '卸载 G HUB 本地版.exe'):
    ROOT_FILES.update((_tool, _tool + '.config'))

def scan_private(name, data, markers=()):
    for marker in markers:
        if marker and any(marker.encode(codec).lower() in data.lower() for codec in ('utf-8', 'utf-16-le')):
            raise ValueError('Private identifier in resource: ' + name)
    if name.endswith(('.json', '.txt', '.md', '.config', '.license')):
        try:
            text = data.decode('utf-8-sig')
        except UnicodeDecodeError:
            # Some vendor device definitions have a binary header despite .json.
            # They are immutable resources, not profiles; still scan raw markers.
            if (re.fullmatch(r'Seed/System/depots/634218/core/data/(devices|receivers)/[a-z0-9_]+\.json', name)
                    and data.startswith(bytes.fromhex('2105212082000000'))):
                return
            raise ValueError('Unexpected non-text resource: ' + name)
        if re.search(r'(?i)[a-z]:[\\/]+Users[\\/]|S-1-5-21-\d+-\d+-\d+-\d+', text):
            raise ValueError('Personal path or machine SID in resource: ' + name)

def generic_path(name):
    if name in ROOT_FILES or name in ('payload-manifest.json', 'Seed/System/version.json',
                                      'Seed/System/current.template.json', 'Seed/System/installation.template.json'):
        return True
    if name.startswith('App/'):
        return True
    parts = name.split('/')
    return len(parts) > 5 and parts[:4] == ['Seed', 'System', 'depots', '634218'] and parts[4] in DEPOTS
def validate_name(name):
    parts=name.split('/')
    if not name or name.startswith('/') or '\\' in name or ':' in name:
        raise ValueError('Invalid archive path')
    if any(p in ('','.','..') or p.endswith((' ','.')) or re.match(r'(?i)^(CON|NUL|AUX|PRN|COM[1-9]|LPT[1-9])(?:\.|$)',p) for p in parts):
        raise ValueError('Unsafe archive path')
    lower=name.lower()
    if lower.startswith(('data/','backups/','seed/local/','seed/roaming/')) or re.search(r'\.(db(?:-.*)?|sqlite\d*|log|jsonl)$',lower):
        raise ValueError('User data or logs are not allowed in a distributable resource input')
    if lower in ('seed/system/keys.json','seed/system/groups.json','installation-journal.json','install-state.json'):
        raise ValueError('Machine or installation state is not allowed')

def validate(path, markers=()):
    with zipfile.ZipFile(path) as archive:
        info=archive.getinfo('payload-manifest.json')
        if info.file_size>1024**2:raise ValueError('Manifest too large')
        raw=archive.read(info)
        manifest=json.loads(raw)
        if manifest.get('version')!='2025.9.814156':raise ValueError('Unsupported vendor core version')
        if manifest.get('packageKind')!='g304-generic' or manifest.get('profilePolicy')!='new-user':raise ValueError('Not a generic, fresh-profile package')
        files=manifest.get('files')
        if not isinstance(files,dict) or not files:raise ValueError('Missing file inventory')
        required={'App/lghub.exe','App/lghub_agent.exe','App/lghub_updater.exe','App/resources/app.asar','app.offline.asar','backup/app.original.asar','G HUB Offline.exe','网络设置.exe','卸载 G HUB 本地版.exe','Seed/System/current.template.json','Seed/System/installation.template.json'}
        required.update(ROOT_FILES)
        required.update({'Seed/System/depots/634218/driver_hid_virtual/virtual_driver_manager.exe', 'Seed/System/depots/634218/g305/manifest.json', 'Seed/System/depots/634218/g305/metadata.json'})
        if not required.issubset(files):raise ValueError('Required resources are missing')
        seen=set();size=0;count=0
        for item in archive.infolist():
            validate_name(item.filename)
            if not generic_path(item.filename):raise ValueError('Resource outside generic allowlist: '+item.filename)
            folded=item.filename.casefold()
            if folded in seen:raise ValueError('Duplicate archive path')
            seen.add(folded)
            if item.is_dir() or (item.external_attr>>16)&0o170000==0o120000:raise ValueError('Directory or symlink entry is not allowed')
            if item.file_size>FILE_LIMIT:raise ValueError('Individual resource size limit exceeded')
            size+=item.file_size
            if size>LIMIT:raise ValueError('Resource size limit exceeded')
            if item.filename=='payload-manifest.json':continue
            expected=files.get(item.filename)
            if not isinstance(expected,str) or not re.fullmatch('[a-f0-9]{64}',expected):raise ValueError('Invalid file digest')
            digest=hashlib.sha256()
            data=archive.read(item)
            digest.update(data)
            scan_private(item.filename,data,markers)
            if digest.hexdigest()!=expected:raise ValueError('File hash mismatch: '+item.filename)
            if item.filename.endswith('.template.json'):
                text=archive.read(item).decode('utf-8-sig')
                if re.search(r'(?i)[a-z]:[\\/]+Users[\\/]|S-1-5-21-',text):raise ValueError('Personal paths or machine SIDs in template')
            count+=1
        if count!=len(files):raise ValueError('Incomplete file inventory')
        return {'success':True,'packageKind':'g304-generic','profilePolicy':'new-user','files':count,'uncompressedBytes':size,'manifestSha256':hashlib.sha256(raw).hexdigest(),'redistributionRightsChecked':False,'cleanMachineInstallValidated':False}

if __name__=='__main__':
    if len(sys.argv)!=3:raise SystemExit('Usage: validate_payload.py input.zip report.json')
    result=validate(sys.argv[1])
    Path(sys.argv[2]).write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps(result))
