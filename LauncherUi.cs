using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class LauncherStyle
{
    internal static readonly Color Surface = Color.FromArgb(244, 247, 250);
    internal static readonly Color White = Color.White;
    internal static readonly Color Text = Color.FromArgb(22, 33, 50);
    internal static readonly Color Muted = Color.FromArgb(69, 84, 105);
    internal static readonly Color Blue = Color.FromArgb(29, 78, 161);
    internal static readonly Color Line = Color.FromArgb(190, 204, 221);

    internal static Label Label(string value, float size, bool bold = false)
    {
        return new Label
        {
            Text = value, ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Text, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            Dock = DockStyle.Fill, Margin = Padding.Empty
        };
    }

    internal static Button Button(string title, bool primary = false)
    {
        var button = new Button
        {
            Text = title, Size = new Size(112, 40), FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand, Anchor = AnchorStyles.Right,
            Margin = new Padding(8, 0, 0, 0), UseVisualStyleBackColor = false
        };
        // 高对比度模式交还给系统绘制，保留键盘焦点和系统主题颜色。
        if (SystemInformation.HighContrast)
        {
            button.FlatStyle = FlatStyle.System;
            button.UseVisualStyleBackColor = true;
            return button;
        }

        Action update = () =>
        {
            button.BackColor = !button.Enabled ? Color.FromArgb(235, 239, 245) : primary ? Blue : White;
            button.ForeColor = primary ? White : Text;
            button.FlatAppearance.BorderColor = primary && button.Enabled ? Blue : Line;
        };
        update();
        button.EnabledChanged += (sender, args) => update();
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(27, 65, 134) : Surface;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(22, 56, 112) : Line;
        return button;
    }

    internal static TableLayoutPanel Band()
    {
        // 单个操作入口即可覆盖整行，给代理状态和应用名称留足显示空间。
        var band = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 2, Dock = DockStyle.Fill,
            Padding = new Padding(15, 8, 15, 8), Margin = new Padding(0, 0, 0, 9),
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : White
        };
        band.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        band.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        band.RowStyles.Add(new RowStyle(SizeType.Percent, 53));
        band.RowStyles.Add(new RowStyle(SizeType.Percent, 47));
        return band;
    }
}

internal sealed class LauncherForm : Form
{
    private string proxy;
    private readonly string[] programs = new string[Service.All.Length];
    private readonly ProbeResult[] results = new ProbeResult[Service.All.Length];
    private readonly Button[] nativeButtons = new Button[Service.All.Length];
    private readonly Label[] lines = new Label[Service.All.Length];
    private readonly Label status = LauncherStyle.Label("", 9);
    private readonly Label proxyLine = LauncherStyle.Label("", 10);
    private readonly Button test = LauncherStyle.Button("测试连接");
    private bool busy;

