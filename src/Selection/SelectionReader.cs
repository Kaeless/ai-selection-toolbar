using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace AiSelectionToolbar.Selection
{
    /// <summary>
    /// Reads the selection exposed by the foreground application's UI Automation provider.
    /// Call from a background thread; some providers make synchronous cross-process calls.
    /// </summary>
    public sealed class SelectionReader
    {
        private readonly HashSet<string> excludedProcessNames;

        public SelectionReader(IEnumerable<string> excludedProcessNames = null)
        {
            this.excludedProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excludedProcessNames == null)
                return;

            foreach (string name in excludedProcessNames)
            {
                if (!String.IsNullOrWhiteSpace(name))
                    this.excludedProcessNames.Add(NormalizeProcessName(name));
            }
        }

        public SelectionSnapshot TryRead()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return null;

            uint processId;
            if (GetWindowThreadProcessId(window, out processId) == 0 || processId == 0 ||
                processId == GetCurrentProcessId())
                return null;

            string application;
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                    application = process.ProcessName;
            }
            catch (Exception exception) when (exception is ArgumentException ||
                                              exception is InvalidOperationException ||
                                              exception is System.ComponentModel.Win32Exception)
            {
                return null;
            }

            // Check the exclusion list before requesting the focused element or its text.
            if (excludedProcessNames.Contains(NormalizeProcessName(application)))
                return null;

            try
            {
                AutomationElement element = AutomationElement.FocusedElement;
                if (element == null || element.Current.ProcessId != (int)processId)
                    return null;

                TextPattern pattern = FindTextPattern(element, (int)processId);
                if (pattern == null)
                    return null;

                TextPatternRange[] ranges = pattern.GetSelection();
                if (ranges == null || ranges.Length == 0)
                    return null;

                StringBuilder selected = new StringBuilder();
                Rectangle bounds = Rectangle.Empty;
                foreach (TextPatternRange range in ranges)
                {
                    string part = range.GetText(-1);
                    if (String.IsNullOrEmpty(part))
                        continue;

                    if (selected.Length > 0)
                        selected.Append(Environment.NewLine);
                    selected.Append(part);
                    bounds = UnionBounds(bounds, range.GetBoundingRectangles());
                }

                if (String.IsNullOrWhiteSpace(selected.ToString()))
                    return null;

                // The user may have changed windows during the cross-process UIA calls.
                if (GetForegroundWindow() != window)
                    return null;

                return new SelectionSnapshot(selected.ToString(), application, GetWindowTitle(window), bounds);
            }
            catch (Exception exception) when (exception is ElementNotAvailableException ||
                                              exception is InvalidOperationException ||
                                              exception is COMException ||
                                              exception is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static TextPattern FindTextPattern(AutomationElement element, int processId)
        {
            for (int depth = 0; element != null && depth < 32; depth++)
            {
                if (element.Current.ProcessId != processId)
                    break;

                object pattern;
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                    return (TextPattern)pattern;

                element = TreeWalker.ControlViewWalker.GetParent(element);
            }

            return null;
        }

        private static Rectangle UnionBounds(Rectangle existing, Rect[] values)
        {
            if (values == null)
                return existing;

            foreach (Rect value in values)
            {
                double x = value.X, y = value.Y, width = value.Width, height = value.Height;
                if (Double.IsNaN(x) || Double.IsNaN(y) || Double.IsNaN(width) || Double.IsNaN(height) ||
                    Double.IsInfinity(x) || Double.IsInfinity(y) || Double.IsInfinity(width) || Double.IsInfinity(height) ||
                    width <= 0 || height <= 0 || x < Int32.MinValue || y < Int32.MinValue ||
                    x + width > Int32.MaxValue || y + height > Int32.MaxValue)
                    continue;

                int left = (int)Math.Floor(x);
                int top = (int)Math.Floor(y);
                int right = (int)Math.Ceiling(x + width);
                int bottom = (int)Math.Ceiling(y + height);
                Rectangle next = Rectangle.FromLTRB(left, top, right, bottom);
                existing = existing.IsEmpty ? next : Rectangle.Union(existing, next);
            }

            return existing;
        }

        private static string NormalizeProcessName(string name)
        {
            string normalized = name.Trim().Trim('"');
            int separator = normalized.LastIndexOfAny(new[] { '\\', '/' });
            if (separator >= 0)
                normalized = normalized.Substring(separator + 1);
            return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? normalized.Substring(0, normalized.Length - 4)
                : normalized;
        }

        private static string GetWindowTitle(IntPtr window)
        {
            int length = GetWindowTextLength(window);
            StringBuilder title = new StringBuilder(length + 1);
            GetWindowText(window, title, title.Capacity);
            return title.ToString();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder title, int maxCount);
    }
}
