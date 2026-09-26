using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiSelectionToolbar.Core;
using Gtk;

namespace AiSelectionToolbar.Linux;

/// <summary>The GTK shell. The host owns secrets, history and network requests.</summary>
public sealed class LinuxWindow : IDisposable
{
    private readonly LinuxHost host;
    private readonly LinuxSelectionService selection;
    private readonly Window management;
    private readonly Window toolbar;
    private readonly Window answer;
    private readonly StatusIcon tray;
    private readonly Box toolbarButtons;
    private readonly TextView answerText;
    private readonly Entry endpoint = new();
    private readonly Entry model = new();
    private readonly Entry secret = new() { Visibility = false, PlaceholderText = "留空以保留当前地址的密钥" };
    private readonly Entry language = new();
    private readonly Entry accent = new();
    private readonly Entry excluded = new();
    private readonly Entry historySearch = new();
    private readonly CheckButton autoShow = new("选中文字后自动显示工具栏");
    private readonly ComboBoxText style = new();
    private readonly Box customRows = new(Orientation.Vertical, 8);
    private readonly Box historyRows = new(Orientation.Vertical, 8);
    private readonly List<(Entry name, TextView prompt, string id)> customEditors = new();
    private AppSettings current;
    private string selectedText = "";
    private string selectedApp = "";
    private string selectedTitle = "";
    private string renderedAnswer = "";
    private int popupX;
    private int popupY;
    private CancellationTokenSource? activeRequest;
    private bool disposed;

    public LinuxWindow(LinuxHost host, LinuxSelectionService selection)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.selection = selection ?? throw new ArgumentNullException(nameof(selection));
        current = host.LoadSettings();

        management = new Window("AI 划词助手") { DefaultWidth = 760, DefaultHeight = 620 };
        management.SetPosition(WindowPosition.Center);
        management.DeleteEvent += (_, e) => { management.Hide(); e.RetVal = true; };
        management.Add(BuildManagement());

        toolbar = FloatingWindow();
        toolbarButtons = new Box(Orientation.Horizontal, 4);
        var toolbarFrame = new Frame { ShadowType = ShadowType.Out };
        toolbarFrame.Add(toolbarButtons);
        toolbar.Add(toolbarFrame);
        RebuildToolbar();

        answer = FloatingWindow();
        answer.SetDefaultSize(440, 340);
        var answerBox = new Box(Orientation.Vertical, 4) { BorderWidth = 10 };
        var top = new Box(Orientation.Horizontal, 4);
        var close = new Button("×") { Relief = ReliefStyle.None };
        close.Clicked += (_, _) => { activeRequest?.Cancel(); answer.Hide(); };
        top.PackStart(new Label(""), true, true, 0);
        top.PackEnd(close, false, false, 0);
        answerBox.PackStart(top, false, false, 0);
        answerText = new TextView { Editable = false, WrapMode = WrapMode.WordChar, CursorVisible = false,
            LeftMargin = 12, RightMargin = 12, TopMargin = 8, BottomMargin = 8 };
        CreateMarkdownTags(answerText.Buffer);
        var scroll = new ScrolledWindow();
        scroll.Add(answerText);
        answerBox.PackStart(scroll, true, true, 0);
        answer.Add(answerBox);
        answer.DeleteEvent += (_, e) => { activeRequest?.Cancel(); answer.Hide(); e.RetVal = true; };

