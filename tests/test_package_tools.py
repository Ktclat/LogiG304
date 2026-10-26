# SPDX-FileCopyrightText: 2026 Ktclat
# SPDX-License-Identifier: AGPL-3.0-only
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import package_tools as package


class PublicPackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.artifacts = Path(self.temp.name)
        for name in package.BINARIES:
            (self.artifacts / name).write_bytes(b'MZtest-tool' if name.endswith('.exe') else b'<configuration/>')
        source = io.BytesIO()
        with zipfile.ZipFile(source, 'w') as archive:
            for name in (*package.DOCUMENTS, package.GUIDE):
                archive.writestr(package.SOURCE_PREFIX + name, 'public source text')
        self.source = source.getvalue()
        self.commit = 'a' * 40

    def test_excludes_stale_payload_profiles_and_test_executables(self):
        for name in ('GHub-Local-Setup.exe', 'Setup.Development.exe', 'SandboxSmoke.Development.exe',
                     'private-source.zip', 'settings.db', 'lghub.exe', 'installer-tests.json'):
            (self.artifacts / name).write_bytes(b'PRIVATE-SENTINEL')
        (self.artifacts / 'Data').mkdir()
        (self.artifacts / 'Data' / 'settings.db').write_bytes(b'PRIVATE-SENTINEL')
        bundle = package.make_bundle(self.artifacts, self.source, self.commit)
        with zipfile.ZipFile(io.BytesIO(bundle)) as archive:
            for name in archive.namelist():
                self.assertNotIn(b'PRIVATE-SENTINEL', archive.read(name))
            self.assertEqual(sum(name.endswith('.exe') for name in archive.namelist()), 3)
            manifest = json.loads(archive.read('package-manifest.json'))
            self.assertFalse(manifest['isCompleteInstaller'])
            self.assertFalse(manifest['containsOfficialCore'])
            self.assertFalse(manifest['containsDrivers'])
            self.assertEqual(archive.read(package.SOURCE_ZIP), self.source)

    def test_missing_tool_fails_instead_of_shipping_partial_package(self):
        (self.artifacts / package.BINARIES[0]).unlink()
        with self.assertRaises((ValueError, FileNotFoundError)):
            package.make_bundle(self.artifacts, self.source, self.commit)

    def test_invalid_executable_is_rejected(self):
        (self.artifacts / package.TOOLS[0]).write_bytes(b'not an executable')
        with self.assertRaises(ValueError):
            package.make_bundle(self.artifacts, self.source, self.commit)

    def test_tampering_and_unlisted_files_are_rejected(self):
        original = package.make_bundle(self.artifacts, self.source, self.commit)
        for extra in (False, True):
            with self.subTest(extra=extra):
                changed = io.BytesIO()
                with zipfile.ZipFile(io.BytesIO(original)) as source, zipfile.ZipFile(changed, 'w') as target:
                    for name in source.namelist():
                        data = source.read(name)
                        if not extra and name == 'tools/' + package.TOOLS[0]:
                            data += b'tampered'
                        target.writestr(name, data)
                    if extra:
                        target.writestr('settings.db', b'private')
                with self.assertRaises(ValueError):
                    package.verify_bundle(changed.getvalue(), self.source, self.commit)

    def test_wrong_source_revision_or_source_archive_is_rejected(self):
        bundle = package.make_bundle(self.artifacts, self.source, self.commit)
        for source, commit in ((b'other source', self.commit), (self.source, 'b' * 40)):
            with self.assertRaises(ValueError):
                package.verify_bundle(bundle, source, commit)


if __name__ == '__main__':
    unittest.main()
