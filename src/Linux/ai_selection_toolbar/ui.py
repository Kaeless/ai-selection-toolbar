from __future__ import annotations

import html
import math
import sys
from pathlib import Path

from PySide6.QtCore import QPoint, QRect, QSize, Qt, QTimer, Signal
from PySide6.QtGui import QColor, QFont, QGuiApplication, QLinearGradient, QPainter, QPen, QPixmap, QTextOption
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


def _accent(value: str, fallback: str = "#0071E3") -> QColor:
    color = QColor(value)
    return color if color.isValid() else QColor(fallback)


def _light(color: QColor, factor: int = 180) -> str:
    return color.lighter(factor).name()


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
        self._accent = QColor("#4F46E5")
        self._compact = False

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
        self._compact = compact
        self.layout.setContentsMargins(5 if compact else 8, 4 if compact else 7,
                                       5 if compact else 8, 4 if compact else 7)
        self.layout.setSpacing(0 if compact else 4)
        self._accent = _accent(settings.get("ToolbarAccentColor", "#4F46E5"), "#4F46E5")
        icon_path = (Path(getattr(sys, "_MEIPASS")) / "AppIcon.svg"
                     if getattr(sys, "frozen", False)
                     else Path(__file__).resolve().parents[2] / "Desktop" / "Assets" / "AppIcon.svg")
        icon = QLabel()
        icon.setObjectName("toolbarIcon")
        icon.setFixedSize(24 if compact else 28, 24 if compact else 28)
        icon.setPixmap(QPixmap(str(icon_path)).scaled(icon.size(), Qt.KeepAspectRatio, Qt.SmoothTransformation))
        icon.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self.layout.addWidget(icon)
        for index, (title, action) in enumerate(actions):
            button = QPushButton(title)
            button.setProperty("action", True)
            button.setProperty("compactDivider", compact and index > 0)
            button.setCursor(Qt.PointingHandCursor)
            button.clicked.connect(lambda _checked=False, value=action: self.action_requested.emit(value))
            button.setContentsMargins(0, 0, 0, 0)
            self.layout.addWidget(button)
        exclude = QPushButton("⊘")
        exclude.setProperty("compactDivider", compact and bool(actions))
        exclude.setToolTip("在此程序隐藏")
        exclude.clicked.connect(self.exclude_requested)
        self.layout.addWidget(exclude)
        padding = "7px 12px" if compact else "8px 12px"
        self.setStyleSheet(f"""
            QPushButton {{ color: {'#1d1d1f' if compact else '#f5f7fb'}; background: transparent; border: 0; border-radius: {'0' if compact else '9px'};
                          padding: {padding}; font-size: 13px; }}
            QPushButton[compactDivider="true"] {{ border-left: 1px solid {'#d7dbe3' if compact else 'transparent'}; }}
            QPushButton:hover {{ background: {_light(self._accent, 185) if compact else '#2d374a'}; color: {'#172033' if compact else 'white'}; }}
            QPushButton:pressed {{ background: {self._accent.name()}; color: white; }}
            QLabel#toolbarIcon {{ margin-right: 3px; }}
        """)
        self.adjustSize()

    def paintEvent(self, event) -> None:  # noqa: N802
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        rect = self.rect().adjusted(1, 1, -1, -1)
        gradient = QLinearGradient(rect.topLeft(), rect.bottomRight())
        if self._compact:
            gradient.setColorAt(0, QColor("#ffffff"))
            gradient.setColorAt(0.55, QColor("#f7f8fb"))
            gradient.setColorAt(1, self._accent.lighter(185))
        else:
            gradient.setColorAt(0, QColor("#18202e"))
            gradient.setColorAt(0.55, QColor("#101622"))
            gradient.setColorAt(1, self._accent.darker(125))
        painter.setBrush(gradient)
        painter.setPen(QPen(self._accent.darker(110), 1))
        painter.drawRoundedRect(rect, 13 if self._compact else 15, 13 if self._compact else 15)
        super().paintEvent(event)

    def show_below(self, anchor: QPoint) -> None:
        self.adjustSize()
        screen = QGuiApplication.screenAt(anchor) or QGuiApplication.primaryScreen()
        self.move(popup_position(anchor, self.sizeHint(), screen.availableGeometry()))
        self.show()
        self.raise_()


