using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var latestFrames = new ConcurrentDictionary<string, byte[]>();
var latestReports = new ConcurrentDictionary<string, ReportData>();
var frameTimestamps = new ConcurrentDictionary<string, long>();

// ============ استقبال التقرير (JSON) ============
app.MapPost("/report", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    string json = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(json))
        return Results.BadRequest("Empty body");

    var report = JsonConvert.DeserializeObject<ReportData>(json);
    if (report == null || string.IsNullOrEmpty(report.ChildId))
        return Results.BadRequest("Invalid data");

    report.ReceivedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    latestReports[report.ChildId] = report;

    return Results.Ok(new { status = "ok" });
});

// ============ استقبال الإطار (Form Data) ============
app.MapPost("/frame", async (HttpContext context) =>
{
    if (!context.Request.HasFormContentType)
        return Results.BadRequest("Invalid form data");

    var form = await context.Request.ReadFormAsync();
    string childId = form["child_id"].ToString();
    var file = form.Files["frame"];

    if (file == null || string.IsNullOrEmpty(childId))
        return Results.BadRequest("Missing child_id or frame");

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    byte[] bytes = ms.ToArray();

    latestFrames[childId] = bytes;
    frameTimestamps[childId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    return Results.Ok(new { status = "ok", size = bytes.Length });
});

// ============ الحصول على آخر إطار لطفل ============
app.MapGet("/frame/{childId}", (string childId) =>
{
    if (!latestFrames.TryGetValue(childId, out var bytes))
        return Results.NotFound("No frame yet");

    return Results.File(bytes, "image/jpeg", enableRangeProcessing: true);
});

// ============ قائمة الأطفال المتصلين ============
app.MapGet("/children", () =>
{
    var children = latestReports.Values.Select(r => new
    {
        child_id = r.ChildId,
        last_update = r.ReceivedAt,
        active_window = r.ActiveWindow,
        ip = r.IPAddress,
        running_apps_count = r.RunningApps?.Count ?? 0,
        has_live_frame = latestFrames.ContainsKey(r.ChildId),
        last_frame_ago_ms = frameTimestamps.TryGetValue(r.ChildId, out var ts)
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts
            : -1
    });
    return Results.Json(children);
});

// ============ لوحة التحكم ============
app.MapGet("/dashboard", () =>
{
    var html = BuildDashboard();
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/", () => Results.Redirect("/dashboard"));

// ============ صفحة التشخيص ============
app.MapGet("/debug", () =>
{
    var info = new
    {
        children_count = latestReports.Count,
        frames_count = latestFrames.Count,
        children = latestReports.Keys.ToArray(),
        current_time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
    };
    return Results.Json(info);
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
app.Run($"http://0.0.0.0:{port}");

// ============ لوحة التحكم (HTML) ============
static string BuildDashboard()
{
    var sb = new StringBuilder();
    sb.Append("<!DOCTYPE html>");
    sb.Append("<html lang='ar' dir='rtl'>");
    sb.Append("<head>");
    sb.Append("<meta charset='UTF-8'>");
    sb.Append("<meta name='viewport' content='width=device-width, initial-scale=1.0'>");
    sb.Append("<title>مراقبة الأبناء - بث مباشر</title>");
    sb.Append("<style>");
    sb.Append("* { box-sizing: border-box; margin: 0; padding: 0; }");
    sb.Append("body { background: #0f172a; color: #e2e8f0; font-family: 'Segoe UI', Tahoma, sans-serif; padding: 20px; }");
    sb.Append("h1 { color: #60a5fa; text-align: center; margin-bottom: 30px; font-size: 28px; }");
    sb.Append(".grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(400px, 1fr)); gap: 20px; max-width: 1600px; margin: 0 auto; }");
    sb.Append(".child-card { background: #1e293b; border-radius: 16px; padding: 20px; box-shadow: 0 4px 20px rgba(0,0,0,0.3); }");
    sb.Append(".child-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 15px; }");
    sb.Append(".child-name { font-size: 22px; font-weight: bold; color: #60a5fa; }");
    sb.Append(".status { display: flex; align-items: center; gap: 8px; font-size: 14px; }");
    sb.Append(".dot { width: 12px; height: 12px; border-radius: 50%; background: #22c55e; animation: pulse 2s infinite; }");
    sb.Append(".dot.offline { background: #ef4444; animation: none; }");
    sb.Append("@keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.5; } }");
    sb.Append(".stream-container { background: #000; border-radius: 12px; padding: 10px; text-align: center; min-height: 250px; display: flex; align-items: center; justify-content: center; }");
    sb.Append(".stream-img { max-width: 100%; max-height: 400px; display: block; margin: 0 auto; border-radius: 8px; }");
    sb.Append(".no-stream { color: #64748b; font-size: 16px; padding: 80px 20px; }");
    sb.Append(".info-grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 10px; margin-top: 15px; }");
    sb.Append(".info-item { background: #0f172a; padding: 12px; border-radius: 8px; }");
    sb.Append(".info-label { color: #94a3b8; font-size: 11px; text-transform: uppercase; margin-bottom: 4px; }");
    sb.Append(".info-value { color: #e2e8f0; font-size: 14px; word-break: break-word; }");
    sb.Append("</style>");
    sb.Append("</head><body>");
    sb.Append("<h1>🎥 مراقبة الأبناء - بث مباشر</h1>");
    sb.Append("<div class='grid' id='children'></div>");
    sb.Append("<script>");
    sb.Append("async function loadChildren(){");
    sb.Append("  try {");
    sb.Append("    const r = await fetch('/children');");
    sb.Append("    const children = await r.json();");
    sb.Append("    renderChildren(children);");
    sb.Append("  } catch(e) { console.error(e); }");
    sb.Append("}");
    sb.Append("function renderChildren(children){");
    sb.Append("  const c = document.getElementById('children');");
    sb.Append("  if(children.length === 0){");
    sb.Append("    c.innerHTML = '<div class=\"child-card\"><div class=\"no-stream\">⏳ في انتظار اتصال الأجهزة...</div></div>';");
    sb.Append("    return;");
    sb.Append("  }");
    sb.Append("  let h = '';");
    sb.Append("  const names = { 'mohamed': 'محمد', 'ahmed': 'أحمد', 'abdullah': 'عبد الله', 'wahiba': 'وهيبة' };");
    sb.Append("  for(const x of children){");
    sb.Append("    const live = x.last_frame_ago_ms >= 0 && x.last_frame_ago_ms < 5000;");
    sb.Append("    const displayName = names[x.child_id] || x.child_id;");
    sb.Append("    h += '<div class=\"child-card\">';");
    sb.Append("    h += '<div class=\"child-header\">';");
    sb.Append("    h += '<div class=\"child-name\">👤 ' + displayName + '</div>';");
    sb.Append("    h += '<div class=\"status\"><span class=\"dot ' + (live ? '' : 'offline') + '\"></span> ' + (live ? 'مباشر' : 'غير متصل') + '</div>';");
    sb.Append("    h += '</div>';");
    sb.Append("    h += '<div class=\"stream-container\">';");
    sb.Append("    if(x.has_live_frame) {");
    sb.Append("      h += '<img class=\"stream-img\" src=\"/frame/' + x.child_id + '?t=' + Date.now() + '\">';");
    sb.Append("    } else {");
    sb.Append("      h += '<div class=\"no-stream\">📷 لا توجد صورة</div>';");
    sb.Append("    }");
    sb.Append("    h += '</div>';");
    sb.Append("    h += '<div class=\"info-grid\">';");
    sb.Append("    h += '<div class=\"info-item\"><div class=\"info-label\">آخر تحديث</div><div class=\"info-value\">' + (x.last_update || '—') + '</div></div>';");
    sb.Append("    h += '<div class=\"info-item\"><div class=\"info-label\">النافذة النشطة</div><div class=\"info-value\">' + (x.active_window || '—') + '</div></div>';");
    sb.Append("    h += '<div class=\"info-item\"><div class=\"info-label\">عنوان IP</div><div class=\"info-value\">' + (x.ip || '—') + '</div></div>';");
    sb.Append("    h += '<div class=\"info-item\"><div class=\"info-label\">التطبيقات</div><div class=\"info-value\">' + x.running_apps_count + '</div></div>';");
    sb.Append("    h += '</div></div>';");
    sb.Append("  }");
    sb.Append("  c.innerHTML = h;");
    sb.Append("}");
    sb.Append("function refreshStreams(){");
    sb.Append("  document.querySelectorAll('.stream-img').forEach(i => {");
    sb.Append("    const base = i.src.split('?')[0];");
    sb.Append("    i.src = base + '?t=' + Date.now();");
    sb.Append("  });");
    sb.Append("}");
    sb.Append("loadChildren();");
    sb.Append("setInterval(refreshStreams, 500);");
    sb.Append("setInterval(loadChildren, 3000);");
    sb.Append("</script></body></html>");
    return sb.ToString();
}

// ============ كلاس البيانات ============
public class ReportData
{
    [JsonProperty("child_id")] public string ChildId { get; set; } = "";
    [JsonProperty("timestamp")] public string Timestamp { get; set; } = "";
    [JsonProperty("active_window")] public string ActiveWindow { get; set; } = "";
    [JsonProperty("running_apps")] public List<string> RunningApps { get; set; } = new();
    [JsonProperty("typed_text")] public string TypedText { get; set; } = "";
    [JsonProperty("ip")] public string IPAddress { get; set; } = "";
    [JsonProperty("received_at")] public string ReceivedAt { get; set; } = "";
}