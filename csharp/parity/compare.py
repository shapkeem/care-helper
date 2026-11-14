# -*- coding: utf-8 -*-
"""local/py.json 과 local/cs.json 을 비교한다. 출력 엑셀은 둘 다 openpyxl 로 다시 읽어 비교한다."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from py_dump import sheet_values  # noqa: E402

local = Path(sys.argv[1])
py = json.loads((local / "py.json").read_text(encoding="utf-8"))
cs = json.loads((local / "cs.json").read_text(encoding="utf-8"))
bad = 0


def diff(name, a, b, limit=8):
    global bad
    if a == b:
        print(f"[같음] {name}")
        return
    bad += 1
    print(f"[다름] {name}")
    if isinstance(a, list) and isinstance(b, list):
        n = 0
        if len(a) != len(b):
            print(f"   길이 py={len(a)} cs={len(b)}")
        for i, (x, y) in enumerate(zip(a, b)):
            if x != y:
                print(f"   #{i}\n     py={json.dumps(x, ensure_ascii=False)[:600]}\n     cs={json.dumps(y, ensure_ascii=False)[:600]}")
                n += 1
                if n >= limit:
                    break
    elif isinstance(a, dict) and isinstance(b, dict):
        ka, kb = set(a), set(b)
        if ka != kb:
            print(f"   키 py만={sorted(ka - kb)[:10]} cs만={sorted(kb - ka)[:10]}")
        n = 0
        for k in sorted(ka & kb):
            if a[k] != b[k]:
                print(f"   [{k}]\n     py={json.dumps(a[k], ensure_ascii=False)[:600]}\n     cs={json.dumps(b[k], ensure_ascii=False)[:600]}")
                n += 1
                if n >= limit:
                    break
    else:
        print(f"   py={a!r:.300}\n   cs={b!r:.300}")


for key in ("read_list", "beneficiaries", "counsel", "kakao_msgs", "kakao_items", "kakao_rows", "conv_logs", "conv_result"):
    diff(key, py.get(key), cs.get(key))
pyf = {f.name: sheet_values(f) for f in sorted((local / "py_out").glob("*.xlsx"))}
csf = {f.name: sheet_values(f) for f in sorted((local / "cs_out").glob("*.xlsx"))}
diff("conv_files (openpyxl 로 다시 읽음)", pyf, csf)
print("다른 곳:", bad)
