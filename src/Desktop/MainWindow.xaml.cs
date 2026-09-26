using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using AiSelectionToolbar.Core;

namespace AiSelectionToolbar.Desktop
{
    public partial class MainWindow : Window
    {
        private readonly object _stateLock = new object();
        private readonly List<HistoryItem> _history = new List<HistoryItem>();
        private DesktopSettings _settings = new DesktopSettings();
        private IDesktopSelectionSource _selectionSource;
        private CancellationTokenSource _generation;
        private LocalManagementServer _server;
        private string _sourceApplication = "演示";
        private string _lastExplanation;
        private string _sourceTitle = "";
        private Rectangle _selectionBounds;
        private double _compactWidth = 430;
        private double _compactHeight = 62;
        private readonly DispatcherTimer _dismissTimer = new DispatcherTimer {
            Interval = TimeSpan.FromSeconds(8)
        };

        public IStreamingActionProvider ActionProvider { get; set; } = new DemoStreamingActionProvider();
        // Optional Core hooks. If absent the prototype keeps a bounded in-memory history.
        public Action<HistoryItem> SaveHistoryItem { get; set; }
        public Func<string, int, HistoryItem[]> LoadHistoryItems { get; set; }
        public Action<long> DeleteHistoryItem { get; set; }
        public Action ClearHistoryItems { get; set; }
        public Func<DesktopSettings> LoadSettings { get; set; }
        public Action<DesktopSettings> SaveSettings { get; set; }
        public Action<ApiConnectionInput> SaveApiConnection { get; set; }
        public Action<string, string, string, string> SaveNote { get; set; }
        public Func<string, string, bool> IsDuplicateNote { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            _dismissTimer.Tick += (sender, args) => {
                _dismissTimer.Stop();
                if (QuickPanel.Visibility == Visibility.Visible) Hide();
            };
        }

        public void ShowExpanded()
        {
            _dismissTimer.Stop();
            MinWidth = 480;
            MinHeight = 380;
            Width = 540;
            Height = 430;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            QuickPanel.Visibility = Visibility.Collapsed;
            ExpandedPanel.Visibility = Visibility.Visible;
            Topmost = false;
            if (IsVisible)
            {
                if (!_selectionBounds.IsEmpty) PositionNearSelection(_selectionBounds);
                else
                {
                    Left = Math.Max(SystemParameters.WorkArea.Left,
                        SystemParameters.WorkArea.Right - Width - 24);
                    Top = Math.Max(SystemParameters.WorkArea.Top,
                        SystemParameters.WorkArea.Bottom - Height - 24);
                }
            }
        }

        private void ShowCompact()
        {
            _dismissTimer.Stop();
            RefreshToolbarAppearance();
            ResizeMode = ResizeMode.NoResize;
            MinWidth = _compactWidth;
            MinHeight = _compactHeight;
            Width = _compactWidth;
            Height = _compactHeight;
            ExpandedPanel.Visibility = Visibility.Collapsed;
            QuickPanel.Visibility = Visibility.Visible;
            Topmost = true;
        }

