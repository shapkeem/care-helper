import os
import sys

import openpyxl

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import plan_converter as pc


def _book(path, headers):
    wb = openpyxl.Workbook()
    wb.active.append(headers)
    wb.save(path)


def test_finds_plan_by_headers_without_keyword(tmp_path):
    _book(tmp_path / "정미숙 1019.xlsx", pc.STANDARD_HEADERS)
    _book(tmp_path / "다른표.xlsx", ["이름", "전화번호"])
    _book(tmp_path / "김영희 제공계획서.xlsx", ["이름"])  # 이름에 '제공'이 있으면 지금처럼 찾는다
    names = sorted(f.name for f in pc.find_source_excels(tmp_path))
    assert names == ["김영희 제공계획서.xlsx", "정미숙 1019.xlsx"]
