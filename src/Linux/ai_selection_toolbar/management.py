from __future__ import annotations

import json
import re
import secrets
import sys
import threading
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable
from urllib.parse import parse_qs, urlparse

from .settings import SettingsStore
from .history import HistoryStore


def web_root() -> Path:
    if getattr(sys, "frozen", False):
        return Path(getattr(sys, "_MEIPASS")) / "web"
    return Path(__file__).resolve().parents[2] / "Desktop"


class ManagementServer:
    def __init__(self, store: SettingsStore, on_change: Callable[[], None], history: HistoryStore | None = None) -> None:
        self.store = store
        self.on_change = on_change
        self.history = history
        self.token = secrets.token_urlsafe(32)
        self.httpd: ThreadingHTTPServer | None = None
        self.thread: threading.Thread | None = None

    @property
    def url(self) -> str:
        if self.httpd is None:
            raise RuntimeError("管理服务尚未启动")
        return f"http://127.0.0.1:{self.httpd.server_port}/#{self.token}"

    def start(self) -> None:
        owner = self

        class Handler(BaseHTTPRequestHandler):
            server_version = "AiSelectionToolbar/0.5"

            def log_message(self, _format: str, *_args: object) -> None:
                return

            def do_GET(self) -> None:  # noqa: N802
                path = urlparse(self.path)
                if path.path in {"/", "/app.js", "/style.css"}:
                    files = {"/": ("ManagementPage.html", "text/html; charset=utf-8"),
                             "/app.js": ("ManagementPage.js", "application/javascript; charset=utf-8"),
                             "/style.css": ("ManagementPage.css", "text/css; charset=utf-8")}
                    filename, mime = files[path.path]
                    self._send(HTTPStatus.OK, (web_root() / filename).read_bytes(), mime)
                    return
                if not self._authorized():
                    return
                if path.path == "/api/settings":
                    self._json(owner.store.public_settings())
                elif path.path == "/api/history":
                    query = parse_qs(path.query)
                    term = (query.get("search") or [""])[0][:200]
                    try:
                        offset = max(0, int((query.get("offset") or ["0"])[0]))
                        limit = min(500, max(1, int((query.get("limit") or ["200"])[0])))
                    except ValueError:
                        self._error(HTTPStatus.BAD_REQUEST, "历史分页参数无效")
                        return
                    self._json(owner.history.search(term, offset, limit) if owner.history else [])
                else:
                    self._error(HTTPStatus.NOT_FOUND, "Not found")

            def do_POST(self) -> None:  # noqa: N802
                if not self._authorized(require_origin=True):
                    return
                try:
                    length = int(self.headers.get("Content-Length", "0"))
                    if length < 0 or length > 65536:
                        raise ValueError("请求体过大")
                    payload = json.loads(self.rfile.read(length) or b"{}")
                    path = urlparse(self.path).path
                    if path == "/api/connection":
                        owner.store.upsert_profile(payload)
                    elif path == "/api/connection/select":
                        owner.store.select_profile(str(payload.get("id") or ""))
                    elif path == "/api/connection/delete":
                        owner.store.delete_profile(str(payload.get("id") or ""))
                    elif path == "/api/settings":
                        self._save_general(payload)
                    elif path in {"/api/exclusions/add", "/api/exclusions/remove"}:
                        self._change_exclusion(payload, path.endswith("add"))
                    elif path == "/api/history/delete":
                        if owner.history:
                            owner.history.delete(int(payload.get("id") or 0))
                    elif path == "/api/history/clear":
                        if owner.history:
                            owner.history.clear()
                    else:
                        self._error(HTTPStatus.NOT_FOUND, "Not found")
                        return
                    owner.on_change()
                    self._json({"ok": True})
                except (ValueError, OSError, KeyError) as error:
                    self._error(HTTPStatus.BAD_REQUEST, str(error) or "输入值无效")
                except Exception as error:
                    self._error(HTTPStatus.INTERNAL_SERVER_ERROR, f"保存失败：{error}")

            def _save_general(self, payload: dict[str, Any]) -> None:
                settings = owner.store.load()
                colors = {
                    key: str(payload.get(key, fallback)).upper()
                    for key, fallback in {
                        "ToolbarAccentColor": "#4F46E5",
                        "ToolbarBackgroundColor": "#18202E",
                        "ToolbarBorderColor": "#0B1020",
                        "AnswerBackgroundColor": "#F8FAFC",
                        "AnswerBorderColor": "#0B1020",
                    }.items()
                }
                for key, color in colors.items():
                    if not re.fullmatch(r"#[0-9A-F]{6}", color):
                        raise ValueError(f"{key} 应为 #RRGGBB")
                language = str(payload.get("TargetLanguage") or "").strip()
                if not language or len(language) > 50:
                    raise ValueError("翻译目标语言无效")
                notes = str(payload.get("NotesDirectory") or "").strip()
                if len(notes) > 2048:
                    raise ValueError("笔记目录过长")
                settings.update({
                    "AutoShow": bool(payload.get("AutoShow")),
                    "StartOnLogin": bool(payload.get("StartOnLogin")),
                    "TranslationTargetLanguage": language,
                    "NotesDirectory": notes,
                    "ToolbarStyle": payload.get("ToolbarStyle", "standard"),
                    **colors,
                    "CustomActions": payload.get("CustomActions") or [],
                })
                owner.store.save(settings)

            def _change_exclusion(self, payload: dict[str, Any], add: bool) -> None:
                name = str(payload.get("application") or "").strip()
                if not name or len(name) > 200 or any(char in name for char in "\r\n\0"):
                    raise ValueError("程序名称无效")
                settings = owner.store.load()
                names = list(settings.get("ExcludedApplications") or [])
                if add and name.casefold() not in {item.casefold() for item in names}:
                    names.append(name)
                if not add:
                    names = [item for item in names if item.casefold() != name.casefold()]
                settings["ExcludedApplications"] = names
                owner.store.save(settings)

            def _authorized(self, require_origin: bool = False) -> bool:
                expected = f"Bearer {owner.token}"
                if not secrets.compare_digest(self.headers.get("Authorization", ""), expected):
                    self._error(HTTPStatus.UNAUTHORIZED, "Unauthorized")
                    return False
                expected_origin = f"http://127.0.0.1:{self.server.server_port}"
                origin = self.headers.get("Origin")
                if (require_origin and origin != expected_origin) or (origin and origin != expected_origin):
                    self._error(HTTPStatus.FORBIDDEN, "Forbidden")
                    return False
                return True

            def _json(self, value: Any) -> None:
                self._send(HTTPStatus.OK, json.dumps(value, ensure_ascii=False).encode(), "application/json; charset=utf-8")

            def _error(self, status: HTTPStatus, message: str) -> None:
                self._send(status, message.encode(), "text/plain; charset=utf-8")

            def _send(self, status: HTTPStatus, body: bytes, content_type: str) -> None:
                self.send_response(status)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(body)))
                self.send_header("Cache-Control", "no-store")
                self.send_header("X-Content-Type-Options", "nosniff")
                self.send_header("X-Frame-Options", "DENY")
                self.end_headers()
                self.wfile.write(body)

        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True, name="management-server")
        self.thread.start()

    def stop(self) -> None:
        if self.httpd is not None:
            self.httpd.shutdown()
            self.httpd.server_close()
        if self.thread is not None:
            self.thread.join(timeout=2)
        self.httpd = None
        self.thread = None
