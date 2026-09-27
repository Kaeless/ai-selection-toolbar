using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiSelectionToolbar.Core;

namespace AiSelectionToolbar.Linux;

/// <summary>Loopback-only browser management API. The UI assets are shared with Windows.</summary>
internal sealed class LinuxManagementServer : IDisposable
{
    private const int MaxBodyBytes = 65536;
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true
    };
    private readonly LinuxHost host;
    private readonly HttpListener listener = new();
    private readonly string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private CancellationTokenSource stop = new();
    private Task acceptLoop;
    private int port;

    public event Action SettingsChanged;
    public string Url => $"http://127.0.0.1:{port}/#{token}";

    public LinuxManagementServer(LinuxHost host) => this.host = host;

    public void Start()
    {
        if (listener.IsListening) return;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        acceptLoop = AcceptAsync(stop.Token);
    }

    public void OpenInBrowser()
    {
        if (!listener.IsListening) throw new InvalidOperationException("管理服务尚未启动");
        var open = new ProcessStartInfo("xdg-open");
        open.ArgumentList.Add(Url);
        Process.Start(open);
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            _ = Task.Run(() => Handle(context), cancellationToken);
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            if (request.RemoteEndPoint == null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
                request.Headers["Host"] != $"127.0.0.1:{port}")
            { Write(context, 403, "text/plain; charset=utf-8", "Forbidden"); return; }

            var path = request.Url?.AbsolutePath ?? "/";
            if (request.HttpMethod == "GET" && TryGetAsset(path, out var asset, out var mime))
            { Write(context, 200, mime, asset); return; }

            if (!path.StartsWith("/api/", StringComparison.Ordinal) || !Authorized(context))
            { Write(context, 401, "text/plain; charset=utf-8", "Unauthorized"); return; }
            var origin = request.Headers["Origin"];
            var expectedOrigin = $"http://127.0.0.1:{port}";
            if ((!string.IsNullOrEmpty(origin) && origin != expectedOrigin) ||
                (request.HttpMethod == "POST" && origin != expectedOrigin))
            { Write(context, 403, "text/plain; charset=utf-8", "Forbidden"); return; }

            if (request.HttpMethod == "GET" && path == "/api/settings")
                WriteJson(context, GetSettings());
            else if (request.HttpMethod == "GET" && path == "/api/history")
            {
                var offset = ParseNonNegative(request.QueryString["offset"], 0);
                var items = host.SearchHistory(request.QueryString["search"] ?? "", offset)
                    .Select(item => new {
                        item.Id, TimeUtc = new DateTimeOffset(item.CreatedUtc).ToUnixTimeMilliseconds(), item.Action,
                        Selection = item.SelectedText, Result = item.Response,
                        Application = item.SourceApplication, item.SourceTitle, item.Prompt
                    }).ToArray();
                WriteJson(context, items);
            }
            else if (request.HttpMethod == "POST") HandlePost(context, path);
            else Write(context, 404, "text/plain; charset=utf-8", "Not found");
        }
        catch (ArgumentException error) { Write(context, 400, "text/plain; charset=utf-8", error.Message); }
        catch (JsonException) { Write(context, 400, "text/plain; charset=utf-8", "无效的 JSON 请求"); }
        catch (Exception) { Write(context, 500, "text/plain; charset=utf-8", "保存失败，请检查设置和本机权限。"); }
        finally { try { context.Response.Close(); } catch { } }
    }

    private void HandlePost(HttpListenerContext context, string path)
    {
        var request = context.Request;
        if (!string.Equals(request.ContentType?.Split(';')[0], "application/json", StringComparison.OrdinalIgnoreCase))
        { Write(context, 415, "text/plain; charset=utf-8", "Expected application/json"); return; }
        if (request.ContentLength64 < 0 || request.ContentLength64 > MaxBodyBytes)
        { Write(context, 413, "text/plain; charset=utf-8", "Request body too large"); return; }
        using var input = new MemoryStream();
        request.InputStream.CopyTo(input);
        if (input.Length > MaxBodyBytes) { Write(context, 413, "text/plain; charset=utf-8", "Request body too large"); return; }
        var body = JsonSerializer.Deserialize<JsonElement>(input.ToArray(), JsonOptions);

        if (path == "/api/settings")
        {
            var current = host.LoadSettings();
            var updated = CopySettings(current);
            updated.AutoShow = GetBool(body, "AutoShow");
            updated.StartOnLogin = GetBool(body, "StartOnLogin");
            updated.TranslationTargetLanguage = GetString(body, "TargetLanguage");
            updated.NotesDirectory = GetString(body, "NotesDirectory");
            updated.ToolbarStyle = GetString(body, "ToolbarStyle");
            updated.ToolbarAccentColor = GetString(body, "ToolbarAccentColor");
            updated.CustomActions = body.GetProperty("CustomActions").Deserialize<List<CustomActionDefinition>>(JsonOptions) ?? new();
            foreach (var action in updated.CustomActions)
                if (string.IsNullOrWhiteSpace(action.Id)) action.Id = Guid.NewGuid().ToString("N");
            ValidateSettings(updated);
            host.SaveSettings(updated, null);
            host.SetStartup(updated.StartOnLogin);
            SettingsChanged?.Invoke();
        }
        else if (path == "/api/connection")
        {
            var current = host.LoadSettings();
            var inputValue = body.Deserialize<ApiConnectionInput>(JsonOptions) ?? throw new ArgumentException("连接设置无效");
            ValidateConnection(inputValue);
            var endpointChanged = !string.Equals(current.BaseUrl?.TrimEnd('/'), inputValue.ApiBaseUrl.TrimEnd('/'), StringComparison.Ordinal);
            current.BaseUrl = inputValue.ApiBaseUrl.TrimEnd('/');
            current.Model = inputValue.Model;
            host.SaveSettings(current, inputValue.ApiKey, endpointChanged);
            SettingsChanged?.Invoke();
        }
        else if (path is "/api/exclusions/add" or "/api/exclusions/remove")
        {
            var app = GetString(body, "application").Trim();
            if (app.Length is 0 or > 200 || app.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("程序名称无效");
            if (path.EndsWith("add", StringComparison.Ordinal)) host.ExcludeApplication(app);
            else host.IncludeApplication(app);
            SettingsChanged?.Invoke();
        }
        else if (path == "/api/history/delete")
        {
            if (!body.TryGetProperty("id", out var id) || !id.TryGetInt64(out var value) || value <= 0)
                throw new ArgumentException("历史记录编号无效");
            host.DeleteHistory(value);
        }
        else if (path == "/api/history/clear") host.ClearHistory();
        else { Write(context, 404, "text/plain; charset=utf-8", "Not found"); return; }
        WriteJson(context, new { ok = true });
    }

    private object GetSettings()
    {
        var settings = host.LoadSettings();
        return new {
            AutoShow = settings.AutoShow, StartOnLogin = settings.StartOnLogin,
            TargetLanguage = settings.TranslationTargetLanguage,
            NotesDirectory = MarkdownNoteStore.ResolveDirectory(settings.NotesDirectory),
            ApiBaseUrl = settings.BaseUrl, Model = settings.Model,
            ToolbarStyle = settings.ToolbarStyle, ToolbarAccentColor = settings.ToolbarAccentColor,
            CustomActions = settings.CustomActions, ExcludedApplications = settings.ExcludedApplications
        };
    }

    private static AppSettings CopySettings(AppSettings value) => new() {
        BaseUrl = value.BaseUrl, Model = value.Model, TimeoutSeconds = value.TimeoutSeconds,
        ProtectedApiKey = value.ProtectedApiKey,
        TranslationTargetLanguage = value.TranslationTargetLanguage,
        ExcludedApplications = new(value.ExcludedApplications ?? new()), AutoShow = value.AutoShow,
        NotesDirectory = value.NotesDirectory, StartOnLogin = value.StartOnLogin,
        CustomActions = value.CustomActions?.Select(x => new CustomActionDefinition { Id = x.Id, Name = x.Name, Prompt = x.Prompt }).ToList() ?? new(),
        ToolbarStyle = value.ToolbarStyle, ToolbarAccentColor = value.ToolbarAccentColor
    };

    private static void ValidateSettings(AppSettings value)
    {
        if (string.IsNullOrWhiteSpace(value.TranslationTargetLanguage) || value.TranslationTargetLanguage.Length > 50)
            throw new ArgumentException("翻译目标语言无效");
        if (value.NotesDirectory?.Length > 2048) throw new ArgumentException("笔记目录过长");
        if (value.ToolbarStyle is not ("standard" or "compact") ||
            value.ToolbarAccentColor == null || !System.Text.RegularExpressions.Regex.IsMatch(value.ToolbarAccentColor, "^#[0-9a-fA-F]{6}$"))
            throw new ArgumentException("工具栏外观设置无效");
        if (value.CustomActions.Count > 8 || value.CustomActions.Any(x => x == null || string.IsNullOrWhiteSpace(x.Id) ||
            string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 20 || string.IsNullOrWhiteSpace(x.Prompt) || x.Prompt.Length > 4000))
            throw new ArgumentException("自定义按钮设置无效");
        if (value.CustomActions.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != value.CustomActions.Count)
            throw new ArgumentException("自定义按钮编号重复");
    }

    private static void ValidateConnection(ApiConnectionInput value)
    {
        if (value.ApiBaseUrl?.Length is 0 or > 2048 || value.Model?.Length is 0 or > 200 || value.ApiKey?.Length > 8192 ||
            !Uri.TryCreate(value.ApiBaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)))
            throw new ArgumentException("API 地址、模型或密钥无效；远程地址必须使用 HTTPS。");
    }

    private bool Authorized(HttpListenerContext context)
    {
        var supplied = context.Request.Headers["Authorization"];
        var expected = Encoding.UTF8.GetBytes("Bearer " + token);
        var actual = Encoding.UTF8.GetBytes(supplied ?? "");
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool TryGetAsset(string path, out byte[] bytes, out string mime)
    {
        var name = path switch { "/" => "ManagementPage.html", "/app.js" => "ManagementPage.js", "/style.css" => "ManagementPage.css", _ => null };
        if (name == null) { bytes = null; mime = null; return false; }
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AiSelectionToolbar.Linux." + name);
        if (stream == null) throw new InvalidOperationException("管理页面资源缺失：" + name);
        using var output = new MemoryStream(); stream.CopyTo(output); bytes = output.ToArray();
        mime = name.EndsWith(".html", StringComparison.Ordinal) ? "text/html; charset=utf-8" :
            name.EndsWith(".js", StringComparison.Ordinal) ? "application/javascript; charset=utf-8" : "text/css; charset=utf-8";
        return true;
    }

    private static int ParseNonNegative(string raw, int fallback)
    {
        if (string.IsNullOrEmpty(raw)) return fallback;
        if (!int.TryParse(raw, out var value) || value < 0) throw new ArgumentException("分页参数无效");
        return value;
    }
    private static bool GetBool(JsonElement value, string name) => value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.True;
    private static string GetString(JsonElement value, string name) => value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : "";
    private static void WriteJson(HttpListenerContext context, object value) =>
        Write(context, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(value));
    private static void Write(HttpListenerContext context, int status, string type, string body) =>
        Write(context, status, type, Encoding.UTF8.GetBytes(body));
    private static void Write(HttpListenerContext context, int status, string type, byte[] body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = type;
        context.Response.ContentLength64 = body.Length;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
        context.Response.OutputStream.Write(body);
    }

    public void Dispose()
    {
        stop.Cancel();
        if (listener.IsListening) listener.Stop();
        listener.Close();
        try { acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        stop.Dispose();
    }
}
