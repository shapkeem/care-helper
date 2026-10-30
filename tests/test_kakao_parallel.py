import os
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import kakao
import kakao_job


class FakeApp:
    load_jev_key = staticmethod(lambda: "")

    def in_new_tab(self, fn):
        return fn(object())


def test_parallel_by_worker(monkeypatch):
    lock = threading.Lock()
    running, peak, per_ic = {}, [0], {}  # id → 지원사 (Item 은 dataclass 라 set 에 못 넣음)

    def fake_run(page, dlg, it, ask, log, now=None):
        with lock:
            assert it.ic not in running.values(), "같은 지원사를 동시에 처리하면 안 돼요"
            running[id(it)] = it.ic
            peak[0] = max(peak[0], len(running))
        time.sleep(0.05)
        with lock:
            running.pop(id(it))
            per_ic.setdefault(it.ic, []).append(it.person)

    monkeypatch.setattr(kakao_job.runner, "run_item", fake_run)
    monkeypatch.setattr(kakao_job.g, "Dialogs", lambda tab: None)
    job = kakao_job.KakaoJob(FakeApp(), {"v": False}, lambda t: None, lambda r: None, lambda m: None,
                             lambda q, o: None)
    todo = [kakao.Item(ic=f"지원사{i % 4}", person=f"어르신{i}") for i in range(12)]
    for it in todo:
        job._set_row(it)
    job._run_parallel(todo, lambda: None)
    assert all(it.status == "완료" for it in todo)
    assert peak[0] == 3  # 지원사 4명 중 3명씩 동시에
    assert per_ic["지원사0"] == ["어르신0", "어르신4", "어르신8"]  # 같은 지원사는 받은 순서대로


def test_review_rows():
    from datetime import date
    today = date(2026, 10, 21)
    ics, names = {"이미화"}, {"장귀현", "형순덕"}
    row = {"id": 1, "ic": "이미화", "person": "장귀현", "date": "10/20", "time": "1430~1500", "kind": "방문",
           "action": "실적", "checked": True}
    assert kakao_job.row_problems(row, ics, names, today) == set()
    assert kakao_job.row_problems(dict(row, time=""), ics, names, today) == set()  # 실적은 시간 없어도 됨
    assert kakao_job.row_problems(dict(row, time="", action="일정등록"), ics, names, today) == {"time"}
    assert kakao_job.row_problems(dict(row, person="없는분", date="13/40", kind="??"), ics, names, today) ==         {"person", "date", "kind"}
    roster = [{"성명": "장귀현", "생활지원사": "이미화"}, {"성명": "형순덕", "생활지원사": "박미해"}]
    it = kakao_job.row_to_item(row, roster, today)
    assert (it.ic, it.person, it.date, it.frm, it.to, it.substitute) ==         ("이미화", "장귀현", date(2026, 10, 20), "1430", "1500", False)
    assert kakao_job.row_to_item(dict(row, person="형순덕"), roster, today).substitute is True


def test_substitute_falls_back_to_upload(monkeypatch):
    def fake_run(page, dlg, it, ask, log, now=None):
        raise kakao_job.g.WorkError(f"일정 창 대상자 목록에 '{it.person}' 어르신이 없어요.")
    monkeypatch.setattr(kakao_job.runner, "run_item", fake_run)
    monkeypatch.setattr(kakao_job.g, "Dialogs", lambda tab: None)
    job = kakao_job.KakaoJob(FakeApp(), {"v": False}, lambda t: None, lambda r: None, lambda m: None,
                             lambda q, o: None)
    sub = kakao.Item(ic="이미화", person="형순덕", frm="1000", to="1030", action="일정등록", substitute=True)
    own = kakao.Item(ic="이미화", person="장귀현", frm="1000", to="1030", action="일정등록")
    for it in (sub, own):
        job._set_row(it)
    job._run_parallel([sub, own], lambda: None)
    assert job._to_upload == [sub] and sub.status == "대기"  # 대직만 엑셀로 넘김
    assert own.status == "확인 필요"  # 자기 담당이면 그대로 오류로 알림


def test_review_rows_all_clients():
    from datetime import date
    today = date(2026, 10, 21)
    row = {"id": 1, "ic": "이미화", "person": kakao_job.kakao.ALL_CLIENTS, "date": "10/21", "time": "", "kind": "",
           "action": "실적", "checked": True}
    assert kakao_job.row_problems(row, {"이미화"}, {"장귀현"}, today) == set()
    assert kakao_job.row_problems(dict(row, person="장귀현"), {"이미화"}, {"장귀현"}, today) == set()  # 시간 없는 실적은 서비스 비워도 됨
    assert kakao_job.row_problems(dict(row, action="일정등록"), {"이미화"}, {"장귀현"}, today) >= {"person", "time"}
    assert kakao_job.row_problems(dict(row, time="1000~1100"), {"이미화"}, {"장귀현"}, today) == {"person", "kind"}
    it = kakao_job.row_to_item(row, [], today)
    assert (it.person, it.substitute) == (kakao_job.kakao.ALL_CLIENTS, False)


def test_run_all_clients(monkeypatch):
    from datetime import date, datetime
    import runner
    d = date(2026, 10, 21)
    plans = [dict(person="장귀현", date="20261021", **{"from": "0900", "to": "0930"}, code="A", result=False, seq=1),
             dict(person="김말순", date="20261021", **{"from": "1000", "to": "1030"}, code="B", result=True, seq=2),
             dict(person="김말순", date="20261021", **{"from": "1100", "to": "1101"}, code="B", result=False, seq=3),
             dict(person="장귀현", date="20261021", **{"from": "1500", "to": "1530"}, code="A", result=False, seq=4)]
    done = []
    monkeypatch.setattr(runner, "day_plans_all", lambda page, ic, dd: plans)
    monkeypatch.setattr(runner.g, "forget_screens", lambda page: None)
    monkeypatch.setattr(runner.g, "open_result", lambda page, person, ic, y, m: done.append(person))
    monkeypatch.setattr(runner.g, "register_result", lambda page, dlg, seq, dd: done.append(seq))
    monkeypatch.setattr(runner.g, "CODE_TO_KIND", {"A": "방문", "B": "전화"})
    it = kakao_job.kakao.Item(ic="이미화", person=kakao_job.kakao.ALL_CLIENTS, date=d, action="실적")
    runner.run_item(None, None, it, None, lambda m: None, now=datetime(2026, 10, 21, 12, 0))
    assert done == ["장귀현", 1, "김말순", 3]
    done.clear()
    it.kind = "방문"
    runner.run_item(None, None, it, None, lambda m: None, now=datetime(2026, 10, 21, 12, 0))
    assert done == ["장귀현", 1]
