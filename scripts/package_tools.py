# SPDX-FileCopyrightText: 2026 Ktclat
# SPDX-License-Identifier: AGPL-3.0-only
"""Package only freshly built project tools; never recurse through artifacts/."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import stat
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ('G HUB Offline.exe', '网络设置.exe', '卸载 G HUB 本地版.exe')
BINARIES = tuple(name + suffix for name in TOOLS for suffix in ('', '.config'))
DOCUMENTS = ('LICENSE', 'NOTICE.md', 'THIRD_PARTY.md')
TOOLS_ZIP = 'LogiG304-Tools-Windows-x64.zip'
SOURCE_ZIP = 'LogiG304-Source.zip'
GUIDE = 'docs/DOWNLOADS.md'
SOURCE_PREFIX = 'LogiG304-source/'
MAX_FILE = 16 * 1024 * 1024


def digest(data):
    return hashlib.sha256(data).hexdigest()


def regular_file(path):
    # Reject junctions as well as symlinks, including linked parent directories.
    for part in (path, *path.parents):
        info = part.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('Linked package input or output: ' + part.name)
    if not path.is_file() or not 0 < path.stat().st_size <= MAX_FILE:
        raise ValueError('Missing, empty or oversized input: ' + path.name)
    return path.read_bytes()


def make_bundle(artifacts, source, commit):
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('A full source commit is required.')
    with zipfile.ZipFile(io.BytesIO(source)) as archive:
        docs = {name: archive.read(SOURCE_PREFIX + name) for name in DOCUMENTS}
        docs['使用说明.md'] = archive.read(SOURCE_PREFIX + GUIDE)
    files = {**docs, SOURCE_ZIP: source}
    for name in BINARIES:
        data = regular_file(artifacts / name)
        if name.endswith('.exe') and not data.startswith(b'MZ'):
            raise ValueError('Not a Windows executable: ' + name)
        files['tools/' + name] = data
    manifest = {
        'schemaVersion': 1,
        'packageKind': 'project-tools-only',
        'sourceCommit': commit,
        'sourceRepository': 'https://github.com/Ktclat/LogiG304',
        'containsOfficialCore': False,
        'containsDrivers': False,
        'isCompleteInstaller': False,
        'supportedCoreVersion': '2025.9.814156',
        'files': {name: digest(data) for name, data in sorted(files.items())},
    }
    files['package-manifest.json'] = (json.dumps(manifest, ensure_ascii=False, indent=2) + '\n').encode()
    result = io.BytesIO()
    with zipfile.ZipFile(result, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            entry = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, data)
    bundle = result.getvalue()
    verify_bundle(bundle, source, commit)
    return bundle


def verify_bundle(bundle, source, commit):
    expected = {*DOCUMENTS, '使用说明.md', SOURCE_ZIP, 'package-manifest.json'}
    expected.update('tools/' + name for name in BINARIES)
    with zipfile.ZipFile(io.BytesIO(bundle)) as archive:
        if len(archive.namelist()) != len(expected) or set(archive.namelist()) != expected:
            raise ValueError('Unexpected or duplicate files in public tools ZIP.')
        manifest = json.loads(archive.read('package-manifest.json'))
        if manifest['sourceCommit'] != commit or manifest['isCompleteInstaller'] is not False:
            raise ValueError('Incorrect package identity.')
        hashes = manifest['files']
        if set(hashes) != expected - {'package-manifest.json'}:
            raise ValueError('Incomplete package hash inventory.')
        for name, value in hashes.items():
            if digest(archive.read(name)) != value:
                raise ValueError('Package hash mismatch: ' + name)
        if archive.read(SOURCE_ZIP) != source:
            raise ValueError('Corresponding source does not match.')


def release_notes(commit):
    return f'''自有辅助工具 · Windows x64 · 源码版本 `{commit}`

下载 `LogiG304-Tools-Windows-x64.zip` 可获得已编译的辅助程序、使用说明、完整对应源码与许可；无需自己编译这些工具。

**这是辅助工具包，不是完整 G304 安装器。** 不含罗技官方核心、驱动、设备资源或个人配置，不能用于全新电脑首次安装 G HUB。它只配套本项目固定版本的本地安装环境；直接装有官方 G HUB 也不等于满足条件。请先阅读 ZIP 中的 `使用说明.md`，不要直接替换正在使用的安装目录。

`LogiG304-Source.zip` 是相同提交的完整公开源码。`SHA256SUMS.txt` 可用于核对两个 ZIP 的下载完整性。自有代码按 AGPL-3.0-only 发布，第三方权利不受此许可覆盖。

本次构建已运行隔离回归测试、前端策略测试、资源与隐私校验；不表示在全新系统上验证了实体 G304。程序未做商业代码签名。
'''


def package(root, output):
    def git(*args):
        return subprocess.check_output(['git', *args], cwd=root)
    if git('status', '--porcelain', '--untracked-files=all').strip():
        raise ValueError('Commit all public changes before packaging corresponding source.')
    commit = git('rev-parse', 'HEAD').decode().strip()
    subprocess.run([sys.executable, str(root / 'scripts/audit_public.py')], cwd=root, check=True)
    # Archive the exact commit, not a directory that might contain ignored resources.
    source = git('archive', '--format=zip', '--prefix=' + SOURCE_PREFIX, commit)
    bundle = make_bundle(root / 'artifacts', source, commit)
    notes = release_notes(commit).encode('utf-8')
    assets = {TOOLS_ZIP: bundle, SOURCE_ZIP: source}
    checksums = ''.join(f'{digest(data)}  {name}\n' for name, data in sorted(assets.items()))
    # A new output directory prevents mixed releases and accidental overwrites.
    output.mkdir(parents=True, exist_ok=False)
    for name, data in {**assets, 'SHA256SUMS.txt': checksums.encode(), 'release-notes.md': notes}.items():
        with (output / name).open('xb') as target:
            target.write(data)
    print(json.dumps({'sourceCommit': commit, 'files': sorted(assets), 'completeInstaller': False}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/downloads')
    args = parser.parse_args()
    package(ROOT, args.output)


if __name__ == '__main__':
    main()
