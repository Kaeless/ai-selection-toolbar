from __future__ import annotations

import sqlite3
import threading
from datetime import datetime, timedelta, timezone
from pathlib import Path

from .settings import data_directory


class HistoryStore:
    def __init__(self, path: Path | None = None) -> None:
        target = path or data_directory() / "history.db"
        target.parent.mkdir(parents=True, exist_ok=True)
        self.connection = sqlite3.connect(target, check_same_thread=False)
        self.lock = threading.Lock()
        self._ensure_schema()
        self.connection.commit()

    def _ensure_schema(self) -> None:
        columns = {row[1] for row in self.connection.execute("PRAGMA table_info(history)")}
        if not columns:
            self._create_schema()
            return
        required = {"created_utc", "selected_text", "action", "prompt", "source_application", "source_title", "response"}
        if required.issubset(columns):
            return
        if {"created_utc_ticks", "selected_text", "action", "prompt", "source_application", "source_title", "response"}.issubset(columns):
            self._migrate_legacy_schema()
            return
        raise sqlite3.DatabaseError("无法识别历史记录数据库结构")

    def _create_schema(self) -> None:
        self.connection.execute(
            "CREATE TABLE IF NOT EXISTS history (id INTEGER PRIMARY KEY AUTOINCREMENT, "
            "created_utc TEXT NOT NULL, selected_text TEXT NOT NULL, action TEXT, prompt TEXT, "
            "source_application TEXT, source_title TEXT, response TEXT)"
        )

    def _migrate_legacy_schema(self) -> None:
        rows = self.connection.execute(
            "SELECT id, created_utc_ticks, selected_text, action, prompt, source_application, source_title, response "
            "FROM history ORDER BY id"
        ).fetchall()
        self.connection.execute("DROP TABLE IF EXISTS history_migrated")
        self._create_schema()
        self.connection.execute("ALTER TABLE history RENAME TO history_legacy")
        self._create_schema()
        for row in rows:
            ticks = max(0, int(row[1] or 0))
            created = datetime(1, 1, 1, tzinfo=timezone.utc) + timedelta(microseconds=ticks // 10)
            self.connection.execute(
                "INSERT INTO history(id, created_utc, selected_text, action, prompt, source_application, source_title, response) "
                "VALUES(?,?,?,?,?,?,?,?)",
                (row[0], created.isoformat(), row[2] or "", row[3], row[4], row[5], row[6], row[7]),
            )
        self.connection.execute("DROP TABLE history_legacy")

    def add(self, selection: str, action: str, prompt: str, application: str, title: str, response: str) -> None:
        with self.lock:
            self.connection.execute(
                "INSERT INTO history(created_utc, selected_text, action, prompt, source_application, source_title, response) "
                "VALUES(?,?,?,?,?,?,?)",
                (datetime.now(timezone.utc).isoformat(), selection, action, prompt, application, title, response),
            )
            self.connection.commit()

    def search(self, term: str = "", offset: int = 0, limit: int = 200) -> list[dict]:
        if limit < 1 or limit > 1000:
            raise ValueError("历史记录数量无效")
        pattern = f"%{term}%"
        with self.lock:
            rows = self.connection.execute(
                "SELECT id, created_utc, selected_text, action, prompt, source_application, source_title, response "
                "FROM history WHERE ? = '' OR selected_text LIKE ? OR prompt LIKE ? OR response LIKE ? "
                "ORDER BY id DESC LIMIT ? OFFSET ?",
                (term, pattern, pattern, pattern, limit, max(0, offset)),
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
