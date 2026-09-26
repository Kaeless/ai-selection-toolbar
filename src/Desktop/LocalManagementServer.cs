using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;

namespace AiSelectionToolbar.Desktop
{
    // Intentionally uses a loopback TCP listener rather than a wildcard HttpListener prefix.
    // This also avoids requiring administrator URL reservations on Windows.
    internal sealed class LocalManagementServer : IDisposable
    {
        private readonly Func<DesktopSettings> _getSettings;
        private readonly Action<DesktopSettings> _setSettings;
        private readonly Func<string, int, HistoryItem[]> _getHistory;
        private readonly Action<string> _addExclusion;
        private readonly Action<string> _removeExclusion;
        private readonly Action<ApiConnectionInput> _saveApiConnection;
        private readonly Action<string> _selectApiConnection;
        private readonly Action<string> _deleteApiConnection;
        private readonly Action<long> _deleteHistory;
        private readonly Action _clearHistory;
        private readonly string _token;
        private TcpListener _listener;
        private CancellationTokenSource _stop;
        private int _port;

        public bool IsRunning { get { return _listener != null; } }
        public string ManagementUrl { get { return "http://127.0.0.1:" + _port + "/#" + _token; } }

        public LocalManagementServer(Func<DesktopSettings> getSettings, Action<DesktopSettings> setSettings,
            Func<string, int, HistoryItem[]> getHistory, Action<string> addExclusion, Action<string> removeExclusion,
            Action<ApiConnectionInput> saveApiConnection, Action<string> selectApiConnection,
            Action<string> deleteApiConnection, Action<long> deleteHistory, Action clearHistory)
        {
            _getSettings = getSettings;
            _setSettings = setSettings;
            _getHistory = getHistory;
            _addExclusion = addExclusion;
            _removeExclusion = removeExclusion;
            _saveApiConnection = saveApiConnection;
            _selectApiConnection = selectApiConnection;
            _deleteApiConnection = deleteApiConnection;
            _deleteHistory = deleteHistory;
            _clearHistory = clearHistory;
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            _token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public void Start()
        {
            if (_listener != null) return;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start(8);
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _stop = new CancellationTokenSource();
            _ = AcceptLoopAsync(_stop.Token);
        }

        private async Task AcceptLoopAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                _ = Task.Run(() => HandleClient(client), stop);
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    if (!IPAddress.IsLoopback(((IPEndPoint)client.Client.RemoteEndPoint).Address)) return;
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    var stream = client.GetStream();
                    var header = ReadHeader(stream);
                    if (header == null) { Respond(stream, 400, "text/plain", "Bad request"); return; }
                    var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var first = lines[0].Split(' ');
                    if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                    { Respond(stream, 400, "text/plain", "Bad request"); return; }
                    var method = first[0];
                    var path = first[1];
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 1; i < lines.Length; i++)
                    {
                        var colon = lines[i].IndexOf(':');
                        if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                    }
                    string host;
                    if (!headers.TryGetValue("Host", out host) ||
                        !string.Equals(host, "127.0.0.1:" + _port, StringComparison.OrdinalIgnoreCase))
                    { Respond(stream, 403, "text/plain", "Forbidden"); return; }
                    if (method == "GET" && (path == "/" || path == "/app.js" || path == "/style.css"))
                    {
                        var name = path == "/" ? "ManagementPage.html" :
                                   path == "/app.js" ? "ManagementPage.js" : "ManagementPage.css";
                        var mime = path == "/" ? "text/html" : path == "/app.js" ? "application/javascript" : "text/css";
                        Respond(stream, 200, mime, Resource(name));
                        return;
                    }
                    if (!path.StartsWith("/api/", StringComparison.Ordinal))
                    { Respond(stream, 404, "text/plain", "Not found"); return; }
                    string supplied;
                    if (!headers.TryGetValue("Authorization", out supplied) ||
                        !ConstantTimeEqual(supplied, "Bearer " + _token))
                    { Respond(stream, 401, "text/plain", "Unauthorized"); return; }
                    // A malicious site cannot obtain the token. Reject cross-origin requests too.
                    string origin;
                    if (headers.TryGetValue("Origin", out origin) &&
                        !string.Equals(origin, "http://127.0.0.1:" + _port, StringComparison.Ordinal))
                    { Respond(stream, 403, "text/plain", "Forbidden"); return; }
                    if (method == "GET" && path == "/api/settings")
                        RespondJson(stream, _getSettings());
                    else if (method == "GET" && (path == "/api/history" ||
                        path.StartsWith("/api/history?", StringComparison.Ordinal)))
                    {
                        var query = HttpUtility.ParseQueryString(new Uri("http://127.0.0.1" + path).Query);
                        int offset;
                        if (!int.TryParse(query["offset"] ?? "0", out offset) || offset < 0)
                            throw new ArgumentException("Invalid history offset");
                        var term = query["search"] ?? "";
                        if (term.Length > 200) throw new ArgumentException("Search term too long");
                        RespondJson(stream, _getHistory(term, offset));
                    }
                    else if (method == "POST")
                    {
                        if (!headers.TryGetValue("Origin", out origin) ||
                            !string.Equals(origin, "http://127.0.0.1:" + _port, StringComparison.Ordinal) ||
                            !headers.TryGetValue("Content-Type", out supplied) ||
                            !supplied.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                        { Respond(stream, 403, "text/plain", "Forbidden"); return; }
                        int length;
                        if (!headers.TryGetValue("Content-Length", out supplied) ||
                            !int.TryParse(supplied, out length) || length < 0 || length > 65536)
                        { Respond(stream, 413, "text/plain", "Invalid body length"); return; }
                        var body = new byte[length];
                        var received = 0;
                        while (received < length)
                        {
                            var count = stream.Read(body, received, length - received);
                            if (count == 0) throw new IOException("Unexpected end of request");
                            received += count;
                        }
                        var json = Encoding.UTF8.GetString(body);
                        var serializer = new JavaScriptSerializer { MaxJsonLength = 65536 };
                        if (path == "/api/settings")
                        {
                            var settings = serializer.Deserialize<DesktopSettings>(json);
                            if (settings == null || string.IsNullOrWhiteSpace(settings.TargetLanguage) ||
                                settings.TargetLanguage.Length > 50 ||
                                (settings.NotesDirectory != null && settings.NotesDirectory.Length > 2048))
                                throw new ArgumentException("Invalid settings");
                            _setSettings(settings);
                        }
                        else if (path == "/api/connection")
                        {
                            var input = serializer.Deserialize<ApiConnectionInput>(json);
                            Uri uri;
                            if (input == null || input.ApiKey == null || input.ApiKey.Length > 8192 ||
                                string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80 ||
                                input.Model == null || input.Model.Length > 200 ||
                                input.ApiBaseUrl == null || input.ApiBaseUrl.Length > 2048 ||
                                !Uri.TryCreate(input.ApiBaseUrl, UriKind.Absolute, out uri) ||
                                (uri.Scheme != Uri.UriSchemeHttps &&
                                 (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)))
                                throw new ArgumentException("Invalid connection");
                            _saveApiConnection(input);
                        }
                        else if (path == "/api/connection/select" || path == "/api/connection/delete")
                        {
                            var input = serializer.Deserialize<Dictionary<string, string>>(json);
                            string id;
                            if (input == null || !input.TryGetValue("id", out id) ||
                                string.IsNullOrWhiteSpace(id) || id.Length > 64)
                                throw new ArgumentException("Invalid connection id");
                            if (path.EndsWith("select", StringComparison.Ordinal)) _selectApiConnection(id);
                            else _deleteApiConnection(id);
                        }
                        else if (path == "/api/exclusions/add" || path == "/api/exclusions/remove")
                        {
                            var input = serializer.Deserialize<Dictionary<string, string>>(json);
                            string application;
                            if (input == null || !input.TryGetValue("application", out application) ||
                                string.IsNullOrWhiteSpace(application) || application.Length > 200 ||
                                application.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                                throw new ArgumentException("Invalid application");
                            if (path.EndsWith("add", StringComparison.Ordinal)) _addExclusion(application.Trim());
                            else _removeExclusion(application.Trim());
                        }
                        else if (path == "/api/history/delete")
                        {
                            var input = serializer.Deserialize<Dictionary<string, long>>(json);
                            long id;
                            if (input == null || !input.TryGetValue("id", out id) || id <= 0)
                                throw new ArgumentException("Invalid history id");
                            _deleteHistory(id);
                        }
                        else if (path == "/api/history/clear")
                        {
                            _clearHistory();
                        }
                        else { Respond(stream, 404, "text/plain", "Not found"); return; }
                        RespondJson(stream, new { ok = true });
                    }
                    else Respond(stream, 405, "text/plain", "Method not allowed");
                }
                catch (ArgumentException)
                {
                    try { Respond(client.GetStream(), 400, "text/plain", "输入值无效，请检查目录或程序路径。"); } catch { }
                }
                catch (Exception)
                {
                    try { Respond(client.GetStream(), 500, "text/plain", "保存失败，请检查目录或 Windows 启动项权限。"); }
                    catch { /* A disconnected client cannot stop the listener. */ }
                }
            }
        }

