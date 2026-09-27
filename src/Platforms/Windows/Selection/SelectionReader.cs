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
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint WmGetText = 0x000D;
        private const uint WmGetTextLength = 0x000E;
        private const uint EmGetSel = 0x00B0;
        private const int EsPassword = 0x0020;
        private const int GwlStyle = -16;
        private const uint MessageTimeoutMs = 250;
        private const int MaxEditLength = 32767;
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
            uint foregroundThreadId = GetWindowThreadProcessId(window, out processId);
            if (foregroundThreadId == 0 || processId == 0 ||
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
                if (element != null && BelongsToWindow(element, window, (int)processId))
                {
                    int focusedProcessId = element.Current.ProcessId;
                    if (!IsAllowedProcess(focusedProcessId, (int)processId))
                        return null;
                    if (element.Current.IsPassword)
                        return null;

                    TextPattern pattern = FindTextPattern(element, window, (int)processId);
                    if (pattern != null)
                    {
                        TextPatternRange[] ranges = pattern.GetSelection();
                        if (ranges != null && ranges.Length > 0)
                        {
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

                            if (!String.IsNullOrWhiteSpace(selected.ToString()))
                            {
                                if (GetForegroundWindow() != window)
                                    return null;
                                return new SelectionSnapshot(selected.ToString(), application,
                                    GetWindowTitle(window), bounds);
                            }
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException ||
                                              exception is InvalidOperationException ||
                                              exception is COMException ||
                                              exception is UnauthorizedAccessException ||
                                              exception is ArgumentException ||
                                              exception is System.ComponentModel.Win32Exception)
            {
                // Some standard Edit controls expose no usable UIA TextPattern.
            }

            return TryReadStandardEdit(window, foregroundThreadId, processId, application);
        }

        private SelectionSnapshot TryReadStandardEdit(IntPtr window, uint threadId,
            uint processId, string application)
        {
            GUITHREADINFO info = new GUITHREADINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(GUITHREADINFO));
            if (!GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == IntPtr.Zero ||
                GetAncestor(info.hwndFocus, 2) != window)
                return null;

            uint focusedProcessId;
            if (GetWindowThreadProcessId(info.hwndFocus, out focusedProcessId) == 0 ||
                focusedProcessId == 0 || focusedProcessId == GetCurrentProcessId())
                return null;

            if (!IsAllowedProcess((int)focusedProcessId, (int)processId))
                return null;

            StringBuilder className = new StringBuilder(32);
            if (GetClassName(info.hwndFocus, className, className.Capacity) == 0 ||
                !String.Equals(className.ToString(), "Edit", StringComparison.OrdinalIgnoreCase) ||
                !IsWindowUnicode(info.hwndFocus) ||
                (GetWindowLongPtr(info.hwndFocus, GwlStyle).ToInt64() & EsPassword) != 0)
                return null;

            IntPtr result;
            if (SendMessageTimeout(info.hwndFocus, WmGetTextLength, IntPtr.Zero, IntPtr.Zero,
                SmtoAbortIfHung, MessageTimeoutMs, out result) == IntPtr.Zero)
                return null;
            long length = result.ToInt64();
            if (length <= 0 || length > MaxEditLength)
                return null;

            if (SendMessageTimeout(info.hwndFocus, EmGetSel, IntPtr.Zero, IntPtr.Zero,
                SmtoAbortIfHung, MessageTimeoutMs, out result) == IntPtr.Zero)
                return null;
            uint selection = unchecked((uint)result.ToInt64());
            if (selection == UInt32.MaxValue)
                return null;
            int start = (int)(selection & 0xFFFF);
            int end = (int)(selection >> 16);
            if (end <= start || end > length)
                return null;

            StringBuilder value = new StringBuilder((int)length + 1);
            if (SendMessageTimeout(info.hwndFocus, WmGetText, (IntPtr)value.Capacity, value,
                SmtoAbortIfHung, MessageTimeoutMs, out result) == IntPtr.Zero ||
                result.ToInt64() < end)
                return null;

            string text = value.ToString();
            if (text.Length < end || String.IsNullOrWhiteSpace(text.Substring(start, end - start)))
                return null;

            // Unlike TextPattern this only knows the control bounds, not glyph positions.
            RECT rect;
            if (!GetWindowRect(info.hwndFocus, out rect) || rect.right <= rect.left ||
                rect.bottom <= rect.top || GetForegroundWindow() != window)
                return null;
            GUITHREADINFO current = new GUITHREADINFO();
            current.cbSize = info.cbSize;
            if (!GetGUIThreadInfo(threadId, ref current) || current.hwndFocus != info.hwndFocus)
                return null;
            Rectangle bounds = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
            return new SelectionSnapshot(text.Substring(start, end - start), application,
                GetWindowTitle(window), bounds);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        private static bool BelongsToWindow(AutomationElement element, IntPtr window, int processId)
        {
            int focusedProcessId = element.Current.ProcessId;
            for (int depth = 0; element != null && depth < 32; depth++)
            {
                int handle = element.Current.NativeWindowHandle;
                if (handle != 0)
                    return GetAncestor(new IntPtr(handle), 2) == window; // GA_ROOT

                element = TreeWalker.RawViewWalker.GetParent(element);
            }

            // A provider with no HWND can only be attributed to the host by PID.
            return focusedProcessId == processId;
        }

        private bool IsAllowedProcess(int candidateId, int foregroundId)
        {
            if (candidateId <= 0 || candidateId == (int)GetCurrentProcessId())
                return false;
            if (candidateId == foregroundId)
                return true; // The foreground process was checked before any content access.
            try
            {
                using (Process process = Process.GetProcessById(candidateId))
                    return !excludedProcessNames.Contains(NormalizeProcessName(process.ProcessName));
            }
            catch (Exception exception) when (exception is ArgumentException ||
                                              exception is InvalidOperationException ||
                                              exception is System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private TextPattern FindTextPattern(AutomationElement element, IntPtr window, int foregroundId)
        {
            for (int depth = 0; element != null && depth < 32; depth++)
            {
                int handle = element.Current.NativeWindowHandle;
                if (handle != 0 && GetAncestor(new IntPtr(handle), 2) != window)
                    break;
                if (!IsAllowedProcess(element.Current.ProcessId, foregroundId) || element.Current.IsPassword)
                    break;
                object pattern;
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                    return (TextPattern)pattern;

                element = TreeWalker.RawViewWalker.GetParent(element);
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowUnicode(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            [Out] StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder title, int maxCount);
    }
}
