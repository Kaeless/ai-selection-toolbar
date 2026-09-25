using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AiSelectionToolbar.Desktop
{
    internal static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AiSelectionToolbar.Desktop";

        public static bool IsEnabledForCurrentExecutable()
        {
            try
            {
                var command = "\"" + Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName) + "\"";
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return key != null && string.Equals(key.GetValue(ValueName) as string,
                        command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                var executable = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
                if (!File.Exists(executable) ||
                    !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("无法找到当前桌面程序的可执行文件。");
                // Windows Run values have a 260-character command-line limit.
                // Quote the entire path so portable installs under folders with spaces work.
                var command = "\"" + executable + "\"";
                if (command.Length > 260)
                    throw new ArgumentException("程序路径过长，无法加入 Windows 登录启动项。");
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) throw new InvalidOperationException("无法打开当前用户的登录启动项。");
                    key.SetValue(ValueName, command, RegistryValueKind.String);
                }
            }
            else
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (key != null) key.DeleteValue(ValueName, false);
            }
        }
    }
}
