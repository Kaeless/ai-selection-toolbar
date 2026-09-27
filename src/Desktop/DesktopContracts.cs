using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using AiSelectionToolbar.Core;

namespace AiSelectionToolbar.Desktop
{
    // The host converts AiSelectionToolbar.Selection.SelectionSnapshot to this event.
    public sealed class DesktopSelectionEventArgs : EventArgs
    {
        public string Text { get; set; }
        public string SourceApplication { get; set; }
        public string SourceTitle { get; set; }
        public Rectangle Bounds { get; set; }
    }

    public interface IDesktopSelectionSource
    {
        event EventHandler<DesktopSelectionEventArgs> SelectionCaptured;
    }

    public sealed class ManualSelectionSource : IDesktopSelectionSource
    {
        public event EventHandler<DesktopSelectionEventArgs> SelectionCaptured;

        public void Publish(string text, string sourceApplication, string sourceTitle, Rectangle bounds)
        {
            var handler = SelectionCaptured;
            if (handler != null)
                handler(this, new DesktopSelectionEventArgs {
                    Text = text, SourceApplication = sourceApplication, SourceTitle = sourceTitle,
                    Bounds = bounds
                });
        }

        public void Publish(string text, string sourceApplication, string sourceTitle)
        {
            Publish(text, sourceApplication, sourceTitle, Rectangle.Empty);
        }
    }

    // Core implements this callback. Cancellation stops both upstream work and the UI stream.
    public interface IStreamingActionProvider
    {
        Task RunAsync(string action, string selection, string prompt,
            IProgress<string> chunks, CancellationToken cancellationToken);
    }

    public sealed class DemoStreamingActionProvider : IStreamingActionProvider
    {
        public async Task RunAsync(string action, string selection, string prompt,
            IProgress<string> chunks, CancellationToken cancellationToken)
        {
            string result;
            switch (action)
            {
                case "translate": result = "[演示翻译] " + selection; break;
                case "ask": result = "[演示回答] 问题：“" + prompt + "”；参考选区：“" + selection + "”。这里会逐段显示 Core 返回的内容。"; break;
                default: result = "[演示解释] " + selection + "：这里会逐段显示 Core 返回的解释。"; break;
            }
            foreach (var word in result.Split(new[] { ' ' }, StringSplitOptions.None))
            {
                cancellationToken.ThrowIfCancellationRequested();
                chunks.Report(word + " ");
                await Task.Delay(160, cancellationToken);
            }
        }
    }

    public sealed class DesktopSettings
    {
        public string Version { get; set; } = "0.5.1";
        public string Author { get; set; } = "Kaeless";
        public bool AutoShow { get; set; } = true;
        public bool StartOnLogin { get; set; }
        public string TargetLanguage { get; set; } = "中文";
        public string NotesDirectory { get; set; } = "";
        public string ApiBaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public string ActiveApiId { get; set; } = "";
        public List<ApiProfileSummary> ApiProfiles { get; set; } = new List<ApiProfileSummary>();
        public List<string> ExcludedApplications { get; set; } = new List<string>();
        public List<CustomActionDefinition> CustomActions { get; set; } = new List<CustomActionDefinition>();
        public string ToolbarStyle { get; set; } = "standard";
        public string ToolbarAccentColor { get; set; } = "#4F46E5";
        public string ToolbarBackgroundColor { get; set; } = "#18202E";
        public string ToolbarBorderColor { get; set; } = "#0B1020";
        public string AnswerBackgroundColor { get; set; } = "#F8FAFC";
        public string AnswerBorderColor { get; set; } = "#0B1020";
    }

    public sealed class ApiConnectionInput
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ApiBaseUrl { get; set; }
        public string Model { get; set; }
        public string ApiKey { get; set; }
        public bool MakeActive { get; set; }
    }

    public sealed class ApiProfileSummary
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ApiBaseUrl { get; set; }
        public string Model { get; set; }
        public bool HasApiKey { get; set; }
    }

    public sealed class HistoryItem
    {
        public long Id { get; set; }
        public DateTime TimeUtc { get; set; }
        public string Action { get; set; }
        public string Selection { get; set; }
        public string Result { get; set; }
        public string Application { get; set; }
        public string SourceTitle { get; set; }
        public string Prompt { get; set; }
    }
}
