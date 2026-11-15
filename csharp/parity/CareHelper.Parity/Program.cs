// C#판으로 py_dump.py 와 같은 입력을 처리해 같은 모양의 JSON 을 남긴다 (local/cs.json).
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CareHelper;
using CareHelper.Browser;
using CareHelper.Excel;
using CareHelper.Goodeos;
using CareHelper.Kakao;
using CareHelper.Plan;
using CareHelper.Scare;

if (args[0] == "--live-daily") // 실제 goodeos 조회만 (숫자만 출력)
{
    var t0 = DateTime.Now;
    var r = await DailyCheck.RunAsync(m => Console.WriteLine("log: " + System.Text.RegularExpressions.Regex.Replace(m, "[가-힣]{2,4}(?=:)", "*")),
                                      (p, m) => Console.WriteLine($"{p}% {m}"));
    Console.WriteLine($"users={r.NUsers} absent={r.NAbsent} stats={r.NStats} missing={r.Missing.Count} absentReg={r.AbsentReg.Count} day={r.Day} sec={(DateTime.Now - t0).TotalSeconds:0}");
    Console.WriteLine("status=" + await GoodeosChrome.StatusAsync() + " scare=" + (await ScarePlan.StatusAsync() ?? "off"));
    await GoodeosChrome.CloseAsync();
    return;
}
if (args[0] == "--fetch-lists") // '명단 새로 받기'와 같은 동작 (오늘 명단이 있어도 다시 받기). 개인정보는 출력하지 않는다
{
    var (r0, _) = Lists.LatestFile("대상자리스트", ".xls");
    var before = r0 != null ? File.GetLastWriteTime(r0) : (DateTime?)null;
    var day = await Lists.FetchDailyListsAsync(m => Console.WriteLine("log: " + System.Text.RegularExpressions.Regex.Replace(m, "[가-힣]{2,4}(?=:)", "*")), force: true);
    var (r1, d1) = Lists.LatestFile("대상자리스트", ".xls");
    var (w1, wd1) = Lists.LatestFile("생활지원사명단", ".json");
    Console.WriteLine($"day={day} roster={Path.GetFileName(r1)} rosterDay={d1} before={before:HH:mm:ss} after={File.GetLastWriteTime(r1!):HH:mm:ss} rows={DailyCheck.ReadList(r1!).Count}");
    Console.WriteLine($"workers={Path.GetFileName(w1)} day={wd1} n={Lists.ParseWorkers(File.ReadAllText(w1!)).Count}");
    await GoodeosChrome.CloseAsync();
    return;
}
if (args[0] == "--mock-scare") // 가짜 제공계획 화면(mock.html)에 C#판 FillJs 를 돌려 결과를 남긴다: <폴더> <크롬 경로>
{
    var fillJs = (string)typeof(ScarePlan).GetField("FillJs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
    const string dump = @"() => ({rows: [...document.querySelectorAll('tr')].map(tr => [...tr.querySelectorAll('input,textarea')].map(x => x.type === 'checkbox' ? String(x.checked) : x.value)),
  opinion: document.querySelector('[name=spOvlOpiCn]').value,
  radio: [...document.querySelectorAll('[name=spDfrnSvcNeedYn]')].map(x => x.checked), ev: (window.__ev || []).length})";
    using var pw = await Microsoft.Playwright.Playwright.CreateAsync();
    var b = await pw.Chromium.LaunchAsync(new() { ExecutablePath = args[2], Headless = true });
    var res = new JsonObject();
    foreach (var g in new[] { "일반", "중점" })
        foreach (var other in new[] { true, false })
        {
            var p = await b.NewPageAsync();
            await p.GotoAsync(new Uri(Path.Combine(args[1], "mock.html")).AbsoluteUri);
            var data = ScarePlan.PlanData(g);
            data["other"] = other ? "Y" : "N";
            var r = await p.EvaluateAsync<JsonElement>(fillJs, data);
            var dom = await p.EvaluateAsync<JsonElement>(dump);
            res[$"{g}_{(other ? "True" : "False")}"] = new JsonObject { ["ret"] = JsonNode.Parse(r.GetRawText()), ["dom"] = JsonNode.Parse(dom.GetRawText()) };
            await p.CloseAsync();
        }
    await b.CloseAsync();
    File.WriteAllText(Path.Combine(args[1], "cs_mock.json"), res.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine("ok");
    return;
}
if (args[0] == "--mock-counsel") // 가짜 상담지 화면(counsel.html)에 C#판 FillJs 와 같은 비교를 돌린다: <폴더> <크롬 경로>
{
    var fillJs = (string)typeof(ScareCounsel).GetField("FillJs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
    var w = JsonNode.Parse(File.ReadAllText(Path.Combine(args[1], "want.json")))!;
    var data = new CounselData
    {
        Name = "x", Fields = w["fields"]!.AsObject().ToDictionary(kv => kv.Key, kv => (string)kv.Value!),
        Bdy = w["bdy"]!.AsArray().Select(x => (string)x!).ToList(), Mnd = w["mnd"]!.AsArray().Select(x => (string)x!).ToList(),
    };
    var user = new ScareUser((string)w["user"]!["usrNm"]!, (string)w["user"]!["usrId"]!);
    string day = (string)w["date"]!, hh = (string)w["hour"]!, where = (string)w["place"]!;
    using var pw = await Microsoft.Playwright.Playwright.CreateAsync();
    var b = await pw.Chromium.LaunchAsync(new() { ExecutablePath = args[2], Headless = true });
    var f = await b.NewPageAsync();
    await f.GotoAsync(new Uri(Path.Combine(args[1], "counsel.html")).AbsoluteUri);
    await f.AddScriptTagAsync(new() { Url = "https://cdnjs.cloudflare.com/ajax/libs/jquery/3.7.1/jquery.min.js" });
    // ↓ ScareCounsel.FillOneAsync 와 같은 줄
    var want = new
    {
        date = day, hour = hh, place = where, fields = data.Fields, bdy = data.Bdy, mnd = data.Mnd,
        user = new { usrNm = user.UsrNm, usrId = user.UsrId },
    };
    var got = await f.EvaluateAsync<JsonElement>(fillJs, want);
    var bad = new List<string>();
    if (got.Str("date") != day) bad.Add("date");
    if (got.Str("hour") != hh) bad.Add("hour");
    if (got.Str("place") != where) bad.Add("place");
    if (!got.GetProperty("bdy").Strings().SequenceEqual(data.Bdy)) bad.Add("bdy");
    if (!got.GetProperty("mnd").Strings().SequenceEqual(data.Mnd)) bad.Add("mnd");
    var gf = got.GetProperty("fields");
    foreach (var (k, v) in data.Fields)
        if (gf.Str(k) != v) bad.Add(k);
    if (got.Str("user") != user.UsrId) bad.Add("상담자");
    await b.CloseAsync();
    var g = JsonNode.Parse(got.GetRawText())!.AsObject();
    g.Remove("$id");
    g["fields"]!.AsObject().Remove("$id");
    File.WriteAllText(Path.Combine(args[1], "cs_counsel.json"), g.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine("cs ok, bad = [" + string.Join(", ", bad) + "]");
    return;
}
if (args[0] == "--mock-obj") // 페이지가 돌려준 객체를 JsonElement 로 받을 때 모양 확인: <크롬 경로>
{
    using var pw = await Microsoft.Playwright.Playwright.CreateAsync();
    var b = await pw.Chromium.LaunchAsync(new() { ExecutablePath = args[1], Headless = true });
    var p = await b.NewPageAsync();
    var r = await p.EvaluateAsync<JsonElement>("async (ym) => { const out = {}; out['홍길동'] = {birthday: '65.05.06', dong: '1동', gender: '여'}; out['김철수'] = {birthday: '', dong: '', gender: ''}; return out; }", "202611");
    Console.WriteLine(r.GetRawText());
    Console.WriteLine(string.Join(",", r.EnumerateObject().Select(x => x.Name + ":" + x.Value.ValueKind)));
    await b.CloseAsync();
    return;
}
if (args[0] == "--scare-pure") // py_scare.py 와 같은 가짜 입력으로 s-care 계산 부분만
{
    string[] people = { "홍길동", "홍길동 중점", "홍길동중점", "홍길동(중점)", "홍길동 (일반군)", "홍길동군", "홍길동 중점군",
        "홍길동\n김철수 일반\n\n이영희,박민수 중점", "홍길동,홍길동 중점", " 홍길동 \r\n 김철수 ", "박 민수", "홍길동!",
        "abc123", "홍길동 중", "", "  ,  ,", "홍길동(중점", "홍길동 중점)", "Kim 일반", "홍길동\t중점" };
    string[] hours = { "", "9", "09", "6", "5", "23", "24", "930", "9:30", "14시", "오후 2시", "0930", "١٤", "12345", "  ", "7.5" };
    string[] dates = { "2026-11-13", "20261113", "2026.11.13", "", "2026-1-3", "2026년 11월 13일", "2026/11/13 10:00" };
    string?[] places = { "중점군", "일반군", "", "기타", "  중점 ", "일반 중점", null };
    object?[] cleans = { "", "  a  ", "'- 내용", "줄1\r\n줄2", "a\n\n\nb", " '- x\n'- y ", null, 3L, 3.0, 0L, 0.0, false };
    static JsonArray Run(Func<JsonNode?> fn)
    {
        try { return new JsonArray("ok", fn()); }
        catch (Exception e) when (e is ArgumentException or CounselError) { return new JsonArray("err", e.Message); }
        catch (Exception e) { return new JsonArray("err", e.GetType().Name); }
    }
    static JsonNode PeopleNode(List<(string Name, string Group)> l) => new JsonArray(l.Select(x => (JsonNode)new JsonArray(x.Name, x.Group)).ToArray());
    var o = new JsonObject
    {
        ["plan_일반"] = JsonSerializer.SerializeToNode(ScarePlan.PlanData("일반")),
        ["plan_중점"] = JsonSerializer.SerializeToNode(ScarePlan.PlanData("중점")),
        ["people_일반"] = new JsonArray(people.Select(t => (JsonNode)Run(() => PeopleNode(ScarePlan.ParsePeople(t, "일반")))).ToArray()),
        ["people_중점"] = new JsonArray(people.Select(t => (JsonNode)Run(() => PeopleNode(ScarePlan.ParsePeople(t, "중점")))).ToArray()),
        ["hour"] = new JsonArray(hours.Select(t => (JsonNode)Run(() => ScareCounsel.Hour(t))).ToArray()),
        ["date"] = new JsonArray(dates.Select(t => (JsonNode)Run(() => ScareCounsel.VisitDate(t))).ToArray()),
        ["place"] = new JsonArray(places.Select(t => (JsonNode)Run(() => ScareCounsel.Place(t))).ToArray()),
        ["labels"] = JsonSerializer.SerializeToNode(ScareCounsel.Labels),
        ["clean"] = new JsonArray(cleans.Select(t => (JsonNode)ScareCounsel.Clean(t)).ToArray()),
    };
    File.WriteAllText(args[1], o.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine("ok");
    return;
}
var local = args[0];
var outp = new JsonObject();

static string? Val(object? v) => v switch
{
    null => null,
    bool b => $"b:{(b ? "True" : "False")}",
    long l => $"i:{l}",
    double d when Math.Floor(d) == d && Math.Abs(d) < 9e18 => $"i:{(long)d}",
    double d => $"f:{Py.FloatRepr(d)}",
    DateTime dt => $"d:{dt:yyyy-MM-ddTHH:mm:ss}",
    TimeOnly t => $"t:{t:HH:mm:ss}",
    _ => $"s:{Py.Str(v)}",
};

var rosterPath = Path.Combine(local, "roster.xls");
var roster = DailyCheck.ReadList(rosterPath);
var rl = new JsonArray();
foreach (var r in roster)
{
    var o = new JsonObject();
    foreach (var (k, v) in r) o[k] = v;
    rl.Add(o);
}
outp["read_list"] = rl;

var ben = Beneficiaries.Load(rosterPath);
var bo = new JsonObject();
foreach (var (k, list) in ben)
{
    var arr = new JsonArray();
    foreach (var e in list)
        arr.Add(new JsonObject
        {
            ["name"] = e.Name, ["birth"] = e.Birth, ["gender"] = e.Gender, ["worker"] = e.Worker, ["group"] = e.Group,
            ["start"] = e.Start, ["end"] = e.End, ["display_name"] = e.DisplayName,
        });
    bo[k] = arr;
}
outp["beneficiaries"] = bo;

var co = new JsonObject();
foreach (var f in Directory.GetFiles(Path.Combine(local, "counsel"), "*.xlsx").OrderBy(f => f, StringComparer.Ordinal))
{
    try
    {
        var d = ScareCounsel.ReadCounsel(f);
        var fields = new JsonObject();
        foreach (var (k, v) in d.Fields) fields[k] = v;
        co[Path.GetFileName(f)] = new JsonObject
        {
            ["name"] = d.Name, ["fields"] = fields, ["bdy"] = new JsonArray(d.Bdy.Select(x => (JsonNode)x!).ToArray()),
            ["mnd"] = new JsonArray(d.Mnd.Select(x => (JsonNode)x!).ToArray()),
        };
    }
    catch (CounselError e)
    {
        co[Path.GetFileName(f)] = new JsonObject { ["error"] = e.Message };
    }
}
outp["counsel"] = co;

// 카톡: Jev 와 질문 없이
Jev.AskOverride = (_, _) => throw new InvalidOperationException("JEV");
var today = new DateOnly(2026, 11, 13);
var ip = new Interpreter(roster, (_, _) => null, today);
var text = File.ReadAllText(Path.Combine(local, "kakao.txt"));
var msgs = Words.SplitMessages(text, today, ip.Ics);
outp["kakao_msgs"] = new JsonArray(msgs.Select(m => (JsonNode)new JsonArray(m.Sender, m.Date.ToString("yyyy-MM-dd"), m.Body)).ToArray());
var items = new JsonArray();
var rows = new JsonArray();
foreach (var (s, d, b) in msgs)
{
    try
    {
        var its = ip.Interpret(s, d, b);
        var arr = new JsonArray();
        foreach (var it in its)
        {
            arr.Add(new JsonObject
            {
                ["ic"] = it.Ic, ["person"] = it.Person, ["date"] = it.Date?.ToString("yyyy-MM-dd"), ["frm"] = it.Frm, ["to"] = it.To,
                ["kind"] = it.Kind, ["action"] = it.Action, ["old"] = it.OldPerson, ["note"] = it.Note, ["status"] = it.Status,
                ["sub"] = it.Substitute,
                ["req"] = new JsonArray(it.RequestedKinds.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode)x!).ToArray()),
            });
            var row = Lists.ItemToRow(new Item
            {
                Ic = it.Ic, Person = it.Person, Date = it.Date, Frm = it.Frm, To = it.To, Kind = it.Kind, Action = it.Action,
                Note = it.Note, Source = "", Status = it.Status, Id = 0,
            });
            rows.Add(new JsonObject
            {
                ["id"] = row.Id, ["ic"] = row.Ic, ["person"] = row.Person, ["date"] = row.Date, ["time"] = row.Time, ["kind"] = row.Kind,
                ["action"] = row.Action, ["source"] = row.Source, ["checked"] = row.Checked, ["status"] = row.Status, ["note"] = row.Note,
            });
        }
        items.Add(arr);
    }
    catch (InvalidOperationException e) when (e.Message == "JEV")
    {
        items.Add("JEV");
    }
    catch (Exception e)
    {
        items.Add("ERROR:" + e.GetType().Name);
    }
}
outp["kakao_items"] = items;
outp["kakao_rows"] = rows;

// 제공계획서 변환: 임시 바탕화면에서
var desk = Directory.CreateTempSubdirectory();
try
{
    Converter.DesktopOverride = desk.FullName;
    var (src, dst) = Converter.WorkFolders();
    foreach (var f in Directory.GetFiles(Path.Combine(local, "plan_src")).OrderBy(f => f, StringComparer.Ordinal))
    {
        File.Copy(f, Path.Combine(src, Path.GetFileName(f)));
        File.SetLastWriteTime(Path.Combine(src, Path.GetFileName(f)), File.GetLastWriteTime(f));
    }
    var workers = Lists.ParseWorkers(File.ReadAllText(Path.Combine(local, "workers.json")));
    var logs = new List<string>();
    var res = Converter.Run(logs.Add, rosterPath, workers);
    outp["conv_logs"] = new JsonArray(logs.Select(x => (JsonNode)x!).ToArray());
    outp["conv_result"] = new JsonArray(res.Total, res.Ok,
        new JsonArray(res.Failed.Select(x => (JsonNode)new JsonArray(x.Name, x.Reason)).ToArray()),
        new JsonArray(res.DeletableSources.Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode)x!).ToArray()),
        new JsonArray(res.DeletableOutputs.Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode)x!).ToArray()));
    var files = new JsonObject();
    foreach (var f in Directory.GetFiles(dst).Where(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal))
    {
        var ws = XlsxReader.Read(File.ReadAllBytes(f), active: true);
        var sheet = new JsonArray();
        foreach (var r in ws.Rows())
            sheet.Add(new JsonArray(r.Select(v => (JsonNode?)Val(v)).ToArray()));
        files[Path.GetFileName(f)] = sheet;
    }
    outp["conv_files"] = files;
    var keep = Path.Combine(local, "cs_out");
    if (Directory.Exists(keep)) Directory.Delete(keep, true);
    Directory.CreateDirectory(keep);
    foreach (var f in Directory.GetFiles(dst)) File.Copy(f, Path.Combine(keep, Path.GetFileName(f)));
}
finally
{
    desk.Delete(true);
}
File.WriteAllText(Path.Combine(local, "cs.json"), outp.ToJsonString(JsonOpts.Relaxed));
Console.WriteLine($"ok {msgs.Count} msgs");
