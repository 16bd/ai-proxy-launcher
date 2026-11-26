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
    // 从 Logo 取绿色主调，按钮用较深的绿保证白字清晰，面板使用浅绿区分层次。
    internal static readonly Color Surface = Color.FromArgb(243, 248, 245);
    internal static readonly Color AccentSurface = Color.FromArgb(227, 244, 233);
    internal static readonly Color White = Color.White;
    internal static readonly Color Text = Color.FromArgb(22, 42, 34);
    internal static readonly Color Muted = Color.FromArgb(73, 100, 87);
    internal static readonly Color Accent = Color.FromArgb(8, 114, 69);
    internal static readonly Color AccentHover = Color.FromArgb(8, 97, 60);
    internal static readonly Color AccentPressed = Color.FromArgb(6, 79, 50);
    internal static readonly Color Line = Color.FromArgb(112, 144, 128);
    internal static readonly Color Disabled = Color.FromArgb(233, 239, 235);

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

    internal static Button Button(string title, bool primary = false, int width = 112)
    {
        var button = new Button
        {
            Text = title, Size = new Size(width, 40), FlatStyle = FlatStyle.Flat,
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
            button.BackColor = !button.Enabled ? Disabled : primary ? Accent : White;
            button.ForeColor = primary ? White : Text;
            button.FlatAppearance.BorderColor = primary && button.Enabled ? Accent : Line;
        };
        update();
        button.EnabledChanged += (sender, args) => update();
        button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Surface;
        button.FlatAppearance.MouseDownBackColor = primary ? AccentPressed : AccentSurface;
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
    private string detectedProxy;
    private readonly string[] programs = new string[Service.All.Length];
    private readonly ProbeResult[] results = new ProbeResult[Service.All.Length];
    private readonly Button[] nativeButtons = new Button[Service.All.Length];
    private readonly Label[] lines = new Label[Service.All.Length];
    private readonly Label status = LauncherStyle.Label("", 9);
    private readonly TextBox proxyAddress = new TextBox
    {
        AccessibleName = "共享代理地址", Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = Padding.Empty
    };
    private readonly Label proxyHint = LauncherStyle.Label("", 9);
    private readonly Button test = LauncherStyle.Button("测试连接");
    private readonly Button installChinese = LauncherStyle.Button("安装中文", width: 84);
    private readonly Button restoreEnglish = LauncherStyle.Button("恢复英文", width: 84);
    private readonly AntigravityLocalization localization = AntigravityLocalization.ForCurrentUser();
    private bool busy;
    private bool changingLanguage;

    internal LauncherForm()
    {
        Text = "AI 代理启动器";
        Icon = Icon.ExtractAssociatedIcon(typeof(LauncherForm).Assembly.Location);
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : LauncherStyle.Surface;
        ForeColor = SystemColors.WindowText;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(655, 500);
        MinimumSize = new Size(565, 519);
        MaximizeBox = false;

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(24, 18, 24, 14)
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 64, 100, 73, 73, 73, 34 })
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(page);

        // 标题和窗口共用 EXE 内的 ICO，Logo 换版时只维护一份图标资源。
        var header = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 9)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Bitmap logoImage = Icon.ToBitmap();
        var logo = new PictureBox
        {
            Image = logoImage, Size = new Size(36, 36),
            SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 12, 0), AccessibleName = "启动器 Logo"
        };
        logo.Disposed += (sender, args) => logoImage.Dispose();
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(LauncherStyle.Label(Text, 23, true), 1, 0);
        page.Controls.Add(header, 0, 0);

        var shared = LauncherStyle.Band();
        shared.BackColor = SystemInformation.HighContrast ? SystemColors.Window : LauncherStyle.AccentSurface;
        shared.RowCount = 3;
        shared.RowStyles.Clear();
        foreach (int height in new[] { 30, 40, 30 })
            shared.RowStyles.Add(new RowStyle(SizeType.Percent, height));
        shared.Controls.Add(LauncherStyle.Label("共享代理（留空时自动识别）", 10, true), 0, 0);
        shared.Controls.Add(proxyAddress, 0, 1);
        shared.Controls.Add(proxyHint, 0, 2);
        proxyAddress.TextChanged += (sender, args) =>
        {
            // 地址变化后清除旧结果，程序回填时保留自动识别状态。
            if (proxyAddress.Text != detectedProxy)
            {
                detectedProxy = null;
                status.Text = "";
            }
            Array.Clear(results, 0, results.Length);
            Render();
            UpdateProxyHint();
        };
        proxyAddress.Leave += (sender, args) => UpdateProxyHint(true);
        UpdateProxyHint();
        test.Click += async (sender, args) => await RunAsync(TestAsync);
        shared.Controls.Add(test, 1, 0);
        shared.SetRowSpan(test, 3);
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
            Control actions = start;
            if (app.Id == "antigravity")
            {
                // 汉化按钮只属于 Antigravity，其他应用沿用原来的桌面启动入口。
                var group = new FlowLayoutPanel
                {
                    AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty
                };
                installChinese.Click += async (sender, args) => await RunAsync(() => ChangeLanguageAsync(true));
                restoreEnglish.Click += async (sender, args) => await RunAsync(() => ChangeLanguageAsync(false));
                group.Controls.Add(installChinese);
                group.Controls.Add(restoreEnglish);
                group.Controls.Add(start);
                actions = group;
            }
            row.Controls.Add(actions, 1, 0);
            row.SetRowSpan(actions, 2);
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
        // 同一次测试、下载或启动期间固定输入地址，避免操作结果与显示地址不一致。
        proxyAddress.ReadOnly = busy;
        test.Enabled = !busy;
        installChinese.Enabled = restoreEnglish.Enabled = !busy && programs[0] != null;
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

    private void UpdateProxyHint(bool validate = false)
    {
        string input = proxyAddress.Text;
        if (!string.IsNullOrWhiteSpace(input) && input == detectedProxy)
            proxyHint.Text = "已自动识别，可修改地址。";
        else if (validate && !string.IsNullOrWhiteSpace(input) && Network.Parse(input) == null)
            proxyHint.Text = "地址格式有误，例如 127.0.0.1:7890";
        else
            proxyHint.Text = "填写 HTTP 或混合端口，例如 127.0.0.1:7890";
    }

    private async Task<string> ResolveProxyAsync(bool refresh = false)
    {
        string input = proxyAddress.Text;
        // 手动地址直接交给当前操作，格式不合法时保留原输入供用户修正。
        if (!string.IsNullOrWhiteSpace(input) && input != detectedProxy)
        {
            string manual = Network.Parse(input);
            if (manual == null)
            {
                UpdateProxyHint(true);
                status.Text = "请修正共享代理地址后重试。";
            }
            return manual;
        }
        if (refresh || string.IsNullOrEmpty(detectedProxy))
        {
            proxyHint.Text = "正在识别代理地址…";
            string address = await Network.DetectAsync();
            if (IsDisposed)
                return null;
            detectedProxy = address ?? "";
            proxyAddress.Text = detectedProxy;
            UpdateProxyHint();
        }
        if (!string.IsNullOrEmpty(detectedProxy))
            return detectedProxy;
        status.Text = "未自动识别到代理，请填写客户端的 HTTP 或混合端口。";
        return null;
    }

    private async Task TestAsync()
    {
        status.Text = "正在测试三个服务的 HTTPS 连通性…";
        Array.Clear(results, 0, results.Length);
        RefreshTargets();
        string address = await ResolveProxyAsync(true);
        if (address == null)
            return;
        ProbeResult[] checks = await Task.WhenAll(Service.All.Select(app => Task.Run(() => Network.Check(address, app))));
        Array.Copy(checks, results, checks.Length);
        int passed = checks.Count(result => result.Ok);
        status.Text = "连通性测试完成，" + passed + " / " + checks.Length + " 个探测地址可用。";
    }

    private async Task ChangeLanguageAsync(bool install)
    {
        Service app = Service.All[0];
        string exe = LaunchTarget.FindDesktop(app);
        if (exe == null)
        {
            status.Text = "没有找到 Antigravity 桌面版。";
            RefreshTargets();
            return;
        }
        if (LaunchTarget.DesktopIsRunning(app, exe))
        {
            status.Text = "请彻底退出 Antigravity，再安装中文或恢复英文。";
            return;
        }
        if (!install && !localization.HasRestoreState)
        {
            status.Text = "没有找到此电脑的 Antigravity 汉化记录。";
            return;
        }
        // 需要联网时优先用手填地址。有缓存时允许离线恢复，也为缓存校验失败时保留下载地址。
        bool needsProxy = install || !localization.HasRestorePackage;
        string address = needsProxy ? await ResolveProxyAsync(true) : Network.Parse(proxyAddress.Text);
        if (needsProxy && address == null)
            return;
        status.Text = "正在准备 Antigravity 汉化组件…";
        var progress = new Progress<string>(message => { if (!IsDisposed) status.Text = message; });
        changingLanguage = true;
        string version;
        try
        {
            version = await Task.Run(() => localization.Apply(exe, address, install, progress));
        }
        finally
        {
            changingLanguage = false;
        }
        if (install)
        {
            // 上游组件只处理本地汉化，完成后沿用当前共享代理启动桌面版。
            status.Text = "中文已安装，正在带代理启动 Antigravity…";
            await LaunchAsync(0);
            status.Text = "中文 " + version + " 已安装。 " + status.Text;
        }
        else
            status.Text = "已恢复 Antigravity 官方英文资源。";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 等待汉化进程完成原子替换和校验，避免关闭窗口时提前释放操作锁。
        if (changingLanguage && e.CloseReason == CloseReason.UserClosing)
            e.Cancel = true;
        base.OnFormClosing(e);
    }

    private async Task LaunchAsync(int index)
    {
        Service app = Service.All[index];
        string address = await ResolveProxyAsync();
        if (address == null)
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
            ? Process.Start(LaunchTarget.Desktop(app, exe, address))
            : Process.GetProcessById(LaunchTarget.ActivatePackage(package, LaunchTarget.ProxyArguments(address))))
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
        status.Text = "已发起 " + app.Name + " 桌面版启动  ·  " + address;
    }
}
