# tests/test_scare_counsel.py — 상담지 엑셀 읽기 (내용은 가짜)
import os
import sys

import openpyxl
import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import scare_counsel as sc

ROWS = [  # (A, B, C) — 보내 준 양식과 같은 배치
    ("사회영역(S)", "가족사항", "- 아들 1명\n'- 딸 1명"),
    (None, "사회참여", "- 경로당 다님"),
    (None, "경제상태", "- 기초연금"),
    (None, "주거환경", "- 자가 아파트"),
    ("신체영역(P)", "고려사항", "체크: 만성질환, 치아"),
    (None, "상담기록", "- 무릎 통증"),
    ("정신영역(M)", "고려사항", "체크: 인지기능저하·우울·불안"),
    (None, "상담기록", "- 특이사항 없음"),
    (None, "강점 및 자원", "- 이웃과 친함"),
    (None, "기타사항", "- 없음"),
    (None, "주요욕구", "- 안부 확인 필요"),
]


def make(path, name="홍길동", rows=ROWS, title=True, sheet=None):
    wb = openpyxl.Workbook()
    ws = wb.active
    ws.title = sheet or name
    if title:
        ws["A1"] = f"○ 상담지  (대상자: {name})"
    for i, (a, b, c) in enumerate(rows, start=2):
        ws.cell(i, 1, a)
        ws.cell(i, 2, b)
        ws.cell(i, 3, c)
    wb.save(path)
    return path


def test_read(tmp_path):
    d = sc.read_counsel(make(tmp_path / "홍길동_상담지.xlsx"))
    assert d["name"] == "홍길동"
    f = d["fields"]
    assert f["coFamMtrCn"] == "- 아들 1명\n- 딸 1명"  # 줄 앞 ' 는 뺀다
    assert f["coSociPtinCn"] == "- 경로당 다님"
    assert f["coEcoStaCn"] == "- 기초연금"
    assert f["coHouEnvCn"] == "- 자가 아파트"
    assert f["coBdyCnslRecCn"] == "- 무릎 통증"
    assert f["coMndCnslRecCn"] == "- 특이사항 없음"
    assert f["coStrgRscCn"] == "- 이웃과 친함"
    assert f["coEtcCn"] == "- 없음"
    assert f["coImpDsrCn"] == "- 안부 확인 필요"
    assert d["bdy"] == ["B1", "B3"]
    assert d["mnd"] == ["M1", "M3"]  # '우울·불안' = 우울.불안


def test_empty_check_and_name_fallback(tmp_path):
    rows = [r if r[2] != "체크: 만성질환, 치아" else (r[0], r[1], "체크: ") for r in ROWS]
    d = sc.read_counsel(make(tmp_path / "김철수_상담지.xlsx", name="김철수", rows=rows, title=False, sheet="Sheet"))
    assert d["bdy"] == [] and d["name"] == "김철수"  # A1 없으면 파일 이름에서


def test_errors(tmp_path):
    bad = [r if r[1] != "경제상태" else (r[0], "경제", r[2]) for r in ROWS]
    with pytest.raises(sc.CounselError, match="경제상태"):
        sc.read_counsel(make(tmp_path / "a.xlsx", rows=bad))
    bad = [r if r[2] != "체크: 만성질환, 치아" else (r[0], r[1], "체크: 허리") for r in ROWS]
    with pytest.raises(sc.CounselError, match="허리"):
        sc.read_counsel(make(tmp_path / "b.xlsx", rows=bad))


def test_collect(tmp_path):
    a = make(tmp_path / "가_상담지.xlsx")
    make(tmp_path / "나_상담지.xlsx")
    (tmp_path / "~$가_상담지.xlsx").write_bytes(b"")
    (tmp_path / "메모.txt").write_text("x")
    got = sc.collect([str(tmp_path), str(a)])
    assert [p.name for p in got] == ["가_상담지.xlsx", "나_상담지.xlsx"]


def test_choices():
    assert sc.place("일반") == "유선상담"
    assert sc.place("중점") == "대상자 가정"
    with pytest.raises(sc.CounselError):
        sc.place("퇴원")
    assert sc.hour("") == "09" and sc.hour("9") == "09" and sc.hour("14시") == "14"
    with pytest.raises(sc.CounselError):
        sc.hour("3")
    assert sc.visit_date("20261030") == sc.visit_date("2026-10-30") == sc.visit_date("2026.10.30") == "2026-10-30"
    with pytest.raises(sc.CounselError):
        sc.visit_date("")


def test_template(tmp_path):
    p = tmp_path / "양식.xlsx"
    sc.write_template(p)
    ws = openpyxl.load_workbook(p).active
    assert [ws.cell(r, 2).value for r in range(2, 13)] == [b for _, b, _ in ROWS]
    assert ws["C6"].value == "체크: " and ws["C8"].value == "체크: "
    with pytest.raises(sc.CounselError, match="이름"):  # 이름을 안 적은 양식
        sc.read_counsel(p)
