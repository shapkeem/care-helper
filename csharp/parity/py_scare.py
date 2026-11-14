# -*- coding: utf-8 -*-
"""s-care 계산 부분(사이트 없이 도는 것)을 여러 입력으로 돌려 JSON 으로 남긴다 (C#판 --scare-pure 결과와 비교).
가짜 입력만 쓴다. python py_scare.py <출력.json>"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
import scare_counsel as sc  # noqa: E402
import scare_plan as sp  # noqa: E402

PEOPLE = ["홍길동", "홍길동 중점", "홍길동중점", "홍길동(중점)", "홍길동 (일반군)", "홍길동군", "홍길동 중점군",
          "홍길동\n김철수 일반\n\n이영희,박민수 중점", "홍길동,홍길동 중점", " 홍길동 \r\n 김철수 ", "박 민수", "홍길동!",
          "abc123", "홍길동 중", "", "  ,  ,", "홍길동(중점", "홍길동 중점)", "Kim 일반", "홍길동\t중점"]
HOURS = ["", "9", "09", "6", "5", "23", "24", "930", "9:30", "14시", "오후 2시", "0930", "١٤", "12345", "  ", "7.5"]
DATES = ["2026-11-13", "20261113", "2026.11.13", "", "2026-1-3", "2026년 11월 13일", "2026/11/13 10:00"]
PLACES = ["중점군", "일반군", "", "기타", "  중점 ", "일반 중점", None]


def run(fn, x):
    try:
        return ["ok", fn(x)]
    except Exception as e:  # noqa: BLE001
        return ["err", type(e).__name__ in ("ValueError", "CounselError") and str(e) or type(e).__name__]


out = {
    "plan_일반": sp.plan_data("일반"),
    "plan_중점": sp.plan_data("중점"),
    "people_일반": [run(lambda t: [list(x) for x in sp.parse_people(t, "일반")], t) for t in PEOPLE],
    "people_중점": [run(lambda t: [list(x) for x in sp.parse_people(t, "중점")], t) for t in PEOPLE],
    "hour": [run(sc.hour, t) for t in HOURS],
    "date": [run(sc.visit_date, t) for t in DATES],
    "place": [run(sc.place, t) for t in PLACES],
    "labels": sc.LABELS,
    "clean": [sc.clean(t) for t in ["", "  a  ", "'- 내용", "줄1\r\n줄2", "a\n\n\nb", " '- x\n'- y ", None, 3, 3.0, 0, 0.0, False]],
}
Path(sys.argv[1]).write_text(json.dumps(out, ensure_ascii=False, indent=0, sort_keys=True), encoding="utf-8")
print("ok")
