from __future__ import annotations

import tempfile
import unittest
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


if __name__ == "__main__":
    unittest.main()
