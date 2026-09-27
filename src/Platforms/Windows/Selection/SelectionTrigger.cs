using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace AiSelectionToolbar.Selection
{
    public enum SelectionTriggerKind
    {
        MouseReleased,
        Hotkey
    }

    public sealed class SelectionRequestedEventArgs : EventArgs
    {
        public SelectionRequestedEventArgs(SelectionTriggerKind kind, bool hasMousePosition, int mouseX, int mouseY)
            : this(kind, hasMousePosition, mouseX, mouseY, 0, 0)
        {
        }

        public SelectionRequestedEventArgs(SelectionTriggerKind kind, bool hasMousePosition,
            int mouseX, int mouseY, int mouseDownX, int mouseDownY)
        {
            Kind = kind;
            HasMousePosition = hasMousePosition;
            MouseX = mouseX;
            MouseY = mouseY;
            MouseDownX = mouseDownX;
            MouseDownY = mouseDownY;
        }

        public SelectionTriggerKind Kind { get; private set; }
        public bool HasMousePosition { get; private set; }
        public int MouseX { get; private set; }
        public int MouseY { get; private set; }
        public int MouseDownX { get; private set; }
        public int MouseDownY { get; private set; }
    }

    /// <summary>
    /// Owns a Win32 message thread for the mouse hook and hotkey. SelectionRequested
    /// runs on a ThreadPool thread; handlers must marshal any UI work to the UI thread.
    /// </summary>
    public sealed class SelectionTrigger : IDisposable
    {
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;

        private const int WhMouseLl = 14;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmHotkey = 0x0312;
        private const int WmQuit = 0x0012;
        private const int HotkeyId = 1;

        private readonly Thread messageThread;
        private readonly ManualResetEventSlim started = new ManualResetEventSlim(false);
        private readonly uint modifiers;
        private readonly uint virtualKey;
        private readonly HookProcedure mouseProcedure;
        private Exception startError;
        private IntPtr hook;
        private uint threadId;
        private int pending;
        private int disposed;
        private volatile bool hotkeyAvailable;
        private POINT mouseDown;
        private bool mousePressed;
        private int dragWidth;
        private int dragHeight;

        /// <param name="modifiers">Win32 MOD_* flags; defaults to Ctrl+Shift.</param>
        /// <param name="virtualKey">Win32 virtual key; defaults to Space.</param>
        public SelectionTrigger(uint modifiers = ModControl | ModShift, uint virtualKey = 0x20)
        {
            this.modifiers = modifiers;
            this.virtualKey = virtualKey;
            mouseProcedure = OnMouse;
            messageThread = new Thread(RunMessageLoop);
            messageThread.IsBackground = true;
            messageThread.Name = "Selection input hooks";
            messageThread.Start();
            started.Wait();
            if (startError != null)
            {
                started.Dispose();
                throw new InvalidOperationException("Could not start selection input hooks.", startError);
            }
        }

        public event EventHandler<SelectionRequestedEventArgs> SelectionRequested;
        public bool HotkeyAvailable { get { return hotkeyAvailable; } }

        private void RunMessageLoop()
        {
            bool hotkeyRegistered = false;
            try
            {
                // GetMessage creates the message queue before the constructor can return.
                MSG message;
                PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
                threadId = GetCurrentThreadId();
                dragWidth = Math.Max(1, GetSystemMetrics(68)); // SM_CXDRAG
                dragHeight = Math.Max(1, GetSystemMetrics(69)); // SM_CYDRAG
                hook = SetWindowsHookEx(WhMouseLl, mouseProcedure, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                hotkeyRegistered = RegisterHotKey(IntPtr.Zero, HotkeyId, modifiers, virtualKey);
                hotkeyAvailable = hotkeyRegistered;
                started.Set();

                int result;
                while ((result = GetMessage(out message, IntPtr.Zero, 0, 0)) > 0)
                {
                    if (message.message == WmHotkey && message.wParam == (IntPtr)HotkeyId)
                        QueueRequest(SelectionTriggerKind.Hotkey);
                }
            }
            catch (Exception exception)
            {
                startError = exception;
                started.Set();
            }
            finally
            {
                if (hotkeyRegistered)
                    UnregisterHotKey(IntPtr.Zero, HotkeyId);
                if (hook != IntPtr.Zero)
                    UnhookWindowsHookEx(hook);
            }
        }

        private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            try
            {
                if (code >= 0 && (message == (IntPtr)WmLButtonDown || message == (IntPtr)WmLButtonUp))
                {
                    POINT point = ((MSLLHOOKSTRUCT)Marshal.PtrToStructure(data, typeof(MSLLHOOKSTRUCT))).pt;
                    if (message == (IntPtr)WmLButtonDown)
                    {
                        mouseDown = point;
                        mousePressed = true;
                    }
                    else
                    {
                        bool dragged = mousePressed &&
                            (Math.Abs((long)point.x - mouseDown.x) > dragWidth ||
                             Math.Abs((long)point.y - mouseDown.y) > dragHeight);
                        mousePressed = false;
                        if (dragged)
                            QueueRequest(SelectionTriggerKind.MouseReleased, true, point, mouseDown);
                    }
                }
            }
            catch (Exception) { mousePressed = false; }
            return CallNextHookEx(hook, code, message, data);
        }

        private void QueueRequest(SelectionTriggerKind kind, bool hasMousePosition = false,
            POINT point = default(POINT), POINT down = default(POINT))
        {
            if (Interlocked.CompareExchange(ref pending, 1, 0) != 0)
                return;

            if (Interlocked.CompareExchange(ref disposed, 0, 0) != 0)
            {
                Interlocked.Exchange(ref pending, 0);
                return;
            }

            try { ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    if (Interlocked.CompareExchange(ref disposed, 0, 0) == 0)
                    {
                        // A low-level mouse hook runs before the target handles button-up.
                        // Let the target finish updating its selection first.
                        if (kind == SelectionTriggerKind.MouseReleased)
                            Thread.Sleep(50);
                        if (Interlocked.CompareExchange(ref disposed, 0, 0) != 0)
                            return;

                        EventHandler<SelectionRequestedEventArgs> handler = SelectionRequested;
                        if (handler != null)
                        {
                            try { handler(this, new SelectionRequestedEventArgs(kind, hasMousePosition,
                                point.x, point.y, down.x, down.y)); }
                            catch (Exception) { /* An observer cannot terminate a ThreadPool worker. */ }
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref pending, 0);
                }
            }); }
            catch (Exception)
            {
                Interlocked.Exchange(ref pending, 0);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            if (Thread.CurrentThread != messageThread)
                messageThread.Join();
            started.Dispose();
        }

        private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT point;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr extraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookType, HookProcedure procedure, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out MSG message, IntPtr window, uint min, uint max, uint remove);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);
    }
}
