from __future__ import annotations

import unittest

from PySide6.QtCore import QPoint, QRect, QSize

from ai_selection_toolbar.ui import popup_position


class PopupPositionTests(unittest.TestCase):
    def test_prefers_below_selection_anchor(self) -> None:
        point = popup_position(QPoint(500, 300), QSize(300, 50), QRect(0, 0, 1000, 700))
        self.assertEqual(QPoint(350, 310), point)

    def test_flips_above_at_bottom_and_clamps_right_edge(self) -> None:
        point = popup_position(QPoint(990, 690), QSize(300, 60), QRect(0, 0, 1000, 700))
        self.assertEqual(QPoint(700, 620), point)

    def test_handles_monitor_with_negative_origin(self) -> None:
        point = popup_position(QPoint(-100, 200), QSize(240, 50), QRect(-1920, 0, 1920, 1080))
        self.assertEqual(QPoint(-240, 210), point)


if __name__ == "__main__":
    unittest.main()
