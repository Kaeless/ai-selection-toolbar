using Gtk;

namespace AiSelectionToolbar.Linux;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
            string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("当前 Linux 版本需要 X11 会话。请在登录界面选择 Xorg，并安装 xclip。");
            return 2;
        }
        try
        {
            Application.Init();
            using var host = new LinuxHost();
            using var selection = new LinuxSelectionService(host.LoadSettings().ExcludedApplications);
            using var window = new LinuxWindow(host, selection);
            selection.Start();
            Application.Run();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("启动失败：" + error.Message);
            return 1;
        }
    }
}
