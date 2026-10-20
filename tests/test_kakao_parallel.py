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
