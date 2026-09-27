from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from ai_selection_toolbar.settings import SettingsError, SettingsStore, active_profile


class MemoryKeyring:
    def __init__(self) -> None:
        self.values: dict[tuple[str, str], str] = {}

    def get(self, service: str, username: str) -> str | None:
        return self.values.get((service, username))

    def set(self, service: str, username: str, value: str) -> None:
        self.values[(service, username)] = value

    def delete(self, service: str, username: str) -> None:
        self.values.pop((service, username), None)


class SettingsStoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name)
        self.keys = MemoryKeyring()
        self.patches = [
            patch("ai_selection_toolbar.settings.keyring.get_password", self.keys.get),
            patch("ai_selection_toolbar.settings.keyring.set_password", self.keys.set),
            patch("ai_selection_toolbar.settings.keyring.delete_password", self.keys.delete),
        ]
        for item in self.patches:
            item.start()
        self.store = SettingsStore(self.directory)

    def tearDown(self) -> None:
        for item in reversed(self.patches):
            item.stop()
        self.temporary.cleanup()

    def test_migrates_legacy_single_api(self) -> None:
        self.store.path.write_text(json.dumps({
            "BaseUrl": "https://api.example.com/v1", "Model": "model-a", "AutoShow": True,
        }), encoding="utf-8")
        settings = self.store.load()
        self.assertEqual("legacy", settings["ActiveApiId"])
        self.assertEqual("model-a", active_profile(settings)["Model"])

    def test_multiple_profiles_switch_and_do_not_serialize_secrets(self) -> None:
        first = self.store.upsert_profile({
            "Name": "云端", "ApiBaseUrl": "https://api.example.com/v1",
            "Model": "model-a", "ApiKey": "secret-a", "MakeActive": True,
        })
        second = self.store.upsert_profile({
            "Name": "本地", "ApiBaseUrl": "http://127.0.0.1:11434/v1",
            "Model": "model-b", "ApiKey": "", "MakeActive": False,
        })
        self.assertEqual(first, self.store.load()["ActiveApiId"])
        self.store.select_profile(second)
        self.assertEqual(second, self.store.load()["ActiveApiId"])
        self.assertNotIn("secret-a", self.store.path.read_text(encoding="utf-8"))
        self.assertTrue(self.store.public_settings()["ApiProfiles"][0]["HasApiKey"])

    def test_cannot_delete_only_profile(self) -> None:
        profile_id = self.store.upsert_profile({
            "Name": "唯一", "ApiBaseUrl": "https://api.example.com/v1",
            "Model": "model-a", "ApiKey": "", "MakeActive": True,
        })
        with self.assertRaises(SettingsError):
            self.store.delete_profile(profile_id)

    def test_public_settings_contains_app_metadata(self) -> None:
        public = self.store.public_settings()
        self.assertEqual("0.5.2", public["Version"])
        self.assertEqual("Kaeless", public["Author"])


if __name__ == "__main__":
    unittest.main()
