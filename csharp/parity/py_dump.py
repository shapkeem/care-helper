# -*- coding: utf-8 -*-
"""파이썬판으로 같은 입력을 처리해 결과를 JSON 으로 남긴다 (C#판 결과와 비교용).
입력·결과는 parity/local/ 에만 둔다 (개인정보라 git 에 올리지 않는다).

python py_dump.py <local 폴더>
  local/plan_src/*.xls*    제공계획서 원본
  local/counsel/*.xlsx     상담지
  local/roster.xls         대상자리스트
  local/workers.json       생활지원사 명단
  local/kakao.txt          카톡 글
→ local/py.json
"""
import datetime
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
import openpyxl  # noqa: E402

import kakao  # noqa: E402
import plan_converter as pc  # noqa: E402
import scare_counsel as sc  # noqa: E402
import kakao_job  # noqa: E402
app = __import__("맞춤돌봄도우미")


def val(v):
    if v is None:
        return None
    if isinstance(v, bool):
        return f"b:{v}"
    if isinstance(v, int):
        return f"i:{v}"
    if isinstance(v, float):
        return f"i:{int(v)}" if v.is_integer() else f"f:{v!r}"
    if isinstance(v, datetime.datetime):
        return f"d:{v:%Y-%m-%dT%H:%M:%S}"
    if isinstance(v, datetime.time):
        return f"t:{v:%H:%M:%S}"
    return f"s:{v}"


def sheet_values(path):
    ws = openpyxl.load_workbook(path, data_only=True).active
    return [[val(c) for c in row] for row in ws.iter_rows(values_only=True)]


def main(local):
    local = Path(local)
    out = {}
    roster_path = local / "roster.xls"
    roster = app.read_list(str(roster_path))
    out["read_list"] = roster
    ben = pc.load_beneficiary_list(roster_path)
    out["beneficiaries"] = {k: [{**e, "start": e["start"], "end": e["end"]} for e in v] for k, v in ben.items()}

    counsel = {}
    for f in sorted((local / "counsel").glob("*.xlsx")):
        try:
            counsel[f.name] = sc.read_counsel(f)
        except sc.CounselError as e:
            counsel[f.name] = {"error": str(e)}
    out["counsel"] = counsel

    # 카톡: Jev 와 질문 없이 (Jev 가 필요하면 'JEV' 로 남김)
    def no_jev(*a, **k):
        raise RuntimeError("JEV")
    kakao.jev.ask = no_jev
    kakao.jev.choice = no_jev
    today = datetime.date(2026, 11, 13)
    ip = kakao.Interpreter(roster, lambda q, o: None, today)
    text = (local / "kakao.txt").read_text(encoding="utf-8")
    msgs = kakao.split_messages(text, today, ip.ics)
    out["kakao_msgs"] = [[s, d.isoformat(), b] for s, d, b in msgs]
    items = []
    for s, d, b in msgs:
        try:
            its = ip.interpret(s, d, b)
            items.append([{"ic": it.ic, "person": it.person, "date": it.date.isoformat() if it.date else None,
                           "frm": it.frm, "to": it.to, "kind": it.kind, "action": it.action, "old": it.old_person,
                           "note": it.note, "status": it.status, "sub": it.substitute,
                           "req": sorted(it.requested_kinds)} for it in its])
        except RuntimeError as e:
            items.append(str(e))
        except Exception as e:  # noqa: BLE001
            items.append("ERROR:" + type(e).__name__)
    out["kakao_items"] = items
    out["kakao_rows"] = []
    for its in items:
        if isinstance(its, list):
            for x in its:
                it = kakao.Item(ic=x["ic"], person=x["person"],
                                date=datetime.date.fromisoformat(x["date"]) if x["date"] else None,
                                frm=x["frm"], to=x["to"], kind=x["kind"], action=x["action"], note=x["note"],
                                source="", status=x["status"])
                it._id = 0
                out["kakao_rows"].append(kakao_job.item_to_row(it))

    # 제공계획서 변환: 임시 바탕화면에서
    with tempfile.TemporaryDirectory() as desk:
        pc.desktop_dir = lambda: Path(desk)
        src, dst = pc.work_folders()
        for f in sorted((local / "plan_src").iterdir()):
            shutil.copy2(f, src / f.name)
        workers = json.loads((local / "workers.json").read_text(encoding="utf-8"))
        logs = []
        res = pc.run_conversion(logs.append, str(roster_path), workers)
        out["conv_logs"] = logs
        out["conv_result"] = [res[1], res[2], [list(x) for x in res[3]],
                              sorted(Path(p).name for p in res[4]), sorted(Path(p).name for p in res[5])]
        out["conv_files"] = {f.name: sheet_values(f) for f in sorted(dst.glob("*.xlsx"))}
        keep = local / "py_out"
        shutil.rmtree(keep, ignore_errors=True)
        shutil.copytree(dst, keep)
    (local / "py.json").write_text(json.dumps(out, ensure_ascii=False, indent=0, default=str), encoding="utf-8")
    print("ok", len(out["kakao_msgs"]), "msgs", len(out["conv_files"]), "files")


if __name__ == "__main__":
    main(sys.argv[1])
