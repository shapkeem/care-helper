namespace CareHelper.Kakao;

/// <summary>카톡 요청 하나 (kakao.Item).</summary>
public sealed class Item
{
    public string Ic = "";          // 지원사
    public string Person = "";      // 대상자
    public DateOnly? Date;
    public string Frm = "";         // HHMM
    public string To = "";
    public string Kind = "";        // 전화/방문/...
    public string Action = "";      // 실적/일정등록/수정/삭제/기타
    public string OldPerson = "";   // 이름을 바꾸는 수정일 때 원래 대상자
    public string Note = "";        // 사람 처리 사유
    public string Source = "";      // 원문
    public string Status = "대기";
    public HashSet<string> RequestedKinds = new(); // 같은 메시지에서 같은 어르신·같은 날 요청된 서비스들
    public bool Substitute;         // 대직: 보낸 지원사가 이 어르신 담당이 아님 → 일정실적업로드 엑셀로 올린다
    public int Pos;                 // 원문에서의 위치
    public int? Id;

    public string Label()
    {
        var t = "";
        if (Frm.Length > 0)
            t = $"{Frm[..2]}:{Frm[2..]}~" + (To.Length > 0 ? $"{To[..2]}:{To[2..]}" : "?");
        var who = OldPerson.Length > 0 ? $"{OldPerson}→{Person}" : Person;
        var d = Date is DateOnly dd ? $"{dd.Month:00}/{dd.Day:00}" : "날짜?";
        var sub = Substitute ? " (대직)" : "";
        return $"{Ic}{sub} · {(who.Length > 0 ? who : "?")} · {d} {(t.Length > 0 ? t : "시간없음")} {Kind} · {(Action.Length > 0 ? Action : "?")}";
    }
}
