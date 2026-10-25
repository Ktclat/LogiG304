# SPDX-FileCopyrightText: 2026 Ktclat
# SPDX-License-Identifier: AGPL-3.0-only
import sys
from pathlib import Path
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import make_generic_payload as generic
import validate_payload as payload


class GenericTests(unittest.TestCase):
    def test_profile_and_irrelevant_devices_are_omitted(self):
        for name in ('Seed/Local/LGHUB/settings.db', 'Seed/Local/LGHUB/integrations/a.exe',
                     'Seed/System/keys.json', 'Seed/System/groups.json',
                     'Seed/System/depots/634218/driver_logi_lamparray/driver.exe',
                     'Seed/System/depots/634218/wheel_sdk/sdk.exe', '使用说明.txt',
                     'G HUB Offline.exe'):
            with self.subTest(name=name):
                self.assertFalse(generic.keep_resource(name))

    def test_generic_resources_remain(self):
        for name in ('App/lghub.exe', 'App/msvcp140.dll', 'backup/app.original.asar',
                     'Seed/System/depots/634218/g305/metadata.json',
                     'Seed/System/depots/634218/driver_hid_virtual/virtual_driver_manager.exe'):
            self.assertTrue(generic.keep_resource(name))

    def test_private_content_scan(self):
        home = chr(92).join(('C:', 'Users', 'ExamplePerson', 'settings'))
        sid = 'S-1-5-21-' + '-'.join(('11', '22', '33', '1001'))
        for text in (home, sid):
            with self.assertRaises(ValueError):payload.scan_private('test.json', text.encode())
        for codec in ('utf-8', 'utf-16-le'):
            with self.assertRaises(ValueError):
                payload.scan_private('test.exe', 'PrivateMarker'.encode(codec), ['PrivateMarker'])

    def test_binary_catalog_exception_is_narrow(self):
        header = bytes.fromhex('2105212082000000')
        payload.scan_private('Seed/System/depots/634218/core/data/devices/devices_0003.json', header)
        with self.assertRaises(ValueError):payload.scan_private('Seed/System/version.json', header)
        with self.assertRaises(ValueError):payload.scan_private('unknown/settings.json', b'\x82')

    def test_templates_forget_machine_install_state(self):
        depots = [{'name': n, 'localFolder': '${PROGRAMDATA}/LGHUB/depots/634218/' + n, 'dependsOn': []} for n in payload.DEPOTS]
        current = {'depots': depots + [{'name': 'unrelated', 'localFolder': ''}]}
        installation = {'extensions': {'driver_hid_virtual': {'extensions': [{'state': {'state': 'EXTENSION_INSTALLED'}}]}, 'wheel_sdk': {}},
                        'files': [{'path': '${ROOT}\\LGHUB\\lghub.exe'}, {'path': '${LOCALAPPDATA}\\LGHUB\\settings.db'}], 'folders': []}
        result = generic.templates(current, installation)
        updated = result['Seed/System/installation.template.json']
        self.assertEqual(set(updated['extensions']), {'driver_hid_virtual'})
        self.assertEqual(updated['extensions']['driver_hid_virtual']['extensions'][0]['state']['state'], 'EXTENSION_NOT_INSTALLED')
        self.assertEqual(len(updated['files']), 1)
        self.assertEqual(len(result['Seed/System/current.template.json']['depots']), len(payload.DEPOTS))

    def test_template_requires_dependency_closure(self):
        depots = [{'name': n, 'localFolder': 'resource', 'dependsOn': ['missing']} for n in payload.DEPOTS]
        with self.assertRaises(ValueError):generic.templates({'depots': depots}, {})

    def test_allowlist_excludes_unknown_seed_content(self):
        for name in ('Seed/System/random-state.json', 'Seed/Local/settings.json',
                     'Seed/System/depots/999/g305/data.json', 'Backups/profile.txt'):
            self.assertFalse(payload.generic_path(name))
