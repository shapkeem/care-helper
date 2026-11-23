using System.Text.Json;
using System.Text.RegularExpressions;
using CareHelper.Browser;
using Microsoft.Playwright;

namespace CareHelper.Scare;

/// <summary>서비스상태관리의 어르신 한 분.</summary>
public sealed record DocPerson(string TgtrId, string Name, string Birth, string Dong, string Worker, string StatusCd, string Status, string SvcTy);

/// <summary>s-care 서류 받기: 서비스상태관리 → 이름 → 선정조사 이력(선정조사 결과서)·서비스 제공계획 이력(상담지·서비스 제공계획서),
/// 줄 → 제공안내서. 모두 s-care 보고서 창(ClipReport)에서 [PDF 저장]을 눌러 받는다. 읽기만 하고 아무것도 저장하지 않는다.</summary>
public static class ScareDocs
{
    public static readonly string[] Kinds = { "선정결과서", "상담지", "서비스 제공계획서", "제공안내서" };
    const string Agen = "A26500005"; // 광안노인복지관
    const string Sido = "26", Signgu = "26500";

    // s-care 화면이 쓰는 것과 같은 방식으로 부른다 (eicsAjax 머리, 폼 형식)
    const string PostJs = @"() => { window.__docPost = async (url, data) => {
  const r = await fetch(url, {method: 'POST', headers: {'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8', 'eicsAjax': 'true'},
                              body: new URLSearchParams(data)});
  const j = await r.json();
  if (j.sessionOut) throw new Error('login');
  return j;
}; }";

    static async Task<IPage> SitePageAsync(IBrowserContext ctx)
    {
        var page = ctx.Pages.FirstOrDefault(ScarePlan.LoggedIn)
                   ?? throw new ScareNeedLogin("s-care에 로그인되어 있지 않아요. [s-care 열기]를 누르고 로그인해 주세요.");
        await page.EvaluateAsync(PostJs);
        return page;
    }

    /// <summary>서비스상태관리 목록 (서비스중·대기중 등 지금 받는 분들). 종결·사후관리는 뺀다.</summary>
    public static async Task<List<DocPerson>> ListAsync()
    {
        if (!ScarePlan.ChromeAlive())
            throw new ScareNeedLogin("s-care 크롬이 꺼져 있어요. [s-care 열기]를 누르고 로그인해 주세요.");
        await using var pw = await Pw.ConnectAsync(ScarePlan.Cdp);
        var page = await SitePageAsync(pw.Context);
        JsonElement res;
        try
        {
            res = await page.EvaluateAsync<JsonElement>(@"async (a) => {
  const j = await window.__docPost('/svc/selectSvcStaMngList.do', {schSido: a.sido, schSigngu: a.signgu, schAgen: a.agen, schProvStaCd: '', page: '1', rows: '3000'});
  return (j.resultList || []).map(r => [r.tgtrId, r.tgtrNm, r.birthYmd, r.adstrdNm, r.astntUsrNm, r.provStaCd, r.provStaCdNm, r.svcTyCdNm]);
}", new { sido = Sido, signgu = Signgu, agen = Agen });
        }
        catch (PlaywrightException e) when (e.Message.Contains("login"))
        {
            throw new ScareNeedLogin("s-care 로그인이 풀렸어요. 크롬 창에서 다시 로그인해 주세요.");
        }
        var list = new List<DocPerson>();
        foreach (var r in res.EnumerateArray())
        {
            var v = r.Strings();
            if (v.Count < 8) continue;
            var st = v[6];
            if (st.Contains("종결") || st.Contains("사후관리")) continue;
            list.Add(new DocPerson(v[0], v[1], Regex.Replace(v[2], @"\D", ""), v[3], v[4], v[5], st, v[7]));
        }
        return list.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    // 서류 하나의 보고서 자료를 받아 보고서 창에 보낼 내용으로 만든다 (s-care 의 ReportFunc 와 같은 모양). 없으면 {none: '이유'}
    const string ReportJs = @"async (a) => {
  const P = window.__docPost;
  const pick = (list, ok) => (list || []).filter(ok).sort((x, y) => String(y.invsDe || y.dlibDe || y.dlrqDe || '').localeCompare(String(x.invsDe || x.dlibDe || x.dlrqDe || '')))[0];
  let rep, name;
  if (a.kind === '선정결과서') {
    const l = await P('/tgtr/selectTgtrInvsPopList.do', {tgtrId: a.id, schAgen: a.agen});
    const r = pick(l.resultList, x => x.selivId && String(x.invsStaNm || '').includes('완료'));
    if (!r) return {none: '선정조사 이력이 없어요'};
    rep = await P('/rept/selectEicsInvest.do', {selivId: r.selivId, rptType: 'V', formType: ''});
    if (!rep.resultList || !rep.resultList.tgtrInfo) return {none: '선정조사 결과서가 없어요'};
    name = 'EicsInvest_' + rep.resultList.tgtrInfo.invspId;
  } else if (a.kind === '상담지' || a.kind === '서비스 제공계획서') {
    const l = await P('/tgtr/selectTgtrProvPlanPopList.do', {tgtrId: a.id, schAgen: a.agen});
    const counsel = a.kind === '상담지';
    const r = pick(l.resultList, x => (counsel ? x.cnslpYn : x.pvplDtlYn) === 'Y');
    if (!r) return {none: (counsel ? '상담지' : '서비스 제공계획서') + '가 없어요'};
    if (counsel) { rep = await P('/rept/selectEicsCounsel.do', {selivId: r.selivId, pvplId: r.pvplId, rptType: 'C', formType: ''}); name = 'EicsCounsel'; }
    else { rep = await P('/rept/selectEicsProvPlan.do', {selivId: r.selivId, pvplId: r.pvplId, rptType: 'P', formType: ''}); name = 'EicsProvPlanPar'; }
  } else {
    if (a.status !== 'S' && a.status !== 'W') return {none: '서비스중·대기중일 때만 제공안내서가 있어요'};
    rep = await P('/rept/selectEicsProvGuide.do', {tgtrId: a.id, rptType: 'S'});
    name = a.status === 'S' ? 'EicsProvGuide' : 'EicsProvGuideWaiter';
  }
  const rl = rep.resultList;
  if (!rl || Object.keys(rl).length === 0) return {none: '자료가 없어요'};
  let s = '<rexdataset>'; let c = 0;
  for (const k of Object.keys(rl).sort()) {
    c++;
    [].concat(rl[k]).forEach(o => { s += '<rexrow' + c + '>'; for (const kk in o) s += '<' + kk + '><![CDATA[' + o[kk] + ']]></' + kk + '>'; s += '</rexrow' + c + '>'; });
  }
  s += '</rexdataset>';
  return {url: rep.reportUrl, data: s, name: name, cnt: name === 'EicsCounselForm' ? 1 : 2};
}";

    // [PDF 저장]을 누르고, 보고서 창이 파일 저장 양식을 보낼 때 그 양식을 직접 보내 PDF 를 base64 로 받는다. 못 받으면 ''
    const string GrabPdfJs = @"async (ms) => {
  const got = new Promise((res, rej) => {
    const orig = window.__origSubmit || HTMLFormElement.prototype.submit;
    window.__origSubmit = orig;
    HTMLFormElement.prototype.submit = function () {
      if (!/saveform/i.test(this.id || this.name || '')) return orig.call(this);
      fetch(this.action, {method: 'POST', body: new URLSearchParams(new FormData(this)), credentials: 'include'})
        .then(r => r.arrayBuffer()).then(b => {
          const u = new Uint8Array(b); let s = '';
          for (let i = 0; i < u.length; i += 8192) s += String.fromCharCode.apply(null, u.subarray(i, i + 8192));
          res(btoa(s));
        }).catch(e => rej(e));
    };
  });
  document.querySelector('[id^=re_pdf]').click();
  return await Promise.race([got, new Promise(r => setTimeout(() => r(''), ms))]);
}";

    /// <summary>크롬이 내려받는 폴더 (윈도우 '다운로드')</summary>
    public static string DownloadsDir()
    {
        try
        {
            var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            var v = k?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
            if (!string.IsNullOrEmpty(v)) return Environment.ExpandEnvironmentVariables(v);
        }
        catch
        {
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>파일 이름에 못 쓰는 글자를 뺀다.</summary>
    public static string Safe(string s) => Regex.Replace(s ?? "", @"[\\/:*?""<>|]", "").Trim();

    public static string FolderOf(string root, DocPerson p) =>
        Path.Combine(root, Safe($"{p.Name}_{(p.Birth.Length >= 8 ? p.Birth[2..8] : p.Birth)}"));

    /// <summary>한 분의 서류들을 받는다 → "받음 3/3" 같은 결과. 없는 서류는 이유를 붙인다.</summary>
    public static async Task<string> FetchOneAsync(IPage viewer, DocPerson p, IReadOnlyList<string> kinds, string root, Action<string> log)
    {
        var ctx = viewer.Context;
        var site = await SitePageAsync(ctx);
        var folder = FolderOf(root, p);
        Directory.CreateDirectory(folder);
        int got = 0;
        var notes = new List<string>();
        foreach (var kind in kinds)
        {
            JsonElement r;
            try
            {
                r = await site.EvaluateAsync<JsonElement>(ReportJs, new { kind, id = p.TgtrId, agen = Agen, status = p.StatusCd });
            }
            catch (PlaywrightException e) when (e.Message.Contains("login"))
            {
                throw new ScareNeedLogin("s-care 로그인이 풀렸어요. 크롬 창에서 다시 로그인해 주세요.");
            }
            var none = r.Str("none");
            if (none.Length > 0)
            {
                notes.Add($"{kind}: {none}");
                continue;
            }
            // 보고서 창을 열고 다 그려지면 [PDF 저장]. 사이트가 보내는 PDF 를 가로채 어르신 이름으로 저장한다
            // (브라우저 다운로드로 두면 '다운로드' 폴더에 report.pdf 로 쌓인다)
            await viewer.SetContentAsync("<form id=f method=post><input type=hidden name=data><input type=hidden name=rptName>" +
                                         "<input type=hidden name=connectCnt></form>");
            await viewer.EvaluateAsync(@"(d) => { const f = document.getElementById('f'); f.action = d.url; f.data.value = d.data;
                f.rptName.value = d.name; f.connectCnt.value = String(d.cnt); f.submit(); }",
                new { url = r.Str("url"), data = r.Str("data"), name = r.Str("name"), cnt = r.Int("cnt") });
            try
            {
                await viewer.WaitForFunctionAsync(
                    "() => { const b = document.querySelector('[id^=re_pdf]'); const n = document.querySelector('[id^=re_input]');" +
                    " return b && n && n.value && document.body.innerText.length > 300; }", null, new() { Timeout = 90000 });
                await Task.Delay(500);
            }
            catch (TimeoutException)
            {
                var st = await viewer.EvaluateAsync<string>("() => location.href + ' | pdf=' + !!document.querySelector('[id^=re_pdf]') + ' | tot=' + " +
                    "((document.querySelector('[id^=re_totalCountNumber]') || {}).textContent) + ' | len=' + document.body.innerText.length + ' | vis=' + document.visibilityState");
                throw new ScareError($"{kind}: 보고서 창이 다 열리지 않았어요 ({st})");
            }
            // [PDF 저장]은 숨은 양식을 보내 크롬이 report.pdf 로 내려받게 한다. 그러면 크롬이 '여러 파일 다운로드'를 막을 수 있어서,
            // 그 양식을 보내는 순간 가로채 같은 내용을 직접 받아 온다 (다운로드 폴더에 아무것도 생기지 않는다)
            byte[]? bytes = null;
            for (int tries = 0; tries < 3 && bytes == null; tries++) // 보고서를 막 연 직후에는 [PDF 저장]이 안 먹을 때가 있어 다시 누른다
            {
                var b64 = await viewer.EvaluateAsync<string>(GrabPdfJs, 15000 + tries * 15000);
                if (b64.Length > 0) bytes = Convert.FromBase64String(b64);
            }
            if (bytes == null) throw new ScareError($"{p.Name} {kind}: PDF 를 받지 못했어요 (s-care 보고서 창이 답하지 않음)");
            if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P')
                throw new ScareError($"{p.Name} {kind}: 받은 파일이 PDF 가 아니에요");
            var path = Path.Combine(folder, Safe($"{p.Name}_{kind}.pdf"));
            await File.WriteAllBytesAsync(path, bytes);
            got++;
        }
        log($"{p.Name}: {got}/{kinds.Count}개 받음{(notes.Count > 0 ? " · " + string.Join(", ", notes) : "")}");
        var msg = $"받음 {got}/{kinds.Count}";
        return notes.Count > 0 ? $"{msg} · {string.Join(", ", notes)}" : msg;
    }

    /// <summary>한 분씩 차례로 받는다. 보고서 창은 앞에 보이는 탭이어야 다 그려져서 탭 하나를 앞으로 꺼내 쓴다.
    /// 읽기만 하므로 문제가 있어도 다음 분으로 넘어간다.</summary>
    /// <summary>받는 동안 다운로드 폴더에 새로 생긴 report*.pdf (두 번 눌러 하나 더 받아진 것)를 치운다.</summary>
    static void CleanDownloads(HashSet<string> before)
    {
        try
        {
            foreach (var f in Directory.GetFiles(DownloadsDir(), "report*.pdf").Where(f => !before.Contains(f)))
                try { File.Delete(f); } catch { }
        }
        catch
        {
        }
    }

    public static async Task RunAsync(List<DocPerson> people, IReadOnlyList<string> kinds, string root,
                                      Action<int, string, string> onRow, Action<string> log, CancellationToken stop = default)
    {
        if (!ScarePlan.ChromeAlive())
            throw new ScareNeedLogin("s-care 크롬이 꺼져 있어요. [s-care 열기]를 누르고 로그인해 주세요.");
        Directory.CreateDirectory(root);
        await using var pw = await Pw.ConnectAsync(ScarePlan.Cdp);
        await SitePageAsync(pw.Context); // 로그인 확인
        var viewer = await pw.Context.NewPageAsync();
        try
        {
            await viewer.BringToFrontAsync();
            for (int i = 0; i < people.Count; i++)
            {
                if (stop.IsCancellationRequested)
                {
                    log("멈춤을 눌러 여기서 멈췄어요.");
                    break;
                }
                onRow(i, "받는 중", "");
                try
                {
                    onRow(i, "완료", await FetchOneAsync(viewer, people[i], kinds, root, log));
                }
                catch (ScareNeedLogin)
                {
                    throw;
                }
                catch (Exception e)
                {
                    var first = e.Message.Split('\n')[0];
                    onRow(i, "확인 필요", first.Length > 150 ? first[..150] : first);
                }
            }
        }
        finally
        {
            await Pw.CloseQuietly(viewer);
        }
    }
}