class AnswerWindow(QWidget):
    close_requested = Signal()
    pinned_changed = Signal(bool)

    def __init__(self) -> None:
        super().__init__(None, Qt.Tool | Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint)
        self.setAttribute(Qt.WA_OpaquePaintEvent, True)
        self._accent = QColor("#0071E3")
        self._background = QColor("#F8FAFC")
        self._pinned = False
        self._drag_offset = None
        self.setWindowTitle("AI 回答")
        self.setMinimumWidth(700)
        self.resize(760, 420)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(18, 16, 18, 16)
        self.heading = QLabel("正在生成回答…")
        self.heading.setObjectName("heading")
        header = QHBoxLayout()
        header.setContentsMargins(0, 0, 0, 5)
        header.addWidget(self.heading)
        header.addStretch()
        self.pin_button = QPushButton("📌")
        self.pin_button.setObjectName("pinButton")
        self.pin_button.setCheckable(True)
        self.pin_button.setToolTip("固定回答窗口")
        self.pin_button.setAccessibleName("固定回答窗口")
        header.addWidget(self.pin_button)
        self.viewer = QTextBrowser()
        self.viewer.setOpenExternalLinks(True)
        text_option = QTextOption()
        text_option.setWrapMode(QTextOption.WrapMode.WrapAtWordBoundaryOrAnywhere)
        self.viewer.document().setDefaultTextOption(text_option)
        self.stop_button = QPushButton("停止")
        self.close_button = QPushButton("关闭")
        footer = QHBoxLayout()
        footer.addStretch()
        footer.addWidget(self.stop_button)
        footer.addWidget(self.close_button)
        layout.addLayout(header)
        layout.addWidget(self.viewer, 1)
        layout.addLayout(footer)
        self.pin_button.toggled.connect(self.set_pinned)
        self.close_button.clicked.connect(self.close)
        self._apply_style()

    def set_accent(self, value: str) -> None:
        self._accent = _accent(value)
        self._apply_style()
        self.update()

    def set_background(self, value: str) -> None:
        self._background = _accent(value, "#F8FAFC")
        self._apply_style()
        self.update()

    def _apply_style(self) -> None:
        accent = self._accent.name()
        self.setStyleSheet(f"""
            QLabel#heading {{ color: #172033; font-size: 18px; font-weight: 650; padding: 2px 2px 0; }}
            QPushButton#pinButton {{ min-width: 32px; max-width: 32px; min-height: 32px; max-height: 32px; padding: 0;
                                    border-radius: 16px; font-size: 16px; }}
            QTextBrowser {{ background: #ffffff; color: #243149; border: 1px solid #d6deea;
                           border-radius: 16px; padding: 16px; font-size: 14px; selection-background-color: {_light(self._accent, 180)}; }}
            QPushButton {{ background: #ffffff; color: #35445d; border: 1px solid #d6deea;
                          border-radius: 999px; padding: 9px 17px; }}
            QPushButton:hover {{ background: {_light(self._accent, 185)}; color: {accent}; }}
            QPushButton:pressed {{ background: {self._accent.name()}; color: white; }}
            QPushButton:checked {{ background: {self._accent.name()}; color: white; border-color: {self._accent.darker(110).name()}; }}
            QScrollBar:vertical {{ width: 11px; margin: 12px 5px 12px 0; background: #eef1f6; }}
            QScrollBar::handle:vertical {{ min-height: 34px; border-radius: 5px; background: {_accent(self._accent.name()).lighter(135).name()}; }}
            QScrollBar::handle:vertical:hover {{ background: {self._accent.name()}; }}
            QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {{ height: 0; }}
            QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {{ background: transparent; }}
        """)

    @property
    def is_pinned(self) -> bool:
        return self._pinned

    def set_pinned(self, pinned: bool) -> None:
        pinned = bool(pinned)
        if self._pinned == pinned and self.pin_button.isChecked() == pinned:
            return
        was_visible = self.isVisible()
        self._pinned = pinned
        if self.pin_button.isChecked() != pinned:
            self.pin_button.setChecked(pinned)
        self.pin_button.setText("📍" if pinned else "📌")
        self.pin_button.setToolTip("取消固定回答窗口" if pinned else "固定回答窗口")
        if was_visible:
            self.show()
            self.raise_()
        self.pinned_changed.emit(pinned)

    def paintEvent(self, event) -> None:  # noqa: N802
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        rect = self.rect().adjusted(1, 1, -1, -1)
        gradient = QLinearGradient(rect.topLeft(), rect.bottomRight())
        gradient.setColorAt(0, self._background)
        gradient.setColorAt(0.7, self._background.lighter(104))
        gradient.setColorAt(1, self._background.lighter(112))
        painter.setBrush(gradient)
        painter.setPen(QPen(self._accent.darker(110), 1))
        painter.drawRoundedRect(rect, 22, 22)
        super().paintEvent(event)

    def reset(self) -> None:
        self.heading.setText("正在生成回答…")
        self.viewer.clear()

    def set_answer(self, markdown: str, complete: bool = False) -> None:
        self.viewer.setMarkdown(markdown)
        self._fit_to_answer()
        QTimer.singleShot(0, self._fit_to_answer)
        if complete:
            self.heading.setText("回答完成")

    def _fit_to_answer(self) -> None:
        """Grow with streamed content, then let the viewer scroll long answers."""
        document = self.viewer.document()
        viewport_width = self.viewer.viewport().width()
        if viewport_width <= 0:
            viewport_width = self.width() - 54
        document.setDocumentMargin(0)
        document.setTextWidth(max(320, viewport_width - 12))
        document.adjustSize()
        content_height = math.ceil(document.size().height())
        target_height = max(220, min(720, content_height + 82))
        if target_height != self.height():
            self.resize(self.width(), target_height)

    def resizeEvent(self, event) -> None:  # noqa: N802
        super().resizeEvent(event)
        QTimer.singleShot(0, self._fit_to_answer)

    def set_error(self, message: str) -> None:
        self.heading.setText("请求失败")
        self.viewer.setHtml("<p style='color:#a33'>" + html.escape(message) + "</p>")

    def mousePressEvent(self, event) -> None:  # noqa: N802
        if event.button() == Qt.LeftButton:
            child = self.childAt(event.position().toPoint())
            if child not in {self.viewer, self.pin_button, self.stop_button, self.close_button}:
                self._drag_offset = event.globalPosition().toPoint() - self.frameGeometry().topLeft()
                event.accept()
                return
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event) -> None:  # noqa: N802
        if self._drag_offset is not None and event.buttons() & Qt.LeftButton:
            self.move(event.globalPosition().toPoint() - self._drag_offset)
            event.accept()
            return
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event) -> None:  # noqa: N802
        self._drag_offset = None
        super().mouseReleaseEvent(event)

    def show_near(self, anchor: QPoint) -> None:
        screen = QGuiApplication.screenAt(anchor) or QGuiApplication.primaryScreen()
        self.move(popup_position(anchor, self.size(), screen.availableGeometry(), 12))
        self.show()
        self.raise_()
        self.activateWindow()


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
