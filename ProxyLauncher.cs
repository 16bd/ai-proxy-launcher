using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // 连通性探测使用 TLS 1.2，证书继续交由 Windows 验证。
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Application.Run(new LauncherForm());
    }
}

internal sealed class Service
{
    internal static readonly Service[] All = {
        new Service("antigravity", "Antigravity", "https://www.google.com/generate_204",
            "Antigravity", @"Programs\antigravity\Antigravity.exe", @"Antigravity\Antigravity.exe"),
        new Service("chatgpt", "ChatGPT", "https://chatgpt.com/robots.txt",
            "ChatGPT", @"Programs\ChatGPT\ChatGPT.exe", @"Programs\OpenAI\ChatGPT.exe",
            @"Programs\OpenAI\ChatGPT\ChatGPT.exe", @"ChatGPT\ChatGPT.exe"),
        new Service("claude", "Claude", "https://claude.ai/robots.txt",
            "Claude", @"Programs\Claude\Claude.exe", @"AnthropicClaude\Claude.exe",
            @"Programs\AnthropicClaude\Claude.exe")
    };

    internal readonly string Id, Name, CheckUrl, ProcessName;
    internal readonly string[] InstallPaths;

    private Service(string id, string name, string checkUrl, string processName,
        params string[] installPaths)
    {
        Id = id;
        Name = name;
        CheckUrl = checkUrl;
        ProcessName = processName;
        InstallPaths = installPaths;
    }
}

internal static class Network
{
    internal static string Parse(string value)
    {
        string input = (value ?? "").Trim();
        if (!input.Contains("://"))
            input = "http://" + input;
        Uri uri;
        // 一个明确的 HTTP 代理地址可直接用于应用参数，分隔符会改变 Chromium 的代理规则。
        if (!Regex.IsMatch(input, @"\Ahttp://(?:[a-z0-9._-]+|\[[0-9a-f:.]+\]):[0-9]{1,5}/?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || !Uri.TryCreate(input, UriKind.Absolute, out uri) || uri.Port <= 0)
            return null;
        return "http://" + input.Substring(7).TrimEnd('/');
    }

    internal static async Task<string> DetectAsync()
    {
        var addresses = new List<string>();
        string configured = null;
        try
        {
            // Windows 关闭系统代理后仍会保留原地址，可优先验证已有的本地客户端端口。
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                configured = key == null ? null : key.GetValue("ProxyServer") as string;
        }
        catch (System.Security.SecurityException) {}
        foreach (string candidate in (configured ?? "").Split(';'))
        {
            int equals = candidate.IndexOf('=');
            string address = Parse(candidate.Substring(equals + 1));
            if (address != null && new Uri(address).IsLoopback)
                addresses.Add(address);
        }
        addresses.AddRange(new[] {
            "http://127.0.0.1:10808", "http://127.0.0.1:7890",
            "http://127.0.0.1:7897", "http://127.0.0.1:10809"
        });
        string[] targets = addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // 同时探测常见端口，仍按优先顺序选择第一个确实能完成 HTTPS 请求的代理。
        Task<bool>[] attempts = targets.Select(address => Task.Run(
            () => Check(address, Service.All[0], 4500).Ok)).ToArray();
        for (int i = 0; i < targets.Length; i++)
            if (await attempts[i])
                return targets[i];
        return null;
    }

    internal static ProbeResult Check(string proxy, Service app, int timeout = 10000)
    {
        try
        {
            // 仅向公开地址发送请求，显式指定代理并禁用跳转，便于区分网络与站点响应。
            var request = (HttpWebRequest)WebRequest.Create(app.CheckUrl);
            request.Proxy = new WebProxy(new Uri(proxy));
            request.AllowAutoRedirect = false;
            request.Timeout = timeout;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                int code = (int)response.StatusCode;
                return code >= 200 && code < 300 && (app.Id != "antigravity" || code == 204)
                    ? new ProbeResult(true, "连通")
                    : new ProbeResult(false, "HTTP " + code);
            }
        }
        catch (WebException error)
        {
            using (var response = error.Response as HttpWebResponse)
            {
                if (response != null)
                    return new ProbeResult(false, "HTTP " + (int)response.StatusCode);
            }
            if (error.Status == WebExceptionStatus.Timeout)
                return new ProbeResult(false, "连接超时");
            if (error.Status == WebExceptionStatus.TrustFailure || error.Status == WebExceptionStatus.SecureChannelFailure)
                return new ProbeResult(false, "TLS 连接失败");
            return new ProbeResult(false, "连接失败");
        }
    }
}

internal sealed class ProbeResult
{
    internal readonly bool Ok;
    internal readonly string Text;

