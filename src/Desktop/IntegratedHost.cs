using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiSelectionToolbar.Core;
using AiSelectionToolbar.Selection;

namespace AiSelectionToolbar.Desktop
{
    internal sealed class IntegratedHost : IStreamingActionProvider, IDesktopSelectionSource, IDisposable
    {
        private readonly object gate = new object();
        private readonly SettingsStore settingsStore = new SettingsStore(SettingsStore.DefaultPath);
        private readonly HistoryStore history = new HistoryStore(HistoryStore.DefaultPath);
        private readonly MarkdownNoteStore notes = new MarkdownNoteStore(MarkdownNoteStore.DefaultDirectory);
        private readonly ChatCompletionClient chat = new ChatCompletionClient();
        private SelectionTrigger trigger;
        private AppSettings settings;

        public event EventHandler<DesktopSelectionEventArgs> SelectionCaptured;
        public bool HotkeyAvailable { get { return trigger != null && trigger.HotkeyAvailable; } }
        public bool IsConfigured { get { lock (gate) return settings.IsConfigured; } }

        public IntegratedHost()
        {
            settings = settingsStore.Load() ?? new AppSettings();
        }

        public void Connect(MainWindow window)
        {
            window.ActionProvider = this;
            window.AttachSelectionSource(this);
            window.LoadSettings = GetDesktopSettings;
            window.SaveSettings = SaveDesktopSettings;
            window.SaveApiConnection = SaveConnection;
            window.SaveHistoryItem = SaveHistory;
            window.LoadHistoryItems = (term, offset) =>
                history.Search(term, 50, offset).Select(ToDesktopHistory).ToArray();
            window.DeleteHistoryItem = id => history.Delete(id);
            window.ClearHistoryItems = () => history.Clear();
            window.IsDuplicateNote = (text, edited) => history.HasNote(text, edited);
            window.SaveNote = SaveNote;
            trigger = new SelectionTrigger();
            trigger.SelectionRequested += OnSelectionRequested;
        }

        private void OnSelectionRequested(object sender, SelectionRequestedEventArgs args)
        {
            try
            {
                List<string> excluded;
                lock (gate) excluded = new List<string>(settings.ExcludedApplications ?? new List<string>());
                var snapshot = new SelectionReader(excluded).TryRead();
                if (snapshot == null) return;
                if (args.Kind == SelectionTriggerKind.MouseReleased)
                {
                    if (!args.HasMousePosition || snapshot.Bounds.IsEmpty) return;
                    var nearSelection = snapshot.Bounds;
                    nearSelection.Inflate(40, 40);
                    // Both ends of the drag must belong to the selected text. Checking only
                    // mouse-up can reopen a stale selection after an unrelated drag nearby.
                    if (!nearSelection.Contains(args.MouseDownX, args.MouseDownY) ||
                        !nearSelection.Contains(args.MouseX, args.MouseY)) return;
                }
                var handler = SelectionCaptured;
                if (handler != null)
                    handler(this, new DesktopSelectionEventArgs {
                        Text = snapshot.Text, SourceApplication = snapshot.SourceApplication,
                        SourceTitle = snapshot.SourceTitle, Bounds = snapshot.Bounds
                    });
            }
            catch (Exception)
            {
                // A misbehaving UI Automation provider cannot terminate the process.
            }
        }

        public async Task RunAsync(string action, string selection, string prompt,
            IProgress<string> chunks, CancellationToken cancellationToken)
        {
            AppSettings current;
            string apiKey;
            lock (gate)
            {
                current = Clone(settings);
                if (!current.IsConfigured)
                    throw new InvalidOperationException("请先在本机管理页配置 API 地址和模型。");
                apiKey = settingsStore.ReadApiKey(current);
            }

            string instruction;
            switch (action)
            {
                case "explain": instruction = "请用简体中文解释用户提供的文字，准确且简洁。"; break;
                case "translate": instruction = "将用户提供的文字翻译为" +
                    (current.TranslationTargetLanguage ?? "简体中文") + "，只给出译文。"; break;
                case "ask":
                    if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("问题不能为空。", nameof(prompt));
                    instruction = "请用简体中文回答用户针对所选文字提出的问题。"; break;
                default: throw new ArgumentException("不支持的操作。", nameof(action));
            }
            var text = action == "ask" ? "选中文字：\n" + selection + "\n\n问题：\n" + prompt : selection;
            await chat.StreamAsync(current, apiKey, new[] {
                new ChatMessage("system", instruction), new ChatMessage("user", text)
            }, delta => { if (!string.IsNullOrEmpty(delta.Text)) chunks.Report(delta.Text); return Task.CompletedTask; },
                cancellationToken).ConfigureAwait(false);
        }

