using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AiSelectionToolbar.Linux;

public sealed class LinuxSelectionEventArgs : EventArgs
{
    public LinuxSelectionEventArgs(string text, string sourceApplication, string sourceTitle, int x, int y)
    {
        Text = text;
        SourceApplication = sourceApplication;
        SourceTitle = sourceTitle;
        X = x;
        Y = y;
    }

    public string Text { get; }
    public string SourceApplication { get; }
    public string SourceTitle { get; }
    public int X { get; }
    public int Y { get; }
}

/// <summary>
/// X11 selection observer. All Xlib operations use one private display on one worker thread.
/// Requires xclip to request the PRIMARY selection from its owning application.
/// This is unavailable in native Wayland sessions, whose compositor does not expose
/// global pointer/keyboard state or other applications' selections to ordinary clients.
/// </summary>
public sealed class LinuxSelectionService : IDisposable
{
    private const uint Button1Mask = 1u << 8;
    private const int PollMilliseconds = 25;
    private const int MaxSelectionLength = 32768;
    private HashSet<string> excludedApplications;
    private readonly object lifecycle = new();
    private Thread? worker;
    private CancellationTokenSource? cancellation;

    public LinuxSelectionService(IEnumerable<string>? excludedApplications = null)
    {
        this.excludedApplications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        UpdateExcludedApplications(excludedApplications ?? Array.Empty<string>());
    }

    public event EventHandler<LinuxSelectionEventArgs>? SelectionCaptured;