    internal ProbeResult(bool ok, string text)
    {
        Ok = ok;
        Text = text;
    }
}

internal static class LaunchTarget
{
    private const string LocalHosts = "localhost,127.0.0.1,::1";
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string ProfileLocal = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.UserProfile), "AppData", "Local");
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    internal static bool IsExecutable(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)
            && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path);
    }

    internal static string FindDesktop(Service app)
    {
        // 读取 Windows 当前用户的软件包登记，更新版本后也能定位真实的 EXE。
        string[] families = app.Id == "chatgpt"
            ? new[] { "OpenAI.Codex_2p2nqsd0c76g0", "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0" }
            : app.Id == "claude"
                ? new[] { "AnthropicPBC.Claude_fnn82j28hfe8t", "Claude_pzs8sxrjxfjjc" }
                : new string[0];
        foreach (string family in families)
        {
            string installed = FindPackagedExe(family, app.ProcessName + ".exe");
            if (installed != null)
                return installed;
        }
        foreach (string root in new[] { Local, ProfileLocal, ProgramFiles, ProgramFilesX86 })
        {
            string found = app.InstallPaths.Select(relative => Path.Combine(root, relative))
                .FirstOrDefault(IsExecutable);
            if (found != null)
                return found;
        }
        if (app.Id == "claude")
        {
            // 兼容旧版 Claude 的版本目录，跳过已经移除或不可访问的安装。
            foreach (string root in new[] { Local, ProfileLocal })
            {
                string installed = Path.Combine(root, "AnthropicClaude");
                if (!Directory.Exists(installed))
                    continue;
                try
                {
                    string old = Directory.GetDirectories(installed, "app-*")
                        .OrderByDescending(Directory.GetLastWriteTimeUtc)
                        .Select(dir => Path.Combine(dir, "Claude.exe")).FirstOrDefault(IsExecutable);
                    if (old != null)
                        return old;
                }
                catch (IOException) {}
                catch (UnauthorizedAccessException) {}
            }
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count,
        [Out] IntPtr[] names, ref uint bufferSize, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(string package, ref uint pathSize, StringBuilder path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int PackageFamilyNameFromFullName(string fullName, ref uint length, StringBuilder family);

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out int processId);
    }

    internal static string GetPackageActivationId(string executable)
    {
        if (executable.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) < 0)
            return null;
        string folder = Path.GetDirectoryName(executable);
        string manifestPath = Path.Combine(folder, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            folder = Path.GetDirectoryName(folder);
            manifestPath = Path.Combine(folder, "AppxManifest.xml");
        }
        if (!File.Exists(manifestPath))
            throw new IOException("找不到桌面应用的软件包清单。");

        string fullName = Path.GetFileName(folder);
        uint nameLength = 0;
        if (PackageFamilyNameFromFullName(fullName, ref nameLength, null) != 122)
            throw new InvalidOperationException("Windows 无法读取桌面应用的软件包身份。");
        var family = new StringBuilder((int)nameLength);
        if (PackageFamilyNameFromFullName(fullName, ref nameLength, family) != 0)
            throw new InvalidOperationException("Windows 无法读取桌面应用的软件包身份。");
        var manifest = new XmlDocument { XmlResolver = null };
        manifest.Load(manifestPath);
        // 从实际定位的 EXE 反查清单中的应用 ID，避免依赖启动别名或写死版本路径。
        foreach (XmlElement entry in manifest.SelectNodes(
            "/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']"))
        {
            string declared = entry.GetAttribute("Executable").Replace('/', '\\');
            if (string.Equals(Path.Combine(folder, declared), executable, StringComparison.OrdinalIgnoreCase))
                return family + "!" + entry.GetAttribute("Id");
        }
        throw new InvalidOperationException("软件包清单中未登记该桌面应用的启动入口。");
    }

    internal static void ActivatePackage(string appId, string arguments)
    {
        // 通过 Windows 保留软件包身份，激活失败时用 HRESULT 报告系统错误。
        var activationType = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true);
        var activation = (IApplicationActivationManager)Activator.CreateInstance(activationType);
        try
        {
            int processId;
            int result = activation.ActivateApplication(appId, arguments, 2, out processId);
            if (result != 0)
                throw new InvalidOperationException("Windows 激活桌面应用失败（0x" + result.ToString("X8") + "）。");
        }
        finally
        {
            Marshal.FinalReleaseComObject(activation);
        }
    }

    private static string FindPackagedExe(string family, string executable)
    {
        const int InsufficientBuffer = 122;
        uint count = 0, size = 0;
        if (GetPackagesByPackageFamily(family, ref count, null, ref size, IntPtr.Zero) != InsufficientBuffer
            || count == 0 || size == 0)
            return null;
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)size * 2));
        try
        {
            var names = new IntPtr[count];
            if (GetPackagesByPackageFamily(family, ref count, names, ref size, buffer) != 0)
                return null;
            // 一个家族可能同时登记新旧版本，优先选最新且实际存在的主程序。
            foreach (string name in names.Take((int)count).Select(pointer => Marshal.PtrToStringUni(pointer))
                .OrderByDescending(full => new Version(full.Split('_')[1])))
            {
                uint pathSize = 0;
                if (GetPackagePathByFullName(name, ref pathSize, null) != InsufficientBuffer)
                    continue;
                var root = new StringBuilder((int)pathSize);
                if (GetPackagePathByFullName(name, ref pathSize, root) != 0)
                    continue;
                foreach (string folder in new[] { "app", "" })
                {
                    string path = Path.Combine(root.ToString(), folder, executable);
                    if (IsExecutable(path))
                        return path;
                }
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool DesktopIsRunning(Service app, string path)
    {
        int session;
        using (var current = Process.GetCurrentProcess())
            session = current.SessionId;
        // 桌面应用一般是单实例，已有进程会沿用此前的网络选项。
        foreach (string name in new[] { app.ProcessName, Path.GetFileNameWithoutExtension(path) }
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var matches = Process.GetProcessesByName(name);
            try
            {
                if (matches.Any(process => process.SessionId == session && !process.HasExited))
                    return true;
            }
            finally
            {
                foreach (Process process in matches)
                    process.Dispose();
            }
        }
        return false;
    }

    internal static ProcessStartInfo Desktop(Service app, string path, string proxy)
    {
        // 桌面渲染进程读取 Chromium 参数，后台服务可以继承环境变量，本地通信保留回环直连。
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(path),
            Arguments = ProxyArguments(proxy)
        };
        foreach (string name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
            info.EnvironmentVariables[name] = proxy;
        info.EnvironmentVariables["NO_PROXY"] = LocalHosts;
        if (app.Id == "chatgpt")
            info.EnvironmentVariables["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = info.Arguments;
        return info;
    }

    internal static string ProxyArguments(string proxy)
    {
        return "--proxy-server=" + proxy + " --proxy-bypass-list=localhost;127.0.0.1;[::1]";
    }
}
