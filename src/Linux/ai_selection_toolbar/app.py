from __future__ import annotations

import os
import shutil
import sys
from pathlib import Path

from PySide6.QtCore import QObject, QPoint, QTimer, QUrl, Signal
from PySide6.QtGui import QAction, QCursor, QDesktopServices, QIcon
from PySide6.QtWidgets import QApplication, QMenu, QMessageBox, QSystemTrayIcon

from .api import ChatWorker
from .history import HistoryStore
from .management import ManagementServer
from .selection import SelectionWatcher
from .settings import SettingsStore
from .ui import AnswerWindow, QuestionDialog, Toolbar


class ChangeBridge(QObject):
    changed = Signal()


class ApplicationController:
    def __init__(self, app: QApplication, open_settings: bool = False) -> None:
        self.app = app
        self.store = SettingsStore()
        self.history = HistoryStore()
        self.settings = self.store.load()
        self.anchor = QPoint(0, 0)
        self.selection_text = ""
        self.selection_app = ""
        self.selection_title = ""
        self.answer_text = ""
        self.worker: ChatWorker | None = None
        self.toolbar = Toolbar()
        self.answer = AnswerWindow()
        self.toolbar.rebuild(self.settings)
        self.answer.set_accent(self.settings.get("ToolbarAccentColor", "#4F46E5"))
        self.answer.set_background(self.settings.get("AnswerBackgroundColor", "#F8FAFC"))
        self.toolbar.action_requested.connect(self.run_action)
        self.toolbar.exclude_requested.connect(self.exclude_current)
        self.answer.stop_button.clicked.connect(self.cancel_request)
        self.bridge = ChangeBridge()
        self.bridge.changed.connect(self.reload_settings)
        self.management = ManagementServer(self.store, self.bridge.changed.emit, self.history)
        self.management.start()
        self.watcher = SelectionWatcher(self.settings.get("ExcludedApplications", []))
        self.watcher.selection_captured.connect(self.show_selection)
        self.watcher.selection_cleared.connect(self.clear_selection)
        self.watcher.failed.connect(self.show_error)
        self.tray = self._create_tray()
        self._apply_autostart()
        self.watcher.start()
        if open_settings or not self.settings.get("ApiProfiles"):
            QTimer.singleShot(100, self.open_management)

    def _create_tray(self) -> QSystemTrayIcon:
        if getattr(sys, "frozen", False):
            icon_path = Path(getattr(sys, "_MEIPASS")) / "AppIcon.svg"
        else:
            icon_path = Path(__file__).resolve().parents[2] / "Desktop" / "Assets" / "AppIcon.svg"
        tray = QSystemTrayIcon(QIcon(str(icon_path)))
        tray.setToolTip("AI 划词工具栏")
        menu = QMenu()
        settings_action = QAction("打开管理页", menu)
        settings_action.triggered.connect(self.open_management)
        quit_action = QAction("退出", menu)
        quit_action.triggered.connect(self.app.quit)
        menu.addAction(settings_action)
        menu.addSeparator()
        menu.addAction(quit_action)
        tray.setContextMenu(menu)
        tray.activated.connect(lambda reason: self.open_management()
                               if reason == QSystemTrayIcon.DoubleClick else None)
        tray.show()
        return tray

    def show_selection(self, text: str, application: str, title: str, x: int, y: int) -> None:
        if not self.settings.get("AutoShow", True):
            return
        self.selection_text = text
        self.selection_app = application
        self.selection_title = title
        # X11 returns physical root coordinates while Qt may expose scaled
        # logical coordinates on HiDPI/XWayland desktops. Use Qt's current
        # global cursor position for popup placement to keep both coordinate
        # systems consistent.
        self.anchor = QCursor.pos()
        if self.anchor.isNull():
            self.anchor = QPoint(x, y)
        if not self.answer.is_pinned:
            self.answer.hide()
        self.toolbar.show_below(self.anchor)

    def clear_selection(self, _x: int, _y: int) -> None:
        """Hide stale controls when the user starts another mouse interaction."""
        cursor = QCursor.pos()
        if self.toolbar.isVisible() and self.toolbar.geometry().contains(cursor):
            return
        self.selection_text = ""
        self.selection_app = ""
        self.selection_title = ""
        self.toolbar.hide()

    def run_action(self, action: str) -> None:
        if not self.selection_text:
            return
        question = ""
        if action == "ask":
            dialog = QuestionDialog()
            if not dialog.exec():
                return
            question = dialog.input.text().strip()
            if not question:
                return
        self.cancel_request()
        self.toolbar.hide()
        self.answer_text = ""
        self.answer.reset()
        self.answer.show_near(QPoint(self.anchor.x(), self.anchor.y() + self.toolbar.height() + 8))
        worker = ChatWorker(self.store, action, self.selection_text, question)
        self.worker = worker
        worker.chunk.connect(self.append_chunk)
        worker.completed.connect(lambda value: self.finish_action(action, question, value))
        worker.failed.connect(self.answer.set_error)
        worker.finished.connect(lambda: setattr(self, "worker", None) if self.worker is worker else None)
        worker.start()

    def append_chunk(self, chunk: str) -> None:
        self.answer_text += chunk
        self.answer.set_answer(self.answer_text)

    def finish_action(self, action: str, question: str, value: str) -> None:
        self.answer_text = value
        self.answer.set_answer(value, complete=True)
        self.history.add(self.selection_text, action, question, self.selection_app, self.selection_title, value)

    def cancel_request(self) -> None:
        if self.worker is not None:
            self.worker.cancel()
            self.worker.wait(1500)
            self.worker = None

    def exclude_current(self) -> None:
        if not self.selection_app:
            return
        settings = self.store.load()
        names = list(settings.get("ExcludedApplications") or [])
        if self.selection_app.casefold() not in {item.casefold() for item in names}:
            names.append(self.selection_app)
            settings["ExcludedApplications"] = names
            self.store.save(settings)
        self.reload_settings()
        self.toolbar.hide()

    def reload_settings(self) -> None:
        self.settings = self.store.load()
        self.toolbar.rebuild(self.settings)
        self.answer.set_accent(self.settings.get("ToolbarAccentColor", "#4F46E5"))
        self.answer.set_background(self.settings.get("AnswerBackgroundColor", "#F8FAFC"))
        self.watcher.update_excluded(self.settings.get("ExcludedApplications", []))
        self._apply_autostart()

    def open_management(self) -> None:
        QDesktopServices.openUrl(QUrl(self.management.url))

    def show_error(self, message: str) -> None:
        QMessageBox.warning(None, "AI 划词工具栏", message)

    def _apply_autostart(self) -> None:
        directory = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "autostart"
        path = directory / "ai-selection-toolbar.desktop"
        if not self.settings.get("StartOnLogin"):
            if path.exists():
                path.unlink()
            return
        directory.mkdir(parents=True, exist_ok=True)
        executable = shutil.which("ai-selection-toolbar") or sys.argv[0]
        path.write_text("[Desktop Entry]\nType=Application\nName=AI 划词助手\n" +
                        f"Exec={executable}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n", encoding="utf-8")

    def close(self) -> None:
        self.cancel_request()
        self.watcher.stop()
        self.management.stop()
        self.history.close()
        self.tray.hide()


def main() -> int:
    if not os.environ.get("DISPLAY") or os.environ.get("XDG_SESSION_TYPE", "").casefold() == "wayland":
        print("当前 Linux 版本需要 X11 会话。请在登录界面选择 Xorg，并安装 xclip。", file=sys.stderr)
        return 2
    app = QApplication(sys.argv)
    app.setApplicationName("AI 划词工具栏")
    app.setQuitOnLastWindowClosed(False)
    controller = ApplicationController(app, "--settings" in sys.argv)
    app.aboutToQuit.connect(controller.close)
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
