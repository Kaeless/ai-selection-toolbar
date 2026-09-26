from __future__ import annotations

import sqlite3
import threading
from datetime import datetime, timezone
from pathlib import Path

from .settings import data_directory


class HistoryStore:
    def __init__(self, path: Path | None = None) -> None:
        target = path or data_directory() / "history.db"
        target.parent.mkdir(parents=True, exist_ok=True)
        self.connection = sqlite3.connect(target, check_same_thread=False)
        self.lock = threading.Lock()
        self.connection.execute(
            "CREATE TABLE IF NOT EXISTS history (id INTEGER PRIMARY KEY AUTOINCREMENT, "
            "created_utc TEXT NOT NULL, selected_text TEXT NOT NULL, action TEXT, prompt TEXT, "
            "source_application TEXT, source_title TEXT, response TEXT)"
        )
        self.connection.commit()

    def add(self, selection: str, action: str, prompt: str, application: str, title: str, response: str) -> None:
        with self.lock:
            self.connection.execute(
                "INSERT INTO history(created_utc, selected_text, action, prompt, source_application, source_title, response) "
                "VALUES(?,?,?,?,?,?,?)",
                (datetime.now(timezone.utc).isoformat(), selection, action, prompt, application, title, response),
            )
            self.connection.commit()

    def search(self, term: str = "", offset: int = 0) -> list[dict]:
        pattern = f"%{term}%"
        with self.lock:
            rows = self.connection.execute(
                "SELECT id, created_utc, selected_text, action, prompt, source_application, source_title, response "
                "FROM history WHERE ? = '' OR selected_text LIKE ? OR prompt LIKE ? OR response LIKE ? "
                "ORDER BY id DESC LIMIT 50 OFFSET ?",
                (term, pattern, pattern, pattern, max(0, offset)),
            ).fetchall()
        return [{
            "Id": row[0], "TimeUtc": row[1], "Selection": row[2], "Action": row[3],
            "Prompt": row[4], "Application": row[5], "SourceTitle": row[6], "Result": row[7],
        } for row in rows]

    def delete(self, entry_id: int) -> None:
        with self.lock:
            self.connection.execute("DELETE FROM history WHERE id = ?", (entry_id,))
            self.connection.commit()

    def clear(self) -> None:
        with self.lock:
            self.connection.execute("DELETE FROM history")
            self.connection.commit()

    def close(self) -> None:
        with self.lock:
            self.connection.close()
