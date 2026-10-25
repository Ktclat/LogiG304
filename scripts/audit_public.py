"""Fail closed on accidental binaries, profiles, machine identifiers or credentials."""
from pathlib import Path
import re
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[1]
def main():
    raw=subprocess.check_output(['git','ls-files','-z','--cached','--others','--exclude-standard'],cwd=ROOT)
    files=sorted(set(p.decode('utf-8') for p in raw.split(b'\0') if p))
    errors=[];total=0
    forbidden={'.exe','.dll','.sys','.asar','.zip','.7z','.db','.sqlite','.sqlite3','.log','.jsonl','.png','.jpg','.pdb'}
    patterns=[
        ('machine SID',r'S-1-5-21-\d+-\d+-\d+-\d+'),
        ('personal Windows home',r'(?i)[A-Z]:[\\/]+Users[\\/]+[A-Za-z0-9_.-]+'),
        ('GitHub credential',r'gh[pousr]_[A-Za-z0-9]{30,}'),
        ('private key',r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    ]
    for name in files:
        path=ROOT/name
        if not path.is_file():errors.append((name,'not a regular file'));continue
        if path.is_symlink():errors.append((name,'symlink'));continue
        if path.suffix.lower() in forbidden:errors.append((name,'binary or private artifact'));continue
        if any(p.lower() in {'private','payload','artifacts','data','backups','seed','node_modules'} for p in path.relative_to(ROOT).parts):errors.append((name,'private/generated directory'));continue
        data=path.read_bytes();total+=len(data)
        if path.suffix.lower()=='.ps1' and any(b>127 for b in data) and not data.startswith(b'\xef\xbb\xbf'):
            errors.append((name,'PowerShell 5.1 requires a UTF-8 BOM for this non-ASCII script'))
        if len(data)>1024**2:errors.append((name,'unexpected large file'));continue
        try:text=data.decode('utf-8-sig')
        except UnicodeDecodeError:errors.append((name,'not UTF-8 text'));continue
        if '\0' in text:errors.append((name,'binary content'));continue
        for label,pattern in patterns:
            if re.search(pattern,text):errors.append((name,label))
    if errors:
        for name,reason in errors:print(f'REJECT {name}: {reason}',file=sys.stderr)
        return 1
    print(f'PASS: {len(files)} public text files; {total} bytes; no binary payload, personal home, machine SID or credential pattern.')
    return 0
if __name__=='__main__':raise SystemExit(main())
