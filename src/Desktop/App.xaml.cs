using System;
using System.Diagnostics;
using System.Windows;

namespace AiSelectionToolbar.Desktop
{
    public partial class App : Application
    {
        private IntegratedHost host;
        private System.Windows.Forms.NotifyIcon tray;
        private System.Drawing.Icon appIcon;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            try
            {
                var window = new MainWindow();
                host = new IntegratedHost();
                host.Connect(window);
                MainWindow = window;
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("显示工具栏", null, (sender, args) => Dispatcher.BeginInvoke(new Action(() => {
                    window.Show(); window.ShowExpanded(); window.Activate();
                })));
                menu.Items.Add("设置和历史", null, (sender, args) => Dispatcher.BeginInvoke(new Action(window.OpenManagement)));
                menu.Items.Add("退出", null, (sender, args) => Dispatcher.BeginInvoke(new Action(window.Close)));
                appIcon = System.Drawing.Icon.ExtractAssociatedIcon(
                    Process.GetCurrentProcess().MainModule.FileName);
                tray = new System.Windows.Forms.NotifyIcon {
                    Text = "AI 划词工具栏", Icon = appIcon,
                    ContextMenuStrip = menu, Visible = true
                };
                tray.DoubleClick += (sender, args) => Dispatcher.BeginInvoke(new Action(() => {
                    window.Show(); window.ShowExpanded(); window.Activate();
                }));
                if (host.IsConfigured) window.Opacity = 0;
                else window.ShowExpanded();
                window.Show();
                if (host.IsConfigured) { window.Hide(); window.Opacity = 1; }
                if (!host.HotkeyAvailable)
                    MessageBox.Show(window, "Ctrl+Shift+Space 已被其他程序占用；鼠标划词仍可使用。",
                        "快捷键不可用", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show("无法启动 AI 划词工具栏：" + exception.Message, "启动失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (appIcon != null) appIcon.Dispose();
            if (host != null) host.Dispose();
            base.OnExit(e);
        }
    }
}