        private void RefreshToolbarAppearance()
        {
            var settings = GetSettings();
            System.Windows.Media.Color accent;
            try { accent = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(settings.ToolbarAccentColor ?? "#4F46E5"); }
            catch { accent = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4F46E5"); }
            var brush = new SolidColorBrush(accent);
            AccentBadge.Background = brush;
            ExpandedAccentBadge.Background = brush;
            QuickCustomActions.Children.Clear();
            ExpandedCustomActions.Children.Clear();
            var customWidth = 0d;
            if (settings.CustomActions != null)
            {
                foreach (var custom in settings.CustomActions)
                {
                    if (custom == null || string.IsNullOrWhiteSpace(custom.Id) ||
                        string.IsNullOrWhiteSpace(custom.Name)) continue;
                    QuickCustomActions.Children.Add(CreateCustomButton(custom));
                    ExpandedCustomActions.Children.Add(CreateCustomButton(custom));
                    customWidth += Math.Min(156, 28 + custom.Name.Length * 15);
                }
            }
            var compact = string.Equals(settings.ToolbarStyle, "compact", StringComparison.Ordinal);
            var overflow = 430 + customWidth > 760;
            _compactHeight = compact ? (overflow ? 70 : 52) : (overflow ? 78 : 62);
            _compactWidth = Math.Max(430, Math.Min(760, 430 + customWidth));
            QuickCustomActions.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 3, 0);
            QuickPanel.BorderBrush = compact ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 231, 241)) :
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(212, 222, 238));
        }

        private Button CreateCustomButton(CustomActionDefinition custom)
        {
            var button = new Button { Content = custom.Name, Tag = "custom:" + custom.Id,
                ToolTip = "自定义操作：" + custom.Name, MaxWidth = 156 };
            button.Click += Action_Click;
            return button;
        }

        // A composition root may attach a source that publishes SelectionSnapshot values.
        // It can also call ShowSelection directly from any thread.
        public void AttachSelectionSource(IDesktopSelectionSource source)
        {
            if (_selectionSource != null) _selectionSource.SelectionCaptured -= SelectionCaptured;
            _selectionSource = source;
            if (source != null) source.SelectionCaptured += SelectionCaptured;
        }

        private void SelectionCaptured(object sender, DesktopSelectionEventArgs e)
        {
            if (e != null) ShowSelection(e.Text, e.SourceApplication, e.SourceTitle, e.Bounds);
        }

        public void ShowSelection(string text, string sourceApplication, string sourceTitle)
        {
            ShowSelection(text, sourceApplication, sourceTitle, Rectangle.Empty);
        }

        public void ShowSelection(string text, string sourceApplication, string sourceTitle, Rectangle bounds)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowSelection(text, sourceApplication, sourceTitle, bounds)));
                return;
            }
            if (string.IsNullOrWhiteSpace(text)) return;
            sourceApplication = sourceApplication ?? "未知程序";
            var processName = NormalizeApplicationName(sourceApplication);
            if (GetSettings().ExcludedApplications.Any(x =>
                string.Equals(NormalizeApplicationName(x), processName, StringComparison.OrdinalIgnoreCase))) return;
            CancelGeneration();
            _selectionBounds = bounds;
            ShowCompact();
            _sourceApplication = sourceApplication;
            _lastExplanation = null;
            _sourceTitle = sourceTitle ?? "";
            SelectionText.Text = text;
            SourceLabel.Text = string.IsNullOrWhiteSpace(sourceTitle)
                ? sourceApplication : sourceApplication + " · " + sourceTitle;
            ResultText.Text = "选择解释、翻译或提问。";
            StatusLabel.Text = "已捕获选区";
            if (GetSettings().AutoShow)
            {
                if (!IsVisible) Show();
                PositionNearSelection(bounds);
                _dismissTimer.Start();
            }
        }

        private void PositionNearSelection(Rectangle bounds)
        {
            if (bounds.IsEmpty) return;
            var area = System.Windows.Forms.Screen.FromRectangle(bounds).WorkingArea;
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
            if (!transform.HasValue) return;
            var desired = transform.Value.Transform(new System.Windows.Point(bounds.Right + 10, bounds.Bottom + 10));
            var topLeft = transform.Value.Transform(new System.Windows.Point(area.Left, area.Top));
            var bottomRight = transform.Value.Transform(new System.Windows.Point(area.Right, area.Bottom));
            Left = Math.Max(topLeft.X, Math.Min(desired.X, bottomRight.X - Width));
            Top = Math.Max(topLeft.Y, Math.Min(desired.Y, bottomRight.Y - Height));
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshToolbarAppearance();
            Left = Math.Max(0, SystemParameters.WorkArea.Right - Width - 24);
            Top = Math.Max(0, SystemParameters.WorkArea.Bottom - Height - 24);
            _server = new LocalManagementServer(GetSettings, UpdateSettings, GetHistory,
                AddExclusion, RemoveExclusion, UpdateApiConnection,
                id => { if (DeleteHistoryItem != null) DeleteHistoryItem(id); },
                () => { if (ClearHistoryItems != null) ClearHistoryItems(); });
            try { _server.Start(); }
            catch (Exception ex) { StatusLabel.Text = "管理页未启动：" + ex.Message; }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _dismissTimer.Stop();
            CancelGeneration();
            if (_selectionSource != null) _selectionSource.SelectionCaptured -= SelectionCaptured;
            if (_server != null) _server.Dispose();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is TextBlock || e.OriginalSource is Grid) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _dismissTimer.Stop();
            CancelGeneration();
            Hide();
        }

        private void ExcludeCurrent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AddExclusion(_sourceApplication);
                _dismissTimer.Stop();
                Hide();
            }
            catch (Exception ex)
            {
                ShowExpanded();
                StatusLabel.Text = "无法排除程序：" + ex.Message;
            }
        }

        private void Demo_Click(object sender, RoutedEventArgs e)
        {
            ShowSelection("XDP 程序在网卡驱动收包后、构造 skb 之前执行。", "演示程序", "eBPF 学习笔记");
        }

        private void Manage_Click(object sender, RoutedEventArgs e)
        {
            OpenManagement();
        }

        public void OpenManagement()
        {
            if (_server == null || !_server.IsRunning)
            {
                StatusLabel.Text = "本机管理页不可用";
                return;
            }
            try { Process.Start(new ProcessStartInfo(_server.ManagementUrl) { UseShellExecute = true }); }
            catch (Exception ex) { StatusLabel.Text = "无法打开管理页：" + ex.Message; }
        }

        private async void Action_Click(object sender, RoutedEventArgs e)
        {
            var action = ((Button)sender).Tag as string;
            var selection = SelectionText.Text.Trim();
            if (selection.Length == 0) { StatusLabel.Text = "请先选择文字"; return; }
            ShowExpanded();
            string prompt = "";
            if (action == "ask")
            {
                var entered = EditText("输入问题", "请针对选中文字提问：", "");
                if (entered == null) return;
                prompt = entered.Trim();
                if (prompt.Length == 0) { StatusLabel.Text = "问题不能为空"; return; }
            }
            CancelGeneration();
            var cancellation = new CancellationTokenSource();
            _generation = cancellation;
            StopButton.IsEnabled = true;
            ResultText.Clear();
            StatusLabel.Text = "生成中…";
            var progress = new Progress<string>(chunk =>
            {
                if (_generation != cancellation || cancellation.IsCancellationRequested) return;
                ResultText.AppendText(chunk);
                ResultText.ScrollToEnd();
            });
            try
            {
                await ActionProvider.RunAsync(action, selection, prompt, progress, cancellation.Token);
                if (_generation == cancellation)
                {
                    StatusLabel.Text = "完成";
                    if (action == "explain") _lastExplanation = ResultText.Text;
                    AddHistory(action, selection, ResultText.Text, _sourceApplication, prompt);
                }
            }
            catch (OperationCanceledException) { if (_generation == cancellation) StatusLabel.Text = "已停止"; }
            catch (Exception ex)
            {
                if (_generation == cancellation)
                {
                    ResultText.AppendText((ResultText.Text.Length == 0 ? "" : "\n\n") + ex.Message);
                    StatusLabel.Text = "操作失败，详情见结果区";
                }
            }
            finally
            {
                if (_generation == cancellation) { _generation = null; StopButton.IsEnabled = false; }
                cancellation.Dispose();
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e) { CancelGeneration(); StatusLabel.Text = "已停止"; }

        private void CancelGeneration()
        {
            var active = _generation;
            _generation = null;
            StopButton.IsEnabled = false;
            if (active != null) active.Cancel();
        }

        private void Note_Click(object sender, RoutedEventArgs e)
        {
            var selection = SelectionText.Text.Trim();
            if (selection.Length == 0) { StatusLabel.Text = "请先选择文字"; return; }
            if (string.IsNullOrWhiteSpace(_lastExplanation))
            { StatusLabel.Text = "请先完成解释，再保存笔记"; return; }
            var initial = _lastExplanation;
            var edited = EditText("编辑并确认笔记", "编辑笔记内容，确认后保存：", initial);
            if (edited == null) return;
            if (string.IsNullOrWhiteSpace(edited)) { StatusLabel.Text = "空笔记未保存"; return; }
            try
            {
                if (IsDuplicateNote != null && IsDuplicateNote(selection, edited))
                {
                    if (MessageBox.Show(this, "检测到相同笔记，仍要保存？", "重复笔记",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                }
                if (SaveNote != null) SaveNote(selection, edited, _sourceApplication, _sourceTitle);
                else AddHistory("note", selection, edited, _sourceApplication, "");
                StatusLabel.Text = "笔记已保存";
            }
            catch (Exception ex) { StatusLabel.Text = "笔记保存失败：" + ex.Message; }
        }

        private string EditText(string title, string instruction, string initial)
        {
            var editor = new Window { Title = title, Owner = this, Width = 480, Height = 330,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White };
            var layout = new DockPanel { Margin = new Thickness(16) };
            var hint = new TextBlock { Text = instruction, Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(hint, Dock.Top);
            layout.Children.Add(hint);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var cancel = new Button { Content = "取消", MinWidth = 72 };
            var save = new Button { Content = "确认保存", MinWidth = 90 };
            cancel.Click += (s, args) => editor.DialogResult = false;
            save.Click += (s, args) => editor.DialogResult = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            DockPanel.SetDock(buttons, Dock.Bottom);
            layout.Children.Add(buttons);
            var note = new TextBox { Text = initial,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            layout.Children.Add(note);
            editor.Content = layout;
            return editor.ShowDialog() == true ? note.Text : null;
        }

        private void AddHistory(string action, string selection, string result, string application, string prompt)
        {
            var historyAction = action;
            if (action != null && action.StartsWith("custom:", StringComparison.Ordinal))
            {
                var id = action.Substring("custom:".Length);
                var configured = GetSettings().CustomActions;
                var match = configured == null ? null : configured.FirstOrDefault(x => x.Id == id);
                historyAction = "自定义 · " + (match == null ? "操作" : match.Name);
            }
            var item = new HistoryItem { TimeUtc = DateTime.UtcNow, Action = historyAction,
                Selection = selection, Result = result, Application = application,
                SourceTitle = _sourceTitle, Prompt = prompt };
            if (SaveHistoryItem != null) { SaveHistoryItem(item); return; }
            lock (_stateLock)
            {
                _history.Insert(0, item);
                if (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
            }
        }

        private DesktopSettings GetSettings()
        {
            if (LoadSettings != null)
            {
                var external = LoadSettings();
                if (external != null) return new DesktopSettings {
                    AutoShow = external.AutoShow, TargetLanguage = external.TargetLanguage,
                    StartOnLogin = external.StartOnLogin, NotesDirectory = external.NotesDirectory,
                    ApiBaseUrl = external.ApiBaseUrl, Model = external.Model,
                    CustomActions = new List<CustomActionDefinition>(external.CustomActions ?? new List<CustomActionDefinition>()),
                    ToolbarStyle = external.ToolbarStyle, ToolbarAccentColor = external.ToolbarAccentColor,
                    ExcludedApplications = new List<string>(external.ExcludedApplications ?? new List<string>()) };
            }
            lock (_stateLock) return new DesktopSettings { AutoShow = _settings.AutoShow,
                TargetLanguage = _settings.TargetLanguage,
                StartOnLogin = _settings.StartOnLogin, NotesDirectory = _settings.NotesDirectory,
                ApiBaseUrl = _settings.ApiBaseUrl, Model = _settings.Model,
                CustomActions = new List<CustomActionDefinition>(_settings.CustomActions ?? new List<CustomActionDefinition>()),
                ToolbarStyle = _settings.ToolbarStyle, ToolbarAccentColor = _settings.ToolbarAccentColor,
                ExcludedApplications = new List<string>(_settings.ExcludedApplications) };
        }

        private void UpdateSettings(DesktopSettings value)
        {
            var current = GetSettings();
            current.AutoShow = value.AutoShow;
            current.TargetLanguage = value.TargetLanguage;
            current.StartOnLogin = value.StartOnLogin;
            current.NotesDirectory = value.NotesDirectory;
            current.CustomActions = value.CustomActions ?? new List<CustomActionDefinition>();
            current.ToolbarStyle = value.ToolbarStyle;
            current.ToolbarAccentColor = value.ToolbarAccentColor;
            PersistSettings(current);
        }

        private void UpdateApiConnection(ApiConnectionInput input)
        {
            var current = GetSettings();
            current.ApiBaseUrl = input.ApiBaseUrl;
            current.Model = input.Model;
            if (SaveApiConnection != null) SaveApiConnection(input);
            PersistSettings(current);
        }

        private void PersistSettings(DesktopSettings settings)
        {
            lock (_stateLock) _settings = settings;
            if (SaveSettings != null) SaveSettings(settings);
            Dispatcher.BeginInvoke(new Action(RefreshToolbarAppearance));
        }

        private HistoryItem[] GetHistory(string term, int offset)
        {
            if (LoadHistoryItems != null) return LoadHistoryItems(term, offset);
            lock (_stateLock) return _history.Where(item => string.IsNullOrWhiteSpace(term) ||
                (item.Selection ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                .Skip(offset).Take(50).ToArray();
        }

        private void AddExclusion(string application)
        {
            var processName = NormalizeApplicationName(application);
            if (processName.Length == 0) throw new ArgumentException("程序名不能为空。", nameof(application));
            var current = GetSettings();
            if (!current.ExcludedApplications.Any(x => string.Equals(NormalizeApplicationName(x), processName, StringComparison.OrdinalIgnoreCase)))
                current.ExcludedApplications.Add(processName + ".exe");
            PersistSettings(current);
        }

        private void RemoveExclusion(string application)
        {
            var processName = NormalizeApplicationName(application);
            var current = GetSettings();
            current.ExcludedApplications.RemoveAll(x => string.Equals(NormalizeApplicationName(x), processName, StringComparison.OrdinalIgnoreCase));
            PersistSettings(current);
        }

        private static string NormalizeApplicationName(string application)
        {
            var name = (application ?? "").Trim().Trim('"');
            var slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) name = name.Substring(slash + 1);
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4) : name;
        }
    }
}
