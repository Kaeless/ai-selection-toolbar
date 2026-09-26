from __future__ import annotations

import html
import math
from pathlib import Path

from PySide6.QtCore import QPoint, QRect, QSize, Qt, Signal
from PySide6.QtGui import QColor, QFont, QGuiApplication
from PySide6.QtWidgets import (
    QDialog,
    QDialogButtonBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QPushButton,
    QTextBrowser,
    QVBoxLayout,
    QWidget,
)


def popup_position(anchor: QPoint, size: QSize, available: QRect, gap: int = 10) -> QPoint:
    x = anchor.x() - size.width() // 2
    x = max(available.left(), min(x, available.right() - size.width() + 1))
    below = anchor.y() + gap
    if below + size.height() <= available.bottom() + 1:
        y = below
    else:
        y = anchor.y() - size.height() - gap
    y = max(available.top(), min(y, available.bottom() - size.height() + 1))
    return QPoint(x, y)


class Toolbar(QWidget):
    action_requested = Signal(str)
    exclude_requested = Signal()

    def __init__(self) -> None:
        super().__init__(None, Qt.Tool | Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint)
        self.setAttribute(Qt.WA_OpaquePaintEvent, True)
        self.layout = QHBoxLayout(self)
        self.layout.setContentsMargins(8, 7, 8, 7)
        self.layout.setSpacing(4)
        self._accent = "#4F46E5"

    def rebuild(self, settings: dict) -> None:
        while self.layout.count():
            item = self.layout.takeAt(0)
            if item.widget():
                item.widget().deleteLater()
        actions = [("了解", "explain"), ("详细解释", "explain_detailed"),
                   ("翻译", "translate"), ("提问", "ask")]
        actions.extend((item.get("Name", "操作"), "custom:" + item.get("Id", ""))
                       for item in settings.get("CustomActions", []) if item.get("Id"))
        compact = settings.get("ToolbarStyle") == "compact"
        self._accent = settings.get("ToolbarAccentColor", "#4F46E5")
        for title, action in actions:
            button = QPushButton(title)
            button.setProperty("action", True)
            button.setCursor(Qt.PointingHandCursor)
            button.clicked.connect(lambda _checked=False, value=action: self.action_requested.emit(value))
            button.setContentsMargins(0, 0, 0, 0)
            self.layout.addWidget(button)
        exclude = QPushButton("⊘")
        exclude.setToolTip("在此程序隐藏")
        exclude.clicked.connect(self.exclude_requested)
        self.layout.addWidget(exclude)
        padding = "5px 9px" if compact else "8px 12px"
        self.setStyleSheet(f"""
            Toolbar {{ background: #1b2433; border: 1px solid #46546b; border-radius: 14px; }}
            QPushButton {{ color: #dce4f2; background: transparent; border: 0; border-radius: 8px;
                          padding: {padding}; font-size: 13px; }}
            QPushButton:hover {{ background: #273146; color: white; }}
            QPushButton:pressed {{ background: {self._accent}; }}
        """)
        self.adjustSize()

    def show_below(self, anchor: QPoint) -> None:
        self.adjustSize()
        screen = QGuiApplication.screenAt(anchor) or QGuiApplication.primaryScreen()
        self.move(popup_position(anchor, self.sizeHint(), screen.availableGeometry()))
        self.show()
        self.raise_()


class AnswerWindow(QWidget):
    close_requested = Signal()

    def __init__(self) -> None:
        super().__init__(None, Qt.Tool | Qt.WindowStaysOnTopHint)
        self.setWindowTitle("AI 回答")
        self.resize(560, 420)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(18, 16, 18, 16)
        self.heading = QLabel("正在生成回答…")
        self.heading.setObjectName("heading")
        self.viewer = QTextBrowser()
        self.viewer.setOpenExternalLinks(True)
        self.stop_button = QPushButton("停止")
        self.close_button = QPushButton("关闭")
        footer = QHBoxLayout()
        footer.addStretch()
        footer.addWidget(self.stop_button)
        footer.addWidget(self.close_button)
        layout.addWidget(self.heading)
        layout.addWidget(self.viewer, 1)
        layout.addLayout(footer)
        self.close_button.clicked.connect(self.close)
        self.setStyleSheet("""
            AnswerWindow { background: #f8fafc; }
            QLabel#heading { color: #172033; font-size: 17px; font-weight: 650; padding: 3px 0 8px; }
            QTextBrowser { background: white; color: #243149; border: 1px solid #dfe6ef;
                           border-radius: 12px; padding: 14px; font-size: 14px; }
            QPushButton { background: white; color: #35445d; border: 1px solid #d9e1eb;
                          border-radius: 9px; padding: 8px 15px; }
            QPushButton:hover { background: #eef2f8; }
        """)

    def reset(self) -> None:
        self.heading.setText("正在生成回答…")
        self.viewer.clear()

    def set_answer(self, markdown: str, complete: bool = False) -> None:
        self.viewer.setMarkdown(markdown)
        self._fit_to_answer()
        if complete:
            self.heading.setText("回答完成")

    def _fit_to_answer(self) -> None:
        """Grow with streamed content, then let the viewer scroll long answers."""
        document = self.viewer.document()
        document.setTextWidth(self.viewer.viewport().width())
        document.adjustSize()
        content_height = math.ceil(document.size().height())
        target_height = max(220, min(720, content_height + 82))
        if target_height != self.height():
            self.resize(self.width(), target_height)

    def set_error(self, message: str) -> None:
        self.heading.setText("请求失败")
        self.viewer.setHtml("<p style='color:#a33'>" + html.escape(message) + "</p>")

    def show_near(self, anchor: QPoint) -> None:
        screen = QGuiApplication.screenAt(anchor) or QGuiApplication.primaryScreen()
        self.move(popup_position(anchor, self.size(), screen.availableGeometry(), 12))
        self.show()
        self.raise_()


class QuestionDialog(QDialog):
    def __init__(self) -> None:
        super().__init__()
        self.setWindowTitle("针对选中文字提问")
        self.setMinimumWidth(420)
        layout = QVBoxLayout(self)
        layout.addWidget(QLabel("输入你的问题"))
        self.input = QLineEdit()
        self.input.setPlaceholderText("例如：这段代码为什么这样写？")
        layout.addWidget(self.input)
        buttons = QDialogButtonBox(QDialogButtonBox.Cancel | QDialogButtonBox.Ok)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
