from __future__ import annotations

import html
import math
import sys
from pathlib import Path

from PySide6.QtCore import QPoint, QRect, QSize, Qt, QTimer, Signal
from PySide6.QtGui import QColor, QFont, QFontMetrics, QGuiApplication, QLinearGradient, QPainter, QPen, QPixmap, QTextOption
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
    last_answer_requested = Signal()
    exclude_requested = Signal()

    def __init__(self) -> None:
        super().__init__(None, Qt.Tool | Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint)
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.layout = QHBoxLayout(self)
        self.layout.setContentsMargins(8, 7, 8, 7)
        self.layout.setSpacing(4)
        self._accent = QColor("#4F46E5")
        self._background = QColor("#18202E")
        self._border = QColor("#0B1020")
        self._compact = False

    def rebuild(self, settings: dict) -> None:
        while self.layout.count():
            item = self.layout.takeAt(0)
            if item.widget():
                item.widget().deleteLater()
        actions = [("了解", "explain"), ("详细解释", "explain_detailed"),
                   ("翻译", "translate"), ("提问", "ask"), ("上次解答", "last_answer")]
        actions.extend((item.get("Name", "操作"), "custom:" + item.get("Id", ""))
                       for item in settings.get("CustomActions", []) if item.get("Id"))
        compact = settings.get("ToolbarStyle") == "compact"
        self._compact = compact
        self.layout.setContentsMargins(5 if compact else 8, 4 if compact else 7,
                                       5 if compact else 8, 4 if compact else 7)
        self.layout.setSpacing(0 if compact else 4)
        self._accent = _accent(settings.get("ToolbarAccentColor", "#4F46E5"), "#4F46E5")
        self._background = _accent(settings.get("ToolbarBackgroundColor", "#18202E"), "#18202E")
        self._border = _accent(settings.get("ToolbarBorderColor", "#0B1020"), "#0B1020")
        text_color = "#172033" if self._background.lightness() > 150 else "#F5F7FB"
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
            button.clicked.connect(lambda _checked=False, value=action: self.last_answer_requested.emit()
                                   if value == "last_answer" else self.action_requested.emit(value))
            button.setContentsMargins(0, 0, 0, 0)
            self.layout.addWidget(button)
        exclude = QPushButton("⊘")
        exclude.setProperty("compactDivider", compact and bool(actions))
        exclude.setToolTip("在此程序隐藏")
        exclude.clicked.connect(self.exclude_requested)
        self.layout.addWidget(exclude)
        padding = "7px 12px" if compact else "8px 12px"
        self.setStyleSheet(f"""
            QPushButton {{ color: {text_color}; background: transparent; border: 0; border-radius: {'0' if compact else '9px'};
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
        gradient.setColorAt(0, self._background)
        gradient.setColorAt(0.55, self._background.darker(105 if not self._compact else 100))
        gradient.setColorAt(1, self._background.lighter(115 if self._compact else 105))
        painter.setBrush(gradient)
        painter.setPen(QPen(self._border, 1))
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
    followup_requested = Signal(str)

    def __init__(self) -> None:
        super().__init__(None, Qt.Tool | Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint)
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self._accent = QColor("#0071E3")
        self._background = QColor("#F8FAFC")
        self._border = QColor("#0B1020")
        self._pending_markdown = ""
        self._pending_complete = False
        self._followup_allowed = False
        self._render_timer = QTimer(self)
        self._render_timer.setSingleShot(True)
        self._render_timer.timeout.connect(self._render_pending_answer)
        self._fitting = False
        self._pinned = False
        self._drag_offset = None
        self.setWindowTitle("AI 回答")
        self.setMinimumWidth(560)
        self.resize(700, 420)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(18, 16, 18, 16)
        self.heading = QLabel("正在生成回答…")
        self.heading.setObjectName("heading")
        self.heading.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        header = QHBoxLayout()
        header.setContentsMargins(0, 0, 0, 5)
        header.addWidget(self.heading)
        header.addStretch()
        self.close_button = QPushButton("×")
        self.close_button.setObjectName("closeButton")
        self.close_button.setToolTip("关闭回答")
        self.close_button.setAccessibleName("关闭回答")
        header.addWidget(self.close_button)
        self.pin_button = QPushButton("📌")
        self.pin_button.setObjectName("pinButton")
        self.pin_button.setCheckable(True)
        self.pin_button.setToolTip("固定回答窗口")
        self.pin_button.setAccessibleName("固定回答窗口")
        header.addWidget(self.pin_button)
        self.viewer = QTextBrowser()
        self.viewer.setOpenExternalLinks(True)
        self.viewer.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.viewer.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        text_option = QTextOption()
        text_option.setWrapMode(QTextOption.WrapMode.WrapAtWordBoundaryOrAnywhere)
        self.viewer.document().setDefaultTextOption(text_option)
        self.followup_input = QLineEdit()
        self.followup_input.setObjectName("followupInput")
        self.followup_input.setPlaceholderText("继续追问这份回答…")
        self.followup_button = QPushButton("追问")
        self.followup_button.setObjectName("followupButton")
        self.followup_button.setToolTip("基于当前回答继续提问")
        self.followup_row = QWidget()
        followup_layout = QHBoxLayout(self.followup_row)
        followup_layout.setContentsMargins(0, 8, 0, 0)
        followup_layout.setSpacing(8)
        followup_layout.addWidget(self.followup_input, 1)
        followup_layout.addWidget(self.followup_button)
        layout.addLayout(header)
        layout.addWidget(self.viewer, 1)
        layout.addWidget(self.followup_row)
        self.pin_button.toggled.connect(self.set_pinned)
        self.close_button.clicked.connect(self.close)
        self.followup_button.clicked.connect(self._submit_followup)
        self.followup_input.returnPressed.connect(self._submit_followup)
        self.followup_row.setVisible(False)
        self._apply_style()

    def set_accent(self, value: str) -> None:
        self._accent = _accent(value)
        self._apply_style()
        self.update()

    def set_background(self, value: str) -> None:
        self._background = _accent(value, "#F8FAFC")
        self._apply_style()
        self.update()

    def set_border(self, value: str) -> None:
        self._border = _accent(value, "#0B1020")
        self._apply_style()
        self.update()

    def _apply_style(self) -> None:
        accent = self._accent.name()
        self.setStyleSheet(f"""
            QLabel#heading {{ color: #172033; font-size: 18px; font-weight: 650; padding: 2px 2px 0; }}
            QPushButton#pinButton {{ min-width: 32px; max-width: 32px; min-height: 32px; max-height: 32px; padding: 0;
                                    border-radius: 16px; font-size: 16px; }}
            QPushButton#closeButton {{ min-width: 32px; max-width: 32px; min-height: 32px; max-height: 32px; padding: 0;
                                      border-radius: 16px; font-size: 24px; font-weight: 300; }}
            QTextBrowser {{ background: {self._background.name()}; color: #243149; border: 1px solid {self._border.name()};
                           border-radius: 16px; padding: 16px; font-size: 14px; selection-background-color: {_light(self._accent, 180)}; }}
            QLineEdit#followupInput {{ background: #ffffff; color: #243149; border: 1px solid {self._border.name()};
                                      border-radius: 999px; padding: 9px 15px; font-size: 14px; }}
            QPushButton#followupButton {{ padding: 9px 18px; }}
            QPushButton {{ background: #ffffff; color: #35445d; border: 1px solid {self._border.name()};
                          border-radius: 999px; padding: 9px 17px; }}
            QPushButton:hover {{ background: {_light(self._accent, 185)}; color: {accent}; }}
            QPushButton:pressed {{ background: {self._accent.name()}; color: white; }}
            QPushButton:checked {{ background: {self._accent.name()}; color: white; border-color: {self._accent.darker(110).name()}; }}
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
        painter.setPen(QPen(self._border, 1))
        painter.drawRoundedRect(rect, 22, 22)
        super().paintEvent(event)

    def reset(self) -> None:
        self._render_timer.stop()
        self._pending_markdown = ""
        self._pending_complete = False
        self.heading.setText("正在生成回答…")
        self.viewer.clear()
        self.followup_input.clear()
        self.followup_row.setVisible(False)

    def set_followup_allowed(self, allowed: bool) -> None:
        self._followup_allowed = allowed
        if not allowed:
            self.followup_row.setVisible(False)

    def set_answer(self, markdown: str, complete: bool = False) -> None:
        self._pending_markdown = markdown
        self._pending_complete = self._pending_complete or complete
        if complete:
            self._render_timer.stop()
            self._render_pending_answer()
        elif not self._render_timer.isActive():
            self._render_timer.start(50)

    def _render_pending_answer(self) -> None:
        complete = self._pending_complete
        self.viewer.setMarkdown(self._pending_markdown)
        self._fit_to_answer(resize_width=complete)
        if complete:
            self.heading.setText("回答完成")
            self._pending_complete = False
            self.followup_row.setVisible(self._followup_allowed)

    def _fit_to_answer(self, resize_width: bool = False) -> None:
        """Grow with streamed content up to the current screen's usable height."""
        if self._fitting:
            return
        self._fitting = True
        try:
            document = self.viewer.document()
            screen = QGuiApplication.screenAt(self.pos()) or QGuiApplication.primaryScreen()
            if resize_width:
                max_width = max(560, int(screen.availableGeometry().width() * 0.82))
                metrics = QFontMetrics(self.viewer.font())
                longest_line = max(
                    (metrics.horizontalAdvance(line) for line in self.viewer.toPlainText().splitlines()),
                    default=0,
                )
                target_width = max(560, min(max_width, longest_line + 88))
                if abs(target_width - self.width()) > 1:
                    self.resize(target_width, self.height())
            viewport_width = self.viewer.viewport().width()
            if viewport_width <= 0:
                viewport_width = self.width() - 54
            document.setDocumentMargin(0)
            document.setTextWidth(max(320, viewport_width - 12))
            document.adjustSize()
            content_height = math.ceil(document.size().height())
            max_height = max(360, int(screen.availableGeometry().height() * 0.70))
            # Keep short Markdown blocks, especially fenced code, inside the
            # viewport even though the scrollbar itself is intentionally hidden.
            target_height = max(240, min(max_height, content_height + 82))
            if abs(target_height - self.height()) > 1:
                self.resize(self.width(), target_height)
        finally:
            self._fitting = False

    def resizeEvent(self, event) -> None:  # noqa: N802
        super().resizeEvent(event)
        if self.viewer.toPlainText():
            self.viewer.document().setTextWidth(max(320, self.viewer.viewport().width() - 12))

    def set_error(self, message: str) -> None:
        self.followup_row.setVisible(False)
        self.heading.setText("请求失败")
        self.viewer.setHtml("<p style='color:#a33'>" + html.escape(message) + "</p>")

    def _submit_followup(self) -> None:
        question = self.followup_input.text().strip()
        if not question:
            return
        self.followup_input.clear()
        self.followup_requested.emit(question)

    def mousePressEvent(self, event) -> None:  # noqa: N802
        if event.button() == Qt.LeftButton:
            child = self.childAt(event.position().toPoint())
            if child not in {self.viewer, self.pin_button, self.close_button,
                             self.followup_input, self.followup_button}:
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
        self.setFixedWidth(460)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 14, 16, 12)
        layout.setSpacing(8)
        label = QLabel("输入你的问题")
        label.setObjectName("questionLabel")
        layout.addWidget(label)
        self.input = QLineEdit()
        self.input.setMinimumHeight(36)
        self.input.setPlaceholderText("例如：这段代码为什么这样写？")
        layout.addWidget(self.input)
        buttons = QDialogButtonBox(QDialogButtonBox.Cancel | QDialogButtonBox.Ok)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
