using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Navigation;
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
        private string _selectedText = "";
        private string _lastExplanation;
        private string _sourceTitle = "";
        private Rectangle _selectionBounds;
        private double _compactWidth = 520;
        private double _compactHeight = 62;
        private readonly StringBuilder _rawResult = new StringBuilder();
        private bool _receivedReasoning;
        private readonly DispatcherTimer _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
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
            _renderTimer.Tick += (sender, args) => { _renderTimer.Stop(); RenderResult(); };
        }

        public void ShowExpanded()
        {
            _dismissTimer.Stop();
            MinWidth = 360;
            MinHeight = 150;
            Width = 420;
            Height = 155;
            ResizeMode = ResizeMode.NoResize;
            QuickPanel.Visibility = Visibility.Collapsed;
            ExpandedPanel.Visibility = Visibility.Visible;
            Topmost = true;
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
            QuickCustomActions.Children.Clear();
            if (settings.CustomActions != null)
            {
                foreach (var custom in settings.CustomActions)
                {
                    if (custom == null || string.IsNullOrWhiteSpace(custom.Id) ||
                        string.IsNullOrWhiteSpace(custom.Name)) continue;
                    QuickCustomActions.Children.Add(CreateCustomButton(custom));
                }
            }
            var compact = string.Equals(settings.ToolbarStyle, "compact", StringComparison.Ordinal);
            QuickCustomActions.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 3, 0);
            QuickButtonStrip.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var chrome = QuickScroll.Padding.Left + QuickScroll.Padding.Right +
                QuickPanel.BorderThickness.Left + QuickPanel.BorderThickness.Right +
                QuickPanel.Margin.Left + QuickPanel.Margin.Right + 2;
            var requiredWidth = Math.Ceiling(QuickButtonStrip.DesiredSize.Width + chrome);
            var workArea = System.Windows.Forms.Screen.FromRectangle(_selectionBounds.IsEmpty
                ? System.Windows.Forms.Screen.PrimaryScreen.WorkingArea : _selectionBounds).WorkingArea;
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
            var maxWidth = transform.HasValue
                ? transform.Value.Transform(new System.Windows.Point(workArea.Right, 0)).X -
                  transform.Value.Transform(new System.Windows.Point(workArea.Left, 0)).X - 24
                : SystemParameters.WorkArea.Width - 24;
            _compactWidth = Math.Min(requiredWidth, Math.Max(160, maxWidth));
            var overflow = requiredWidth > _compactWidth;
            _compactHeight = compact ? (overflow ? 70 : 52) : (overflow ? 78 : 62);
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
            // Clicking a button can leave the source app foreground with its old selection.
            // A second mouse-up must not replace an open answer or cancel its stream.
            if (IsVisible && ExpandedPanel.Visibility == Visibility.Visible &&
                string.Equals(_selectedText, text, StringComparison.Ordinal) &&
                string.Equals(_sourceApplication, sourceApplication, StringComparison.OrdinalIgnoreCase)) return;
            CancelGeneration();
            _selectionBounds = bounds;
            ShowCompact();
            _sourceApplication = sourceApplication;
            _selectedText = text;
            _lastExplanation = null;
            NoteButton.IsEnabled = false;
            _sourceTitle = sourceTitle ?? "";
            _rawResult.Clear();
            _receivedReasoning = false;
            _renderTimer.Stop();
            RenderResult();
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
            ShowCompact();
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
            _renderTimer.Stop();
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
            _renderTimer.Stop();
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
                MessageBox.Show(this, "无法排除程序：" + ex.Message, "排除程序");
            }
        }

        private void Manage_Click(object sender, RoutedEventArgs e)
        {
            OpenManagement();
        }

        public void OpenManagement()
        {
            if (_server == null || !_server.IsRunning)
            {
                MessageBox.Show(this, "本机管理页不可用。", "设置和历史");
                return;
            }
            try { Process.Start(new ProcessStartInfo(_server.ManagementUrl) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "无法打开管理页：" + ex.Message, "设置和历史"); }
        }

        private async void Action_Click(object sender, RoutedEventArgs e)
        {
            var action = ((Button)sender).Tag as string;
            var selection = _selectedText.Trim();
            if (selection.Length == 0)
            {
                ShowExpanded();
                _rawResult.Clear(); _rawResult.Append("请先选中文字，再选择操作。"); RenderResult();
                return;
            }
            string prompt = "";
            if (action == "ask")
            {
                var entered = EditText("输入问题", "请针对选中文字提问：", "");
                if (entered == null) return;
                prompt = entered.Trim();
                if (prompt.Length == 0) { MessageBox.Show(this, "问题不能为空。", "提问"); return; }
            }
            CancelGeneration();
            _rawResult.Clear();
            _receivedReasoning = false;
            ShowExpanded();
            var cancellation = new CancellationTokenSource();
            _generation = cancellation;
            NoteButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            RenderResult();
            StatusLabel.Text = "生成中…";
            var progress = new UiProgress(Dispatcher, chunk =>
            {
                if (_generation != cancellation || cancellation.IsCancellationRequested) return;
                if (chunk == null) _receivedReasoning = true;
                else _rawResult.Append(chunk);
                if (!_renderTimer.IsEnabled) _renderTimer.Start();
            });
            try
            {
                await ActionProvider.RunAsync(action, selection, prompt, progress, cancellation.Token);
                if (_generation == cancellation)
                {
                    _renderTimer.Stop();
                    var hasAnswer = _rawResult.Length > 0;
                    if (!hasAnswer)
                        _rawResult.Append("模型未返回回答正文，请重试或切换模型。");
                    RenderResult();
                    StatusLabel.Text = hasAnswer ? "完成" : "无回答正文";
                    if (hasAnswer && (action == "explain" || action == "explain_detailed"))
                    {
                        _lastExplanation = _rawResult.ToString();
                        NoteButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastExplanation);
                    }
                    if (hasAnswer)
                        AddHistory(action, selection, _rawResult.ToString(), _sourceApplication, prompt);
                }
            }
            catch (OperationCanceledException)
            {
                if (_generation == cancellation)
                {
                    _renderTimer.Stop();
                    _rawResult.Append((_rawResult.Length == 0 ? "" : "\n\n") + "模型响应超时，请重试。");
                    RenderResult();
                    StatusLabel.Text = "请求超时";
                }
            }
            catch (Exception ex)
            {
                if (_generation == cancellation)
                {
                    _rawResult.Append((_rawResult.Length == 0 ? "" : "\n\n") + ex.Message);
                    _renderTimer.Stop(); RenderResult();
                    StatusLabel.Text = "操作失败，详情见结果区";
                }
            }
            finally
            {
                if (_generation == cancellation) { _generation = null; StopButton.IsEnabled = false; }
                if (_generation == null) { _renderTimer.Stop(); RenderResult(); }
                cancellation.Dispose();
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            CancelGeneration();
            _renderTimer.Stop();
            if (_rawResult.Length == 0) _rawResult.Append("已停止。");
            RenderResult();
            StatusLabel.Text = "已停止";
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var plain = new TextRange(ResultView.Document.ContentStart, ResultView.Document.ContentEnd).Text.Trim();
                if (plain.Length > 0) Clipboard.SetText(plain);
            }
            catch (Exception ex) { MessageBox.Show(this, "复制失败：" + ex.Message, "复制回答"); }
        }

        private void CancelGeneration()
        {
            var active = _generation;
            _generation = null;
            StopButton.IsEnabled = false;
            if (active != null) active.Cancel();
        }

        private void Note_Click(object sender, RoutedEventArgs e)
        {
            var selection = _selectedText.Trim();
            if (selection.Length == 0) return;
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
            catch (Exception ex) { MessageBox.Show(this, "笔记保存失败：" + ex.Message, "保存笔记"); }
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

        private void RenderResult()
        {
            var markdown = _rawResult.ToString();
            var document = new FlowDocument {
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei"), FontSize = 14,
                PagePadding = new Thickness(9), ColumnWidth = 1000
            };
            if (markdown.Length == 0)
            {
                document.Blocks.Add(new Paragraph(new Run(_receivedReasoning ? "模型正在思考，等待回答正文…" : "正在生成…")) {
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(135, 147, 164)) });
            }
            else
            {
                var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                var code = new StringBuilder();
                var inCode = false;
                for (var index = 0; index < lines.Length; index++)
                {
                    var line = lines[index];
                    if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        if (inCode) { AddCodeBlock(document, code.ToString()); code.Clear(); }
                        inCode = !inCode;
                        continue;
                    }
                    if (inCode) { code.AppendLine(line); continue; }
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var headingLevel = 0;
                    while (headingLevel < line.Length && headingLevel < 3 && line[headingLevel] == '#') headingLevel++;
                    if (headingLevel > 0 && headingLevel < line.Length && line[headingLevel] == ' ')
                    {
                        var heading = new Paragraph { FontWeight = FontWeights.SemiBold,
                            FontSize = headingLevel == 1 ? 20 : headingLevel == 2 ? 17 : 15,
                            Margin = new Thickness(0, 10, 0, 7) };
                        AddInlines(heading.Inlines, line.Substring(headingLevel + 1));
                        document.Blocks.Add(heading);
                        continue;
                    }

                    bool ordered;
                    string itemText;
                    if (TryListItem(line, out ordered, out itemText))
                    {
                        var list = new System.Windows.Documents.List {
                            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                            Margin = new Thickness(18, 4, 0, 8) };
                        do
                        {
                            var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                            AddInlines(paragraph.Inlines, itemText);
                            list.ListItems.Add(new ListItem(paragraph));
                            if (index + 1 >= lines.Length) break;
                            bool nextOrdered;
                            string nextText;
                            if (!TryListItem(lines[index + 1], out nextOrdered, out nextText) || nextOrdered != ordered) break;
                            index++;
                            itemText = nextText;
                        } while (true);
                        document.Blocks.Add(list);
                        continue;
                    }
                    var body = new Paragraph { Margin = new Thickness(0, 0, 0, 9), LineHeight = 22 };
                    AddInlines(body.Inlines, line);
                    document.Blocks.Add(body);
                }
                if (inCode) AddCodeBlock(document, code.ToString());
            }
            ResultView.Document = document;
            ResultView.ScrollToEnd();
            ResizeAnswerToContent(markdown);
        }

        private void ResizeAnswerToContent(string markdown)
        {
            if (ExpandedPanel.Visibility != Visibility.Visible) return;
            var area = System.Windows.Forms.Screen.FromRectangle(_selectionBounds.IsEmpty
                ? System.Windows.Forms.Screen.PrimaryScreen.WorkingArea : _selectionBounds).WorkingArea;
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
            var topLeft = transform.HasValue
                ? transform.Value.Transform(new System.Windows.Point(area.Left, area.Top))
                : new System.Windows.Point(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top);
            var bottomRight = transform.HasValue
                ? transform.Value.Transform(new System.Windows.Point(area.Right, area.Bottom))
                : new System.Windows.Point(SystemParameters.WorkArea.Right, SystemParameters.WorkArea.Bottom);
            var maxWidth = Math.Max(360, bottomRight.X - topLeft.X - 24);
            var maxHeight = Math.Max(150, bottomRight.Y - topLeft.Y - 24);
            var lines = (markdown.Length == 0 ? "正在生成…" : markdown)
                .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var typeface = new Typeface(new System.Windows.Media.FontFamily("Segoe UI"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var longest = 0d;
            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                var measured = new FormattedText(line, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 14, System.Windows.Media.Brushes.Black);
                longest = Math.Max(longest, measured.WidthIncludingTrailingWhitespace);
            }
            var targetWidth = Math.Min(maxWidth, Math.Max(380, Math.Min(650, longest + 100)));
            Width = targetWidth;

            var contentWidth = Math.Max(220, targetWidth - 95);
            var contentHeight = 0d;
            var inCode = false;
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                { inCode = !inCode; contentHeight += 8; continue; }
                if (string.IsNullOrWhiteSpace(line))
                { contentHeight += 4; continue; }
                var fontSize = line.StartsWith("# ", StringComparison.Ordinal) ? 20 :
                    line.StartsWith("## ", StringComparison.Ordinal) ? 17 : 14;
                var measured = new FormattedText(line, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, fontSize, System.Windows.Media.Brushes.Black) {
                    MaxTextWidth = contentWidth - (inCode ? 12 : 0)
                };
                contentHeight += Math.Max(inCode ? 19 : 22, measured.Height) + (inCode ? 1 : 7);
            }
            // Header, outer margins, document padding and result surface.
            Height = Math.Min(maxHeight, Math.Max(150, Math.Ceiling(contentHeight + 122)));
            if (IsVisible)
            {
                Left = Math.Max(topLeft.X, Math.Min(Left, bottomRight.X - Width));
                Top = Math.Max(topLeft.Y, Math.Min(Top, bottomRight.Y - Height));
            }
        }

        private static bool TryListItem(string line, out bool ordered, out string content)
        {
            var trimmed = line.TrimStart();
            ordered = false;
            content = null;
            if (trimmed.Length >= 2 && (trimmed[0] == '-' || trimmed[0] == '*') && trimmed[1] == ' ')
            { content = trimmed.Substring(2); return true; }
            var digits = 0;
            while (digits < trimmed.Length && digits < 3 && char.IsDigit(trimmed[digits])) digits++;
            if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] == '.' && trimmed[digits + 1] == ' ')
            { ordered = true; content = trimmed.Substring(digits + 2); return true; }
            return false;
        }

        private static void AddCodeBlock(FlowDocument document, string content)
        {
            document.Blocks.Add(new Paragraph(new Run(content.TrimEnd('\n'))) {
                FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12.5,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 238, 246)),
                Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 11) });
        }

        // Builds document objects from text. Markdown is never parsed as XAML or HTML.
        private static void AddInlines(InlineCollection target, string source)
        {
            var plain = new StringBuilder();
            Action flush = () => { if (plain.Length > 0) { target.Add(new Run(plain.ToString())); plain.Clear(); } };
            for (var index = 0; index < source.Length;)
            {
                if (source[index] == '\\' && index + 1 < source.Length)
                { plain.Append(source[index + 1]); index += 2; continue; }
                if (index + 1 < source.Length && source[index] == '*' && source[index + 1] == '*')
                {
                    var end = source.IndexOf("**", index + 2, StringComparison.Ordinal);
                    if (end > index + 2)
                    { flush(); target.Add(new Bold(new Run(source.Substring(index + 2, end - index - 2)))); index = end + 2; continue; }
                }
                if (source[index] == '*' || source[index] == '`')
                {
                    var marker = source[index];
                    var end = source.IndexOf(marker, index + 1);
                    if (end > index + 1)
                    {
                        flush();
                        var content = new Run(source.Substring(index + 1, end - index - 1));
                        if (marker == '*') target.Add(new Italic(content));
                        else target.Add(new Span(content) {
                            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 238, 246)) });
                        index = end + 1; continue;
                    }
                }
                if (source[index] == '[')
                {
                    var labelEnd = source.IndexOf("](", index + 1, StringComparison.Ordinal);
                    var urlEnd = labelEnd < 0 ? -1 : source.IndexOf(')', labelEnd + 2);
                    Uri uri;
                    if (labelEnd > index + 1 && urlEnd > labelEnd + 2 &&
                        Uri.TryCreate(source.Substring(labelEnd + 2, urlEnd - labelEnd - 2), UriKind.Absolute, out uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        flush();
                        var link = new Hyperlink(new Run(source.Substring(index + 1, labelEnd - index - 1))) {
                            NavigateUri = uri,
                            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(70, 81, 210)) };
                        link.RequestNavigate += Link_RequestNavigate;
                        target.Add(link);
                        index = urlEnd + 1; continue;
                    }
                }
                plain.Append(source[index++]);
            }
            flush();
        }

        private static void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            e.Handled = true;
            if (e.Uri == null || (e.Uri.Scheme != Uri.UriSchemeHttp && e.Uri.Scheme != Uri.UriSchemeHttps)) return;
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* An unavailable browser must not close the result window. */ }
        }

        private sealed class UiProgress : IProgress<string>
        {
            private readonly Dispatcher dispatcher;
            private readonly Action<string> update;

            public UiProgress(Dispatcher dispatcher, Action<string> update)
            { this.dispatcher = dispatcher; this.update = update; }

            public void Report(string value)
            {
                if (dispatcher.CheckAccess()) update(value);
                else dispatcher.Invoke(new Action(() => update(value)));
            }
        }

        private void AddHistory(string action, string selection, string result, string application, string prompt)
        {
            var historyAction = action == "explain" ? "了解" :
                action == "explain_detailed" ? "详细解释" : action;
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
