using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiSelectionToolbar.Core;
using AiSelectionToolbar.Desktop;

// Full Windows host and WPF window. Only model output is replaced; settings,
// DPAPI, SQLite, management API, embedded resources and UI are production code.
internal static class WindowsUiSmoke
{
    static string output;
    static readonly List<object> results = new List<object>();
    static void Check(string id, string name, bool ok)
    {
        results.Add(new { id, name, status = ok ? "PASS" : "FAIL" });
        File.WriteAllText(Path.Combine(output, "desktop-results.json"), new JavaScriptSerializer().Serialize(results));
        if (!ok) throw new Exception(id + ": " + name);
    }
    static T Control<T>(MainWindow window, string name) where T : FrameworkElement
    { return (T)window.FindName(name); }
    static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button) yield return (Button)child;
            foreach (var button in Buttons(child)) yield return button;
        }
    }
    static void Click(MainWindow window, string tag)
    { Buttons(window).First(b => (string)b.Tag == tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    static string Answer(MainWindow window)
    { return new TextRange(Control<RichTextBox>(window, "ResultView").Document.ContentStart,
        Control<RichTextBox>(window, "ResultView").Document.ContentEnd).Text; }
    static async Task Complete(MainWindow window)
    {
        for (int i = 0; i < 100; i++)
        {
            if (Control<TextBlock>(window, "StatusLabel").Text == "完成") return;
            await Task.Delay(100);
        }
        throw new Exception("Answer did not complete within 10 seconds.");
    }
    static void Screenshot(MainWindow window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, name + ".png"))) png.Save(file);
    }
    sealed class FixtureModel : IStreamingActionProvider
    {
        public string Selection, Prompt;
        public async Task RunAsync(string action, string selection, string prompt,
            IProgress<string> chunks, CancellationToken token)
        {
            Selection = selection; Prompt = prompt;
            chunks.Report("# TCP 滑动窗口\n\n这是逐段显示的测试回答。\n");
            await Task.Delay(800, token);
            chunks.Report("\n- 窗口控制在途数据量\n- ACK 推进发送边界\n\n```c\nwindow = ack + advertised_window;\n```\n");
            await Task.Delay(150, token);
        }
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new Exception("Run on an ephemeral GitHub Windows runner to isolate user data.");
        output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var store = new SettingsStore(SettingsStore.DefaultPath);
        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:12345", Model = "fixture-model",
            NotesDirectory = Path.Combine(output, "notes"), ExcludedApplications = new List<string> { "excluded.exe" } };
        const string key = "TEST-ONLY-NOT-A-REAL-KEY";
        store.SetApiKey(settings, key); store.Save(settings);
        Check("D01", "DPAPI encrypted settings round trip", store.ReadApiKey(store.Load()) == key &&
            !File.ReadAllText(SettingsStore.DefaultPath).Contains(key));
        using (var history = new HistoryStore(HistoryStore.DefaultPath))
        {
            history.Clear();
            history.Add(new HistoryEntry { SelectedText = "TCP 滑动窗口", Action = "explain",
                SourceApplication = "fixture.exe", Response = "# 标题\n**测试内容**\n<script>window.xss=1</script>" });
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow(); app.MainWindow = window;
        var type = typeof(MainWindow).Assembly.GetType("AiSelectionToolbar.Desktop.IntegratedHost");
        var host = Activator.CreateInstance(type);
        type.GetMethod("Connect").Invoke(host, new object[] { window });
        var model = new FixtureModel(); window.ActionProvider = model;
        window.Loaded += async (sender, e) => {
            try
            {
                Check("D02", "Executable version 0.5.3.0", typeof(MainWindow).Assembly.GetName().Version.ToString() == "0.5.3.0");
                window.Hide(); window.ShowSelection("secret", "excluded.exe", "excluded");
                Check("D03", "Excluded application suppresses toolbar", !window.IsVisible);
                window.ShowSelection("TCP 滑动窗口", "fixture.exe", "test source");
                await Task.Delay(150);
                Check("D04", "Captured selection displays compact toolbar", window.IsVisible &&
                    Control<Border>(window, "QuickPanel").Visibility == Visibility.Visible);
                Screenshot(window, "desktop-toolbar");
                Click(window, "explain_detailed"); await Task.Delay(300);
                Check("D05", "First stream chunk renders before completion", Answer(window).Contains("逐段显示") &&
                    Control<TextBlock>(window, "StatusLabel").Text != "完成");
                Screenshot(window, "desktop-streaming"); await Complete(window);
                Check("D06", "Markdown answer and followup are displayed", Answer(window).Contains("window = ack") &&
                    Control<StackPanel>(window, "FollowupPanel").Visibility == Visibility.Visible);
                Check("D07", "Answer fits current Windows work area", window.Width <= SystemParameters.WorkArea.Width &&
                    window.Height <= SystemParameters.WorkArea.Height);
                Screenshot(window, "desktop-answer");
                Control<TextBox>(window, "FollowupInput").Text = "为什么需要 ACK？";
                Click(window, "followup"); await Complete(window);
                Check("D08", "Followup preserves selection and includes previous answer", model.Selection == "TCP 滑动窗口" &&
                    model.Prompt.Contains("已有回答") && model.Prompt.Contains("为什么需要 ACK"));
                Screenshot(window, "desktop-followup");
                Control<Button>(window, "PinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.ShowSelection("replace", "fixture.exe", "new source");
                Check("D09", "Pinned answer is not replaced by another selection", Control<Border>(window, "ExpandedPanel").Visibility == Visibility.Visible);
                var server = typeof(MainWindow).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                var url = (string)server.GetType().GetProperty("ManagementUrl").GetValue(server);
                File.WriteAllText(Path.Combine(output, "management-url.txt"), url);
                var deadline = DateTime.UtcNow.AddMinutes(3);
                while (!File.Exists(Path.Combine(output, "browser-done")))
                {
                    if (DateTime.UtcNow > deadline) throw new Exception("Browser test timeout.");
                    await Task.Delay(250);
                }
                window.Close(); ((IDisposable)host).Dispose(); app.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "desktop-error.txt"), ex.ToString());
                ((IDisposable)host).Dispose(); app.Shutdown(1);
            }
        };
        window.Show(); return app.Run();
    }
}
