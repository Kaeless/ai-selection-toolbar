from __future__ import annotations

import unittest

from ai_selection_toolbar.api import build_prompt


class PromptTests(unittest.TestCase):
    def test_followup_prompt_contains_original_answer_and_question(self) -> None:
        instruction, text = build_prompt(
            "followup", "原始选区", "为什么？", {"TranslationTargetLanguage": "中文"}, "已有回答"
        )
        self.assertIn("已有回答", instruction)
        self.assertIn("原选中文字：\n原始选区", text)
        self.assertIn("已有回答：\n已有回答", text)
        self.assertIn("追问：\n为什么？", text)

    def test_followup_prompt_rejects_empty_question(self) -> None:
        with self.assertRaises(ValueError):
            build_prompt("followup", "原始选区", "", {}, "已有回答")


if __name__ == "__main__":
    unittest.main()
