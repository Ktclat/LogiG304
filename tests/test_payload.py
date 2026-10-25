import importlib.util
from pathlib import Path
import unittest
spec=importlib.util.spec_from_file_location('payload',Path(__file__).resolve().parents[1]/'scripts/validate_payload.py')
payload=importlib.util.module_from_spec(spec);spec.loader.exec_module(payload)

class PayloadTests(unittest.TestCase):
    def test_safe_paths(self):
        for name in ('App/lghub.exe','Seed/System/depots/device.json','backup/app.original.asar'):
            payload.validate_name(name)
    def test_reject_traversal(self):
        for name in ('../outside','/outside','C:/outside','a\\b','a/./b','a//b','a/CON.txt','a/trailing.'):
            with self.subTest(name=name),self.assertRaises(ValueError):payload.validate_name(name)
    def test_reject_personal_data(self):
        for name in ('Data/Profile/settings.db','Seed/Local/LGHUB/settings.db','Backups/profile','any/file.db-wal','any/file.sqlite','Seed/System/keys.json','Seed/System/groups.json','installation-journal.json'):
            with self.subTest(name=name),self.assertRaises(ValueError):payload.validate_name(name)
