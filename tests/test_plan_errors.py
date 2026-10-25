import os
import sys
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import plan_converter as pc


def _job(rows, row_nums=None):
    job = pc.FileJob(Path("나종순 1026-1030제공계획서.xlsx"))
    job.grid = [list(r) for r in rows]
    job.row_nums = row_nums or list(range(2, 2 + len(rows)))
    return job


def _row(day, name="최종화", vendor="0002"):
    return [day, "1330", "1400", None, None, name, 19441217, "여", "나종순", 19700101, "여", "1A102", vendor]


def test_same_files_are_used_once(tmp_path):
    (tmp_path / "배종숙 제공계획서.xlsx").write_bytes(b"same")
    (tmp_path / "배종숙 제공계획서 (1).xlsx").write_bytes(b"same")
    (tmp_path / "김기주 제공계획서.xlsx").write_bytes(b"other")
    logs = []
    kept = pc._drop_same_files(sorted(tmp_path.iterdir()), logs.append)
    assert [f.name for f in kept] == ["김기주 제공계획서.xlsx", "배종숙 제공계획서 (1).xlsx"]
    assert len(logs) == 1 and "같은 파일" in logs[0]


def test_vendor_code_that_is_not_4_digits_becomes_default():
    assert pc.normalize_vendor_code("34202") == "0002"
    assert pc.normalize_vendor_code(34202) == "0002"
    assert pc.normalize_vendor_code(2) == "0002"
    assert pc.normalize_vendor_code("0105") == "0105"
    assert pc.normalize_vendor_code(None) == "0002"


def test_dates_outside_service_period_are_errors():
    job = _job([_row(20261026), _row(20261027)])
    entry = {"name": "최종화", "display_name": "최종화", "start": 20261012, "end": 20261026}
    job.row_entries = {0: entry, 1: entry}
    pc._check_service_period(job, {})
    assert len(job.errors) == 1
    assert job.errors[0].startswith("3행 최종화 2026-10-27") and "서비스 기간" in job.errors[0]


def test_service_period_uses_corrected_date():
    job = _job([_row(20251027)])  # 연도를 잘못 써서 보정 제안이 붙은 줄
    job.proposals.append(pc.FixProposal("날짜", "f", 2, "최종화", "20251027", "20261027", "연도",
                                        [(0, pc.COL_DATE)], pc._date_builder, auto=True))
    job.row_entries = {0: {"name": "최종화", "start": 20261012, "end": 20261026}}
    pc._check_service_period(job, {job.proposals[0].pid: "20261027"})
    assert len(job.errors) == 1


def test_error_files_merge_drops_only_error_rows():
    a = _job([_row(20261026), _row(20261027)])
    a.errors = ["3행 최종화 2026-10-27 일정이 서비스 기간(2026-10-12~2026-10-26) 밖이에요"]
    b = _job([_row(20261026, "정묘연")])
    b.errors = ["2행 대상자 '정묘연' 대상자리스트에 없음"]
    asked = []

    def ask(files):
        asked.extend(files)
        return {0: "merge", 1: "separate"}

    pc._ask_error_files([a, b], ask, lambda m: None)
    assert [f["can_merge"] for f in asked] == [True, True]
    assert a.errors == [] and list(a.dropped_rows) == [1]
    assert b.errors and not b.dropped_rows  # 따로 빼기: 그대로 오류 파일
    assert pc._output_base_name(b).startswith(pc.ERROR_FILE_PREFIX)


def test_error_without_row_number_cannot_merge():
    job = _job([_row(20261026)])
    job.errors = ["파일 전체 오류"]
    pc._ask_error_files([job], lambda files: {0: "merge"}, lambda m: None)
    assert job.errors == ["파일 전체 오류"]


def test_no_callback_keeps_old_behavior():
    job = _job([_row(20261026)])
    job.errors = ["2행 서비스코드 '13202' 코드표에 없음"]
    pc._ask_error_files([job], None, lambda m: None)
    assert job.errors and not job.dropped_rows
