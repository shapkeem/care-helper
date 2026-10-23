import os
import sys
from datetime import date

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import goodeos_work as g


def test_clear_whole_span_for_each_worker():
    rows = [{"ok": True, "date": "2026-11-03", "worker": "이미화"},
            {"ok": True, "date": "2026-11-05", "worker": "박미해"},
            {"ok": False, "date": "2026-11-07", "worker": "김말순"},  # 오류 줄: 기간에는 들어가지만 지우지는 않음
            {"ok": True, "date": "20261104", "worker": "이미화"}]
    days = g.upload_clear_days(rows)
    span = [date(2026, 11, d) for d in range(3, 8)]  # 11/3~11/7 (새 계획서에서 빠진 날도 포함)
    assert days == {"박미해": span, "이미화": span}