    public void UpdateExcludedApplications(IEnumerable<string> applications)
    {
        var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in applications)
            if (!string.IsNullOrWhiteSpace(name)) next.Add(Normalize(name));
        Volatile.Write(ref excludedApplications, next);
    }

    public void Start()
    {
        lock (lifecycle)
        {
            if (worker != null) return;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
                !string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "x11", StringComparison.OrdinalIgnoreCase))
                throw new PlatformNotSupportedException("Global selection capture requires an X11 desktop session.");
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            worker = new Thread(() => Run(token)) { IsBackground = true, Name = "X11 selection observer" };
            worker.Start();
        }
    }

    private void Run(CancellationToken token)
    {
        // GTK uses its own display connection; never pass this connection to a UI thread.
        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return;
        try
        {
            IntPtr root = XDefaultRootWindow(display);
            int space = XKeysymToKeycode(display, 0x20);
            int ctrlLeft = XKeysymToKeycode(display, 0xffe3);
            int ctrlRight = XKeysymToKeycode(display, 0xffe4);
            int shiftLeft = XKeysymToKeycode(display, 0xffe1);
            int shiftRight = XKeysymToKeycode(display, 0xffe2);
            bool wasPressed = false, wasHotkey = false;
            int downX = 0, downY = 0;
            while (!token.IsCancellationRequested)
            {
                if (XQueryPointer(display, root, out _, out _, out int x, out int y,
                    out _, out _, out uint mask) != 0)
                {
                    bool pressed = (mask & Button1Mask) != 0;
                    if (pressed && !wasPressed) { downX = x; downY = y; }
                    if (!pressed && wasPressed && (Math.Abs(x - downX) > 5 || Math.Abs(y - downY) > 5))
                    {
                        // Applications update PRIMARY after processing the button release.
                        if (token.WaitHandle.WaitOne(65)) break;
                        Capture(display, x, y, token);
                    }
                    wasPressed = pressed;

                    var keys = new byte[32];
                    XQueryKeymap(display, keys);
                    bool hotkey = IsDown(keys, space) && (IsDown(keys, ctrlLeft) || IsDown(keys, ctrlRight)) &&
                        (IsDown(keys, shiftLeft) || IsDown(keys, shiftRight));
                    if (hotkey && !wasHotkey) Capture(display, x, y, token);
                    wasHotkey = hotkey;
                }
                if (token.WaitHandle.WaitOne(PollMilliseconds)) break;
            }
        }
        finally { XCloseDisplay(display); }
    }

    private void Capture(IntPtr display, int x, int y, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        XGetInputFocus(display, out IntPtr focus, out _);
        if (focus == IntPtr.Zero || focus == new IntPtr(1)) return;
        string application = "", title = "";
        for (int depth = 0; depth < 24 && focus != IntPtr.Zero; depth++)
        {
            if (XGetClassHint(display, focus, out XClassHint hint) != 0)
            {
                try { application = Marshal.PtrToStringUTF8(hint.ResClass) ?? ""; }
                finally
                {
                    if (hint.ResName != IntPtr.Zero) XFree(hint.ResName);
                    if (hint.ResClass != IntPtr.Zero) XFree(hint.ResClass);
                }
                if (!string.IsNullOrWhiteSpace(application))
                {
                    if (XFetchName(display, focus, out IntPtr name) != 0 && name != IntPtr.Zero)
                    {
                        try { title = Marshal.PtrToStringUTF8(name) ?? ""; }
                        finally { XFree(name); }
                    }
                    break;
                }
            }
            if (XQueryTree(display, focus, out _, out IntPtr parent, out IntPtr children, out _) == 0) break;
            if (children != IntPtr.Zero) XFree(children);
            if (parent == focus) break;
            focus = parent;
        }
        // Check exclusions before requesting any selection content.
        if (string.IsNullOrWhiteSpace(application) || Volatile.Read(ref excludedApplications).Contains(Normalize(application))) return;
        // PRIMARY can remain owned by the previous application after a drag in a widget
        // that does not support selection. Avoid showing that unrelated old selection.
        IntPtr owner = XGetSelectionOwner(display, XInternAtom(display, "PRIMARY", false));
        if (owner == IntPtr.Zero) return;
        if (owner != focus)
        {
            string ownerApplication = ReadWindowClass(display, owner);
            if (!string.IsNullOrEmpty(ownerApplication) &&
                !string.Equals(Normalize(ownerApplication), Normalize(application), StringComparison.OrdinalIgnoreCase))
                return;
        }
        string? selection = ReadPrimary(token);
        if (string.IsNullOrWhiteSpace(selection)) return;
        try { SelectionCaptured?.Invoke(this, new LinuxSelectionEventArgs(selection, application, title, x, y)); }
        catch (Exception) { /* A UI observer must not terminate the input worker. */ }
    }

    private static string ReadWindowClass(IntPtr display, IntPtr window)
    {
        for (int depth = 0; depth < 24 && window != IntPtr.Zero; depth++)
        {
            if (XGetClassHint(display, window, out XClassHint hint) != 0)
            {
                try
                {
                    string value = Marshal.PtrToStringUTF8(hint.ResClass) ?? "";
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                finally
                {
                    if (hint.ResName != IntPtr.Zero) XFree(hint.ResName);
                    if (hint.ResClass != IntPtr.Zero) XFree(hint.ResClass);
                }
            }
            if (XQueryTree(display, window, out _, out IntPtr parent, out IntPtr children, out _) == 0) break;
            if (children != IntPtr.Zero) XFree(children);
            if (parent == window) break;
            window = parent;
        }
        return "";
    }

    private static string? ReadPrimary(CancellationToken token)
    {
        // ArgumentList avoids a shell; stderr is discarded so no selected text enters logs.
        using var process = new Process { StartInfo = new ProcessStartInfo("xclip") {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        } };
        process.StartInfo.ArgumentList.Add("-selection");
        process.StartInfo.ArgumentList.Add("primary");
        process.StartInfo.ArgumentList.Add("-out");
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add("UTF8_STRING");
        bool started = false;
        try
        {
            process.Start();
            started = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(700);
            var read = process.StandardOutput.ReadToEndAsync(timeout.Token);
            string text = read.GetAwaiter().GetResult();
            if (text.Length > MaxSelectionLength) return null;
            if (!process.WaitForExit(100)) { process.Kill(); return null; }
            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            if (started && !process.HasExited) process.Kill();
            return null;
        }
    }

    private static bool IsDown(byte[] keys, int keycode) => keycode > 0 && keycode < 256 &&
        (keys[keycode / 8] & (1 << (keycode % 8))) != 0;

    private static string Normalize(string name)
    {
        string value = name.Trim().Trim('"');
        int slash = value.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) value = value[(slash + 1)..];
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }

    public void Dispose()
    {
        lock (lifecycle)
        {
            cancellation?.Cancel();
            if (worker != Thread.CurrentThread) worker?.Join(1500);
            worker = null;
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XClassHint { public IntPtr ResName; public IntPtr ResClass; }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XKeysymToKeycode(IntPtr display, int keysym);
    [DllImport("libX11.so.6")] private static extern int XQueryPointer(IntPtr display, IntPtr window,
        out IntPtr root, out IntPtr child, out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);
    [DllImport("libX11.so.6")] private static extern int XQueryKeymap(IntPtr display, [Out] byte[] keys);
    [DllImport("libX11.so.6")] private static extern int XGetInputFocus(IntPtr display, out IntPtr window, out int revertTo);
    [DllImport("libX11.so.6")] private static extern int XGetClassHint(IntPtr display, IntPtr window, out XClassHint hint);
    [DllImport("libX11.so.6")] private static extern int XFetchName(IntPtr display, IntPtr window, out IntPtr name);
    [DllImport("libX11.so.6")] private static extern IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern IntPtr XGetSelectionOwner(IntPtr display, IntPtr atom);
    [DllImport("libX11.so.6")] private static extern int XQueryTree(IntPtr display, IntPtr window,
        out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr value);
}