        private static string ReadHeader(Stream stream)
        {
            var bytes = new List<byte>();
            while (bytes.Count < 8192)
            {
                var value = stream.ReadByte();
                if (value < 0) return null;
                bytes.Add((byte)value);
                var n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 &&
                    bytes[n - 2] == 13 && bytes[n - 1] == 10)
                    return Encoding.ASCII.GetString(bytes.ToArray(), 0, n - 4);
            }
            return null;
        }

        private static string Resource(string name)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream("AiSelectionToolbar.Desktop." + name))
            {
                if (stream == null) throw new InvalidOperationException("Missing resource " + name);
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }

        private static bool ConstantTimeEqual(string a, string b)
        {
            if (a == null || b == null) return false;
            var diff = a.Length ^ b.Length;
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static void RespondJson(Stream stream, object value)
        {
            Respond(stream, 200, "application/json",
                new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(value));
        }

        private static void Respond(Stream stream, int code, string mime, string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var title = code == 200 ? "OK" : code == 400 ? "Bad Request" :
                        code == 401 ? "Unauthorized" : code == 403 ? "Forbidden" :
                        code == 404 ? "Not Found" : code == 405 ? "Method Not Allowed" : "Error";
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " " + title + "\r\n" +
                "Content-Type: " + mime + "; charset=utf-8\r\n" +
                "Content-Length: " + payload.Length + "\r\n" +
                "Cache-Control: no-store\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "X-Frame-Options: DENY\r\n" +
                "Referrer-Policy: no-referrer\r\n" +
                "Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'\r\n" +
                "Connection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(payload, 0, payload.Length);
        }

        public void Dispose()
        {
            if (_stop != null) { _stop.Cancel(); _stop.Dispose(); _stop = null; }
            if (_listener != null) { _listener.Stop(); _listener = null; }
        }
    }
}
