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
def validate_name(name):
    parts=name.split('/')
    if not name or name.startswith('/') or '\\' in name or ':' in name:
        raise ValueError('Invalid archive path')
    if any(p in ('','.','..') or p.endswith((' ','.')) or re.match(r'(?i)^(CON|NUL|AUX|PRN|COM[1-9]|LPT[1-9])(?:\.|$)',p) for p in parts):
        raise ValueError('Unsafe archive path')
    lower=name.lower()
    if lower.startswith(('data/','backups/','seed/local/')) or re.search(r'\.(db(?:-.*)?|sqlite\d*|log|jsonl)$',lower):
        raise ValueError('User data or logs are not allowed in a distributable resource input')
    if lower in ('seed/system/keys.json','seed/system/groups.json','installation-journal.json','install-state.json'):
        raise ValueError('Machine or installation state is not allowed')

def validate(path):
    with zipfile.ZipFile(path) as archive:
        info=archive.getinfo('payload-manifest.json')
        if info.file_size>1024**2:raise ValueError('Manifest too large')
        raw=archive.read(info)
        manifest=json.loads(raw)
        if manifest.get('version')!='2025.9.814156':raise ValueError('Unsupported vendor core version')
        files=manifest.get('files')
        if not isinstance(files,dict) or not files:raise ValueError('Missing file inventory')
        required={'App/lghub.exe','App/lghub_agent.exe','App/lghub_updater.exe','App/resources/app.asar','app.offline.asar','backup/app.original.asar','G HUB Offline.exe','网络设置.exe','卸载 G HUB 本地版.exe','Seed/System/current.template.json','Seed/System/installation.template.json'}
        if not required.issubset(files):raise ValueError('Required resources are missing')
        seen=set();size=0;count=0
        for item in archive.infolist():
            validate_name(item.filename)
            folded=item.filename.casefold()
            if folded in seen:raise ValueError('Duplicate archive path')
            seen.add(folded)
            if item.is_dir() or (item.external_attr>>16)&0o170000==0o120000:raise ValueError('Directory or symlink entry is not allowed')
            size+=item.file_size
            if size>LIMIT:raise ValueError('Resource size limit exceeded')
            if item.filename=='payload-manifest.json':continue
            expected=files.get(item.filename)
            if not isinstance(expected,str) or not re.fullmatch('[a-f0-9]{64}',expected):raise ValueError('Invalid file digest')
            digest=hashlib.sha256()
            with archive.open(item) as stream:
                for chunk in iter(lambda:stream.read(1024*1024),b''):digest.update(chunk)
            if digest.hexdigest()!=expected:raise ValueError('File hash mismatch: '+item.filename)
            if item.filename.endswith('.template.json'):
                text=archive.read(item).decode('utf-8-sig')
                if re.search(r'(?i)[a-z]:[\\/]+Users[\\/]|S-1-5-21-',text):raise ValueError('Personal paths or machine SIDs in template')
            count+=1
        if count!=len(files):raise ValueError('Incomplete file inventory')
        return {'success':True,'files':count,'uncompressedBytes':size,'manifestSha256':hashlib.sha256(raw).hexdigest(),'redistributionRightsChecked':False,'cleanMachineInstallValidated':False}

if __name__=='__main__':
    if len(sys.argv)!=3:raise SystemExit('Usage: validate_payload.py input.zip report.json')
    result=validate(sys.argv[1])
    Path(sys.argv[2]).write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps(result))