    internal LauncherForm()
    {
        Text = "AI 代理启动器";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : LauncherStyle.Surface;
        ForeColor = SystemColors.WindowText;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(655, 474);
        MinimumSize = new Size(565, 493);
        MaximizeBox = false;

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(24, 18, 24, 14)
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 64, 74, 73, 73, 73, 34 })
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(page);

        var title = LauncherStyle.Label(Text, 23, true);
        title.Margin = new Padding(0, 0, 0, 9);
        page.Controls.Add(title, 0, 0);

        var shared = LauncherStyle.Band();
        shared.BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(228, 237, 250);
        shared.Controls.Add(LauncherStyle.Label("共享代理", 10, true), 0, 0);
        shared.Controls.Add(proxyLine, 0, 1);
        test.Click += async (sender, args) => await RunAsync(TestAsync);
        shared.Controls.Add(test, 1, 0);
        shared.SetRowSpan(test, 2);
        page.Controls.Add(shared, 0, 1);

        for (int i = 0; i < Service.All.Length; i++)
        {
            int index = i;
            Service app = Service.All[i];
            var row = LauncherStyle.Band();
            row.Controls.Add(LauncherStyle.Label(app.Name, 14, true), 0, 0);
            var description = LauncherStyle.Label("", 9);
            description.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : LauncherStyle.Muted;
            row.Controls.Add(description, 0, 1);
            lines[i] = description;
            var start = LauncherStyle.Button("启动桌面版", true);
            start.AccessibleName = "启动 " + app.Name + " 桌面版";
            start.Click += async (sender, args) => await RunAsync(() => LaunchAsync(index));
            row.Controls.Add(start, 1, 0);
            row.SetRowSpan(start, 2);
            nativeButtons[i] = start;
            page.Controls.Add(row, 0, i + 2);
        }

        status.AccessibleName = "操作状态";
        page.Controls.Add(status, 0, 5);
        var help = LauncherStyle.Label("保持代理客户端运行。已打开的桌面应用请先彻底退出，再从这里启动。", 9);
        help.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : LauncherStyle.Muted;
        page.Controls.Add(help, 0, 6);

        RefreshTargets();
        Shown += async (sender, args) => await RunAsync(async () => { await ResolveProxyAsync(); });
    }

    private void RefreshTargets()
    {
        for (int i = 0; i < programs.Length; i++)
            programs[i] = LaunchTarget.FindDesktop(Service.All[i]);
        Render();
    }

    private void Render()
    {
        proxyLine.Text = proxy != null
            ? proxy + "  ·  自动识别"
            : busy ? "正在识别代理地址…" : "未找到可用的本地 HTTP 代理";
        test.Enabled = !busy;
        for (int i = 0; i < nativeButtons.Length; i++)
        {
            bool found = programs[i] != null;
            var result = results[i];
            string availability = found ? "桌面版已识别" : "未安装或未检测到桌面版";
            lines[i].Text = result == null ? availability : availability + "  ·  " + result.Text;
            nativeButtons[i].Enabled = !busy && found;
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy)
            return;
        busy = true;
        Render();
        try
        {
            await action();
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException ||
                error is InvalidOperationException || error is Win32Exception || error is ArgumentException ||
                error is COMException || error is System.Xml.XmlException))
                throw;
            status.Text = "操作失败，" + error.Message;
            // 具体系统错误可能比状态栏长，提示框保留完整原因。
            MessageBox.Show(this, error.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            busy = false;
            if (!IsDisposed)
                Render();
        }
    }

    private async Task<bool> ResolveProxyAsync()
    {
        // 代理地址由当前的本地监听探测得到，旧版保存的手动配置不参与选择。
        proxy = await Network.DetectAsync();
        if (proxy == null)
            status.Text = "请启动代理客户端并启用 HTTP 或混合端口，然后重新测试。";
        else if (status.Text.StartsWith("请启动", StringComparison.Ordinal))
            status.Text = "";
        Render();
        return proxy != null;
    }

    private async Task TestAsync()
    {
        status.Text = "正在测试三个服务的 HTTPS 连通性…";
        Array.Clear(results, 0, results.Length);
        RefreshTargets();
        if (!await ResolveProxyAsync())
            return;
        string address = proxy;
        ProbeResult[] checks = await Task.WhenAll(Service.All.Select(app => Task.Run(() => Network.Check(address, app))));
        Array.Copy(checks, results, checks.Length);
        int passed = checks.Count(result => result.Ok);
        status.Text = "连通性测试完成，" + passed + " / " + checks.Length + " 个探测地址可用。";
    }

    private async Task LaunchAsync(int index)
    {
        Service app = Service.All[index];
        if (proxy == null && !await ResolveProxyAsync())
            return;
        // 启动前重新定位已更新的程序，Store 刚更新时也使用当前安装路径。
        string exe = LaunchTarget.FindDesktop(app);
        if (exe == null)
        {
            status.Text = "没有找到 " + app.Name + " 桌面版。";
            RefreshTargets();
            return;
        }
        if (LaunchTarget.DesktopIsRunning(app, exe))
        {
            status.Text = "请从托盘彻底退出 " + app.Name + "，再启动桌面版。";
            return;
        }
        string package = LaunchTarget.GetPackageActivationId(exe);
        using (var launched = package == null
            ? Process.Start(LaunchTarget.Desktop(app, exe, proxy))
            : Process.GetProcessById(LaunchTarget.ActivatePackage(package, LaunchTarget.ProxyArguments(proxy))))
        {
            if (launched == null)
                throw new InvalidOperationException("Windows 未能创建 " + app.Name + " 进程。");
            // 安装包可能允许创建进程，但在初始化阶段立即退出。
            bool exited = await Task.Run(() => launched.WaitForExit(1600));
            if (exited && launched.ExitCode != 0)
            {
                status.Text = app.Name + " 桌面版启动失败（退出码 " + launched.ExitCode + "）。";
                return;
            }
        }
        status.Text = "已发起 " + app.Name + " 桌面版启动  ·  " + proxy;
    }
}