        private DesktopSettings GetDesktopSettings()
        {
            lock (gate) return new DesktopSettings {
                AutoShow = settings.AutoShow, TargetLanguage = settings.TranslationTargetLanguage,
                ApiBaseUrl = settings.BaseUrl, Model = settings.Model,
                ExcludedApplications = new List<string>(settings.ExcludedApplications ?? new List<string>())
            };
        }

        private void SaveDesktopSettings(DesktopSettings value)
        {
            lock (gate)
            {
                settings.AutoShow = value.AutoShow;
                settings.TranslationTargetLanguage = value.TargetLanguage;
                settings.ExcludedApplications = new List<string>(value.ExcludedApplications ?? new List<string>());
                settingsStore.Save(settings);
            }
        }

        private void SaveConnection(ApiConnectionInput input)
        {
            lock (gate)
            {
                var endpointChanged = !string.Equals(
                    (settings.BaseUrl ?? "").TrimEnd('/'),
                    (input.ApiBaseUrl ?? "").TrimEnd('/'),
                    StringComparison.Ordinal);
                // A blank key only preserves the previous secret for the same endpoint.
                // Never send a cloud provider's key to a newly configured service.
                if (endpointChanged && string.IsNullOrEmpty(input.ApiKey))
                    settingsStore.SetApiKey(settings, null);
                settings.BaseUrl = input.ApiBaseUrl;
                settings.Model = input.Model;
                if (!string.IsNullOrEmpty(input.ApiKey)) settingsStore.SetApiKey(settings, input.ApiKey);
                settingsStore.Save(settings);
            }
        }

        private void SaveHistory(HistoryItem item)
        {
            history.Add(new HistoryEntry {
                CreatedUtc = item.TimeUtc, Action = item.Action, Prompt = item.Prompt,
                SelectedText = item.Selection, Response = item.Result,
                SourceApplication = item.Application, SourceTitle = item.SourceTitle,
                Source = item.SourceTitle ?? item.Application
            });
        }

        private static HistoryItem ToDesktopHistory(HistoryEntry entry) => new HistoryItem {
            Id = entry.Id,
            TimeUtc = entry.CreatedUtc, Action = entry.Action, Prompt = entry.Prompt,
            Selection = entry.SelectedText, Result = entry.Response,
            Application = entry.SourceApplication, SourceTitle = entry.SourceTitle
        };

        private void SaveNote(string selection, string edited, string application, string title)
        {
            var entry = new HistoryEntry {
                CreatedUtc = DateTime.UtcNow, Action = "note", SelectedText = selection,
                Response = edited, Source = title ?? application,
                SourceTitle = title, SourceApplication = application
            };
            // Record first so a failed Markdown write can remove the corresponding history row.
            history.Add(entry);
            try { notes.Append(entry); }
            catch
            {
                history.Delete(entry.Id);
                throw;
            }
        }

        private static AppSettings Clone(AppSettings source) => new AppSettings {
            BaseUrl = source.BaseUrl, Model = source.Model, TimeoutSeconds = source.TimeoutSeconds,
            ProtectedApiKey = source.ProtectedApiKey,
            TranslationTargetLanguage = source.TranslationTargetLanguage,
            ExcludedApplications = new List<string>(source.ExcludedApplications ?? new List<string>()),
            AutoShow = source.AutoShow
        };

        public void Dispose()
        {
            if (trigger != null) { trigger.SelectionRequested -= OnSelectionRequested; trigger.Dispose(); trigger = null; }
            chat.Dispose();
            history.Dispose();
        }
    }
}
