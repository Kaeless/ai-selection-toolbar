from __future__ import annotations

import tempfile
import unittest
import sqlite3
from datetime import datetime, timezone
from pathlib import Path

from ai_selection_toolbar.history import HistoryStore


class HistoryStoreTests(unittest.TestCase):
    def test_search_delete_and_clear(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            store = HistoryStore(Path(directory) / "history.db")
            try:
                store.add("Linux selection", "explain", "", "Editor", "demo", "answer one")
                store.add("Other text", "ask", "why", "Browser", "page", "answer two")
                self.assertEqual(1, len(store.search("Linux")))
                item = store.search("why")[0]
                store.delete(item["Id"])
                self.assertEqual(1, len(store.search()))
                store.clear()
                self.assertEqual([], store.search())
            finally:
                store.close()

    def test_migrates_legacy_linux_history_schema(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "history.db"
            connection = sqlite3.connect(path)
            connection.execute(
                "CREATE TABLE history (id INTEGER PRIMARY KEY AUTOINCREMENT, created_utc_ticks INTEGER NOT NULL, "
                "selection_hash TEXT NOT NULL, selected_text TEXT NOT NULL, action TEXT, prompt TEXT, source TEXT, "
                "source_application TEXT, source_file TEXT, source_title TEXT, response TEXT)"
            )
            ticks = datetime(2026, 1, 2, tzinfo=timezone.utc).timestamp() * 10_000_000 + 621355968000000000
            connection.execute(
                "INSERT INTO history(created_utc_ticks, selection_hash, selected_text, action, prompt, source_application, source_title, response) "
                "VALUES(?,?,?,?,?,?,?,?)",
                (int(ticks), "hash", "legacy selection", "explain_detailed", "", "Editor", "Demo", "legacy answer"),
            )
            connection.commit(); connection.close()
            store = HistoryStore(path)
            try:
                item = store.search("legacy")[0]
                self.assertEqual("legacy answer", item["Result"])
                self.assertEqual("explain_detailed", item["Action"])
            finally:
                store.close()


if __name__ == "__main__":
    unittest.main()