        tray = new StatusIcon { IconName = "accessories-text-editor", TooltipText = "AI 划词助手", Visible = true };
        tray.Activate += (_, _) => ShowManagement();
        tray.PopupMenu += (_, args) =>
        {
            var menu = new Menu();
            var open = new MenuItem("打开设置与历史");
            open.Activated += (_, _) => ShowManagement();
            menu.Append(open);
            var quit = new MenuItem("退出");
            quit.Activated += (_, _) => Quit();
            menu.Append(quit);
            menu.ShowAll();
            menu.Popup();
        };
        selection.SelectionCaptured += OnSelectionCaptured;
    }

    private static Window FloatingWindow()
    {
        var window = new Window(WindowType.Toplevel) {
            Decorated = false, Resizable = false, KeepAbove = true, SkipTaskbarHint = true,
            SkipPagerHint = true, TypeHint = Gdk.WindowTypeHint.Utility
        };
        window.DeleteEvent += (_, e) => { window.Hide(); e.RetVal = true; };
        return window;
    }

    private Widget BuildManagement()
    {
        var tabs = new Notebook { BorderWidth = 14 };
        var settingsPage = new Box(Orientation.Vertical, 14) { BorderWidth = 16 };
        settingsPage.PackStart(Heading("连接设置"), false, false, 0);
        settingsPage.PackStart(Field("API 地址", endpoint), false, false, 0);
        settingsPage.PackStart(Field("模型", model), false, false, 0);
        settingsPage.PackStart(Field("API 密钥", secret), false, false, 0);
        settingsPage.PackStart(Heading("划词工具栏"), false, false, 0);
        settingsPage.PackStart(autoShow, false, false, 0);
        style.Append("standard", "标准");
        style.Append("compact", "紧凑");
        settingsPage.PackStart(Field("样式", style), false, false, 0);
        settingsPage.PackStart(Field("颜色 (#RRGGBB)", accent), false, false, 0);
        settingsPage.PackStart(Field("翻译目标语言", language), false, false, 0);
        settingsPage.PackStart(Heading("排除的程序"), false, false, 0);
        settingsPage.PackStart(new Label("每行填写一个程序名。也可以点工具栏右侧的图标排除当前程序。") { Xalign = 0 }, false, false, 0);
        settingsPage.PackStart(Field("程序名", excluded), false, false, 0);
        settingsPage.PackStart(Heading("自定义按钮（最多 8 个）"), false, false, 0);
        settingsPage.PackStart(customRows, false, false, 0);
        var addAction = new Button("添加按钮");
        addAction.Clicked += (_, _) => { if (customEditors.Count < 8) AddCustomRow("", "", ""); };
        settingsPage.PackStart(addAction, false, false, 0);
        var save = new Button("保存设置");
        save.Clicked += (_, _) => SaveSettings();
        settingsPage.PackStart(save, false, false, 0);
        var settingsScroll = new ScrolledWindow();
        settingsScroll.AddWithViewport(settingsPage);
        tabs.AppendPage(settingsScroll, new Label("设置"));

        var historyPage = new Box(Orientation.Vertical, 12) { BorderWidth = 16 };
        historyPage.PackStart(Heading("历史记录"), false, false, 0);
        historySearch.PlaceholderText = "搜索划词、回答或来源";
        historySearch.Changed += (_, _) => RefreshHistory();
        historyPage.PackStart(historySearch, false, false, 0);
        var historyScroll = new ScrolledWindow();
        historyScroll.AddWithViewport(historyRows);
        historyPage.PackStart(historyScroll, true, true, 0);
        tabs.AppendPage(historyPage, new Label("历史"));
        FillSettings();
        RefreshHistory();
        return tabs;
    }

    private static Label Heading(string text) => new(text) { Xalign = 0, Yalign = 0.5f };

    private static Widget Field(string title, Widget input)
    {
        var row = new Box(Orientation.Horizontal, 12);
        var label = new Label(title) { Xalign = 0, WidthRequest = 140 };
        row.PackStart(label, false, false, 0);
        row.PackStart(input, true, true, 0);
        return row;
    }

    private void FillSettings()
    {
        endpoint.Text = current.BaseUrl ?? "";
        model.Text = current.Model ?? "";
        secret.Text = "";
        language.Text = current.TranslationTargetLanguage ?? "中文";
        accent.Text = current.ToolbarAccentColor ?? "#4F46E5";
        style.ActiveId = current.ToolbarStyle == "compact" ? "compact" : "standard";
        autoShow.Active = current.AutoShow;
        excluded.Text = string.Join(", ", current.ExcludedApplications ?? new List<string>());
        foreach (var child in customRows.Children) customRows.Remove(child);
        customEditors.Clear();
        foreach (var item in current.CustomActions ?? new List<CustomActionDefinition>())
            AddCustomRow(item.Name ?? "", item.Prompt ?? "", item.Id ?? "");
    }

    private void AddCustomRow(string name, string prompt, string id)
    {
        var row = new Box(Orientation.Vertical, 4);
        var nameInput = new Entry { PlaceholderText = "按钮名称（如：详细说明）", Text = name };
        var promptInput = new TextView { WrapMode = WrapMode.WordChar };
        promptInput.Buffer.Text = prompt;
        var holder = new ScrolledWindow { HeightRequest = 64 };
        holder.Add(promptInput);
        row.PackStart(nameInput, false, false, 0);
        row.PackStart(holder, false, false, 0);
        customRows.PackStart(row, false, false, 0);
        customEditors.Add((nameInput, promptInput, id));
        row.ShowAll();
    }

    private void SaveSettings()
    {
        try
        {
            var next = host.LoadSettings();
            var oldEndpoint = (next.BaseUrl ?? "").TrimEnd('/');
            var newEndpoint = endpoint.Text.Trim().TrimEnd('/');
            if (oldEndpoint != newEndpoint && string.IsNullOrWhiteSpace(secret.Text)) next.ProtectedApiKey = null;
            next.BaseUrl = newEndpoint;
            next.Model = model.Text.Trim();
            next.TranslationTargetLanguage = language.Text.Trim();
            next.AutoShow = autoShow.Active;
            next.ToolbarStyle = style.ActiveId ?? "standard";
            next.ToolbarAccentColor = accent.Text.Trim();
            if (!Regex.IsMatch(next.ToolbarAccentColor, "^#[0-9a-fA-F]{6}$"))
                throw new ArgumentException("工具栏颜色应为 #RRGGBB 格式。 ");
            next.ExcludedApplications = excluded.Text.Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            next.CustomActions = customEditors
                .Where(row => !string.IsNullOrWhiteSpace(row.name.Text) || !string.IsNullOrWhiteSpace(row.prompt.Buffer.Text))
                .Select(row => new CustomActionDefinition { Id = string.IsNullOrWhiteSpace(row.id) ? Guid.NewGuid().ToString("N") : row.id,
                    Name = row.name.Text.Trim(), Prompt = row.prompt.Buffer.Text.Trim() }).ToList();
            if (next.CustomActions.Any(a => a.Name.Length == 0 || a.Prompt.Length == 0 || a.Name.Length > 20 || a.Prompt.Length > 4000))
                throw new ArgumentException("每个自定义按钮都需要名称和提示词（名称最多 20 字，提示词最多 4000 字）。");
            host.SaveSettings(next, secret.Text);
            current = host.LoadSettings();
            selection.UpdateExcludedApplications(current.ExcludedApplications);
            FillSettings();
            RebuildToolbar();
            Message("设置已保存。", MessageType.Info);
        }
        catch (Exception ex) { Message(ex.Message, MessageType.Error); }
    }

    private void RefreshHistory()
    {
        foreach (var child in historyRows.Children) historyRows.Remove(child);
        try
        {
            foreach (var item in host.SearchHistory(historySearch.Text))
            {
                var row = new Box(Orientation.Vertical, 2);
                row.PackStart(new Label($"{item.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {item.Action} · {item.SourceApplication}") { Xalign = 0 }, false, false, 0);
                row.PackStart(new Label(Shorten(item.SelectedText, 160)) { Xalign = 0, LineWrap = true }, false, false, 0);
                var open = new Button("查看回答") { Halign = Align.Start };
                open.Clicked += (_, _) => { renderedAnswer = item.Response ?? ""; RenderMarkdown(); answer.ShowAll(); answer.Present(); };
                row.PackStart(open, false, false, 0);
                historyRows.PackStart(row, false, false, 0);
            }
            historyRows.ShowAll();
        }
        catch (Exception ex) { Message(ex.Message, MessageType.Error); }
    }

    private static string Shorten(string? s, int length) => s == null ? "" : s.Length <= length ? s : s[..length] + "…";

    private void RebuildToolbar()
    {
        foreach (var child in toolbarButtons.Children) toolbarButtons.Remove(child);
        ActionButton("了解", "explain");
        ActionButton("详细解释", "explain_detailed");
        ActionButton("翻译", "translate");
        ActionButton("提问", "ask");
        foreach (var action in current.CustomActions ?? new List<CustomActionDefinition>())
            if (!string.IsNullOrWhiteSpace(action.Name) && !string.IsNullOrWhiteSpace(action.Id))
                ActionButton(action.Name, "custom:" + action.Id);
        var exclude = new Button("⊘") { Relief = ReliefStyle.None, TooltipText = "在此程序隐藏" };
        exclude.Clicked += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(selectedApp))
            {
                try { host.ExcludeApplication(selectedApp); current = host.LoadSettings();
                    selection.UpdateExcludedApplications(current.ExcludedApplications); FillSettings(); }
                catch (Exception ex) { Message(ex.Message, MessageType.Error); }
            }
            toolbar.Hide();
        };
        toolbarButtons.PackStart(exclude, false, false, 0);
        toolbarButtons.ShowAll();
    }

    private void ActionButton(string title, string action)
    {
        var button = new Button(title) { Relief = ReliefStyle.None };
        button.Clicked += async (_, _) => await ExecuteAction(action);
        toolbarButtons.PackStart(button, false, false, 0);
    }

    private void OnSelectionCaptured(object? sender, LinuxSelectionEventArgs e)
    {
        GLib.Idle.Add(() =>
        {
            if (disposed) return false;
            ShowSelection(e.Text, e.SourceApplication, e.SourceTitle,
                new Rectangle(e.X, e.Y, 0, 0));
            return false;
        });
    }

    public void ShowSelection(string text, string application, string title, Rectangle bounds)
    {
        if (string.IsNullOrWhiteSpace(text) || !current.AutoShow) return;
        if ((current.ExcludedApplications ?? new List<string>()).Any(s =>
            string.Equals(s, application, StringComparison.OrdinalIgnoreCase))) return;
        selectedText = text;
        selectedApp = application ?? "";
        selectedTitle = title ?? "";
        popupX = Math.Max(0, bounds.Right);
        popupY = Math.Max(0, bounds.Bottom + 8);
        answer.Hide();
        toolbar.ShowAll();
        toolbar.Move(popupX, popupY);
        toolbar.Present();
    }

    private async Task ExecuteAction(string action)
    {
        if (string.IsNullOrWhiteSpace(selectedText)) return;
        var prompt = "";
        if (action == "ask")
        {
            var dialog = new Dialog("针对选中文字提问", management, DialogFlags.Modal);
            var input = new Entry { PlaceholderText = "输入你的问题" };
            dialog.ContentArea.PackStart(input, false, false, 12);
            dialog.AddButton("取消", ResponseType.Cancel);
            dialog.AddButton("发送", ResponseType.Ok);
            dialog.ShowAll();
            var accepted = (ResponseType)dialog.Run() == ResponseType.Ok;
            prompt = input.Text.Trim();
            dialog.Destroy();
            if (!accepted || prompt.Length == 0) return;
        }
        activeRequest?.Cancel();
        activeRequest?.Dispose();
        var request = new CancellationTokenSource();
        activeRequest = request;
        var selectionText = selectedText;
        var selectionApp = selectedApp;
        var selectionTitle = selectedTitle;
        renderedAnswer = "";
        RenderMarkdown();
        answer.Move(popupX, popupY + 48);
        answer.ShowAll();
        toolbar.Hide();
        var response = new StringBuilder();
        try
        {
            var chunks = new ImmediateProgress<string>(chunk =>
            {
                if (request != activeRequest || chunk == null) return;
                lock (response) response.Append(chunk);
                GLib.Idle.Add(() =>
                {
                    if (request != activeRequest || request.IsCancellationRequested || disposed) return false;
                    lock (response) renderedAnswer = response.ToString();
                    RenderMarkdown();
                    return false;
                });
            });
            await host.RunActionAsync(action, selectionText, prompt, chunks, request.Token).ConfigureAwait(false);
            GLib.Idle.Add(() =>
            {
                if (request != activeRequest || request.IsCancellationRequested || disposed) return false;
                lock (response) renderedAnswer = response.ToString();
                RenderMarkdown();
                try
                {
                    host.SaveHistory(new HistoryEntry {
                        CreatedUtc = DateTime.UtcNow, Action = action, Prompt = prompt,
                        SelectedText = selectionText, Response = renderedAnswer,
                        SourceApplication = selectionApp, SourceTitle = selectionTitle,
                        Source = selectionTitle.Length > 0 ? selectionTitle : selectionApp
                    });
                    RefreshHistory();
                }
                catch (Exception ex) { Message(ex.Message, MessageType.Error); }
                return false;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GLib.Idle.Add(() =>
            {
                if (request == activeRequest && !disposed)
                { renderedAnswer = "请求失败：" + ex.Message; RenderMarkdown(); }
                return false;
            });
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static void CreateMarkdownTags(TextBuffer buffer)
    {
        buffer.TagTable.Add(new TextTag("heading") { Weight = Pango.Weight.Bold, Scale = 1.3 });
        buffer.TagTable.Add(new TextTag("bold") { Weight = Pango.Weight.Bold });
        buffer.TagTable.Add(new TextTag("code") { Family = "monospace" });
        buffer.TagTable.Add(new TextTag("quote") { Style = Pango.Style.Italic });
    }

    private void RenderMarkdown()
    {
        var buffer = answerText.Buffer;
        buffer.Text = "";
        var fenced = false;
        foreach (var line in renderedAnswer.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            var value = line;
            string? tag = fenced ? "code" : null;
            if (!fenced)
            {
                var heading = Regex.Match(value, @"^\s*#{1,6}\s+(.+)$");
                if (heading.Success) { value = heading.Groups[1].Value; tag = "heading"; }
                else if (value.StartsWith("> ")) { value = value[2..]; tag = "quote"; }
                else if (Regex.IsMatch(value, @"^\s*[-*+]\s+")) value = Regex.Replace(value, @"^\s*[-*+]\s+", "• ");
                else if (Regex.IsMatch(value, @"^\s*\d+\.\s+")) { }
            }
            // Links are displayed as readable text and URL; GTK never interprets HTML from the model.
            value = Regex.Replace(value, @"\[([^\]]+)\]\((https?://[^\s)]+)\)", "$1 ($2)");
            if (tag != null) InsertTagged(buffer, value + "\n", tag);
            else AppendInline(buffer, value + "\n");
        }
    }

    private static void AppendInline(TextBuffer buffer, string text)
    {
        var parts = Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)");
        foreach (var part in parts)
        {
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                InsertTagged(buffer, part[2..^2], "bold");
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
                InsertTagged(buffer, part[1..^1], "code");
            else { var end = buffer.EndIter; buffer.Insert(ref end, part); }
        }
    }

    private static void InsertTagged(TextBuffer buffer, string text, string tag)
    {
        var end = buffer.EndIter;
        buffer.InsertWithTagsByName(ref end, text, tag);
    }

    private void Message(string text, MessageType type)
    {
        using var dialog = new MessageDialog(management, DialogFlags.Modal, type, ButtonsType.Ok, text);
        dialog.Run();
    }

    public void ShowManagement()
    {
        management.ShowAll();
        management.Present();
    }

    public void Quit()
    {
        Dispose();
        Application.Quit();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        selection.SelectionCaptured -= OnSelectionCaptured;
        activeRequest?.Cancel();
        activeRequest?.Dispose();
        tray.Dispose();
        toolbar.Destroy();
        answer.Destroy();
        management.Destroy();
    }
}
