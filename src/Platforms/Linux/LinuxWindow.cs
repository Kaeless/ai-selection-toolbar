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
    private readonly LinuxManagementServer managementServer;
    private readonly Window toolbar;
    private readonly Window answer;
    private readonly StatusIcon tray;
    private readonly Box toolbarButtons;
    private readonly TextView answerText;
    private readonly Button noteButton = new("做笔记") { Sensitive = false };
    private AppSettings current;
    private string selectedText = "";
    private string selectedApp = "";
    private string selectedTitle = "";
    private string renderedAnswer = "";
    private int popupX;
    private int popupY;
    private CancellationTokenSource? activeRequest;
    private bool disposed;

    internal LinuxWindow(LinuxHost host, LinuxSelectionService selection, LinuxManagementServer managementServer)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.selection = selection ?? throw new ArgumentNullException(nameof(selection));
        this.managementServer = managementServer ?? throw new ArgumentNullException(nameof(managementServer));
        current = host.LoadSettings();

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
        noteButton.Clicked += (_, _) => SaveNote();
        top.PackStart(new Label(""), true, true, 0);
        top.PackEnd(noteButton, false, false, 0);
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
        managementServer.SettingsChanged += OnManagementSettingsChanged;
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
                try
                {
                    host.ExcludeApplication(selectedApp);
                    current = host.LoadSettings();
                    selection.UpdateExcludedApplications(current.ExcludedApplications);
                    RebuildToolbar();
                }
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
            var dialog = new Dialog("针对选中文字提问", answer, DialogFlags.Modal);
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
        noteButton.Sensitive = false;
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
                noteButton.Sensitive = (action == "explain" || action == "explain_detailed") &&
                    !string.IsNullOrWhiteSpace(renderedAnswer);
                try
                {
                    host.SaveHistory(new HistoryEntry {
                        CreatedUtc = DateTime.UtcNow, Action = action, Prompt = prompt,
                        SelectedText = selectionText, Response = renderedAnswer,
                        SourceApplication = selectionApp, SourceTitle = selectionTitle,
                        Source = selectionTitle.Length > 0 ? selectionTitle : selectionApp
                    });
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

    private void SaveNote()
    {
        if (string.IsNullOrWhiteSpace(selectedText) || !noteButton.Sensitive) return;
        using var dialog = new Dialog("编辑学习笔记", answer, DialogFlags.Modal);
        var editor = new TextView { WrapMode = WrapMode.WordChar };
        editor.Buffer.Text = renderedAnswer;
        var scroll = new ScrolledWindow { WidthRequest = 520, HeightRequest = 350 };
        scroll.Add(editor);
        dialog.ContentArea.PackStart(scroll, true, true, 12);
        dialog.AddButton("取消", ResponseType.Cancel);
        dialog.AddButton("保存", ResponseType.Ok);
        dialog.ShowAll();
        if ((ResponseType)dialog.Run() != ResponseType.Ok) return;
        try
        {
            var path = host.SaveNote(selectedText, editor.Buffer.Text.Trim(), selectedApp, selectedTitle);
            noteButton.Sensitive = false;
            Message("笔记已保存到：" + path, MessageType.Info);
        }
        catch (Exception ex) { Message(ex.Message, MessageType.Error); }
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
        using var dialog = new MessageDialog(answer, DialogFlags.Modal, type, ButtonsType.Ok, text);
        dialog.Run();
    }

    public void ShowManagement()
    {
        try { managementServer.OpenInBrowser(); }
        catch (Exception ex) { Message("无法打开浏览器管理页面：" + ex.Message, MessageType.Error); }
    }

    private void OnManagementSettingsChanged()
    {
        GLib.Idle.Add(() =>
        {
            if (disposed) return false;
            current = host.LoadSettings();
            selection.UpdateExcludedApplications(current.ExcludedApplications);
            RebuildToolbar();
            return false;
        });
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
        managementServer.SettingsChanged -= OnManagementSettingsChanged;
        activeRequest?.Cancel();
        activeRequest?.Dispose();
        tray.Dispose();
        toolbar.Destroy();
        answer.Destroy();
    }
}
