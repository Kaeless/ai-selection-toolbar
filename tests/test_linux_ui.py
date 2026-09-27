from __future__ import annotations

import unittest

from PySide6.QtWidgets import QApplication, QPushButton

from ai_selection_toolbar.ui import AnswerWindow, Toolbar


class UiSmokeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.app = QApplication.instance() or QApplication([])

    def test_short_markdown_fits_without_hidden_overflow(self) -> None:
        window = AnswerWindow()
        window.set_answer("# 标题\n\n```python\nprint('ok')\n```", complete=True)
        self.app.processEvents()

        self.assertGreaterEqual(window.height(), 240)
        self.assertLessEqual(window.viewer.document().size().height(), window.viewer.viewport().height())
        window.close()

    def test_long_answer_keeps_scrollable_viewport(self) -> None:
        window = AnswerWindow()
        window.set_answer("\n\n".join(["长回答内容"] * 200), complete=True)
        self.app.processEvents()

        self.assertGreater(window.viewer.verticalScrollBar().maximum(), 0)
        self.assertLessEqual(window.height(), self.app.primaryScreen().availableGeometry().height())
        window.close()

    def test_toolbar_rebuilds_actions(self) -> None:
        toolbar = Toolbar()
        toolbar.rebuild({"CustomActions": [{"Id": "custom", "Name": "自定义"}]})
        self.assertEqual(7, len(toolbar.findChildren(QPushButton)))
        toolbar.close()


if __name__ == "__main__":
    unittest.main()
