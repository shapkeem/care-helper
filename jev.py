# -*- coding: utf-8 -*-
"""TypeSafe Jev (System One) 호출. 표준 라이브러리만 사용 (exe 용량을 늘리지 않으려고)."""
import json
import os
import time
import urllib.error
import urllib.request

API_URL = "https://api.typesafe.ai/v1/systemone"
MODEL = "jev-latest"


class JevError(Exception):
    pass


KEY_LOADER = None  # 프로그램이 넣어 줌 (암호화 저장된 키 읽기)


def api_key():
    """환경변수 TYPESAFE_API_KEY, 없으면 프로그램 설정(암호화 저장)에서."""
    key = os.environ.get("TYPESAFE_API_KEY", "").strip()
    if key:
        return key
    try:
        return (KEY_LOADER() or "").strip() if KEY_LOADER else ""
    except Exception:
        return ""


def ask(state, questions, retries=4):
    """questions: {id: {type, instructions, criteria}} → answers dict."""
    key = api_key()
    if not key:
        raise JevError("Jev API 키가 없어요. '로그인 정보'에서 Jev API 키를 넣어 주세요.")
    body = json.dumps({"state": state, "model": MODEL, "questions": questions}, ensure_ascii=False).encode("utf-8")
    delay = 1.0
    for attempt in range(retries + 1):
        req = urllib.request.Request(API_URL, data=body, method="POST", headers={
            "Authorization": f"Bearer {key}", "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.loads(r.read().decode("utf-8"))["answers"]
        except urllib.error.HTTPError as e:
            if e.code in (429, 529) and attempt < retries:
                time.sleep(delay)
                delay *= 2
                continue
            detail = e.read().decode("utf-8", "replace")[:300]
            if e.code == 401:
                raise JevError("Jev API 키가 맞지 않아요.")
            raise JevError(f"Jev 오류 {e.code}: {detail}")
        except (urllib.error.URLError, TimeoutError) as e:
            if attempt < retries:
                time.sleep(delay)
                delay *= 2
                continue
            raise JevError(f"Jev 에 연결하지 못했어요: {e}")


def choice(state, instructions, options, extra=None):
    """options: {key: 설명 또는 None}. → (선택 key, 확신도, 확률표)"""
    qs = {"q": {"type": "choice", "instructions": instructions, "criteria": options}}
    if extra:
        qs.update(extra)
    a = ask(state, qs)
    q = a["q"]
    return q["choice"], q["confidence"], q["probabilities"], a
