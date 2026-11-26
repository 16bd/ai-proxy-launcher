using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class AntigravityLocalization
{
    private const string LatestUrl = "https://api.github.com/repos/Ace2025001/antigravity-desktop-zhcn/releases/latest";
    private const string DownloadRoot = "https://github.com/Ace2025001/antigravity-desktop-zhcn/releases/download/";
    private const string AssetName = "Antigravity-Windows+Mac.zip";
    private const int MetadataLimit = 1024 * 1024;
    private const long ArchiveLimit = 256L * 1024 * 1024;
    // 旧的测试版未记录所用的包，只在恢复或迁移已验证的旧缓存时使用这一组摘要。
    private static readonly Package Legacy = new Package("v3.1.9",
        "sha256:20b4bd349e08a99da40836a7225025d14acbf2f10ee4e2cf867bb9761067e2be", 73615348);
    private const string PackageRoot = "antigravity汉化包/";
    private const string Language = PackageRoot + "语言包/";
    private const string Runtime = PackageRoot + "win启动/配套程序/";
    private readonly string cache;

    internal AntigravityLocalization(string cache)
    {
        this.cache = Path.GetFullPath(cache);
    }

    internal static AntigravityLocalization ForCurrentUser()
    {
        return new AntigravityLocalization(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIProxyLauncher", "AntigravityZhcn"));
    }

    private string LegacyCache { get { return Path.Combine(cache, Legacy.Tag); } }

    private static string InstallStateFile
    {
        get
        {
            string root = Environment.GetEnvironmentVariable("AGY_ZHCN_STATE_DIR");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntigravityZhcn");
            return Path.Combine(root, "install-state.json");
        }
    }

    internal bool HasRestoreState { get { return File.Exists(InstallStateFile); } }

    internal bool HasRestorePackage
    {
        get { return HasRestoreState && File.Exists(PackagePath(ReadInstalledPackage())); }
    }

    internal string Apply(string exe, string proxy, bool install, IProgress<string> progress)
    {
        return ApplyPackage(exe, proxy, install, progress, null);
    }

    private string ApplyPackage(string exe, string proxy, bool install, IProgress<string> progress, Package package)
    {
        Directory.CreateDirectory(cache);
        using (FileStream operation = AcquireLock())
        {
            if (LaunchTarget.DesktopIsRunning(Service.All[0], exe))
                throw new InvalidOperationException("请先彻底退出 Antigravity，再进行汉化或恢复。");
            if (package == null)
            {
                if (install)
                {
                    progress.Report("正在检查上游最新正式版汉化包…");
                    package = CheckLatest(proxy);
                }
                else
                    package = ReadInstalledPackage();
            }
            string archive = PackagePath(package);
            if (!IsVerifiedPackage(archive, package))
                DownloadPackage(archive, proxy, progress, package);
            else if (install)
                progress.Report("已校验 " + package.Tag + " 汉化包缓存。");

            // 每次从验过哈希的原始归档解包，避免执行上一次留下的临时程序。
            string staging = Path.Combine(cache, "run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                progress.Report("正在校验并解包 Windows 汉化组件…");
                ExtractWindowsPackage(staging, archive, package);
                progress.Report(install ? "正在离线安装中文并校验备份…" : "正在校验备份并恢复英文…");
                RunBridge(staging, exe, install, package);
            }
            finally
            {
                // 这里只清理本次创建的直接子目录，汉化状态与原版备份均由上游保留。
                if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(staging)), cache, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(staging, true); }
                    catch (IOException) {}
                    catch (UnauthorizedAccessException) {}
                }
            }
        }
        return package.Tag;
    }

    private FileStream AcquireLock()
    {
        // 与此前固定版本的启动器共用锁，两个测试版同时运行时只允许一次修改。
        Directory.CreateDirectory(LegacyCache);
        try { return new FileStream(Path.Combine(LegacyCache, "operation.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None); }
        catch (IOException error) { throw new InvalidOperationException("另一项 Antigravity 汉化操作正在进行。", error); }
    }

    private static Package CheckLatest(string proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy))
            throw new InvalidOperationException("请先启动本地代理，再检查最新汉化包。");
        var request = CreateRequest(new Uri(LatestUrl), proxy);
        request.Accept = "application/vnd.github+json";
        request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new IOException("GitHub 未返回最新正式版信息。");
            var text = new char[MetadataLimit + 1];
            int length = reader.ReadBlock(text, 0, text.Length);
            if (length > MetadataLimit)
                throw new IOException("汉化包发布信息超出预期大小。");
            ReleaseMetadata release;
            try { release = new JavaScriptSerializer().Deserialize<ReleaseMetadata>(new string(text, 0, length)); }
            catch (ArgumentException error) { throw new IOException("GitHub 汉化包发布信息格式无效。", error); }
            if (release == null || release.draft != false || release.prerelease != false || release.assets == null)
                throw new IOException("没有查到有效的正式版汉化包。");
            var assets = release.assets.Where(asset => asset != null && asset.name == AssetName && asset.state == "uploaded").ToArray();
            if (assets.Length != 1)
                throw new IOException("上游正式版未提供可识别的完整汉化包。");
            return new Package(release.tag_name, assets[0].digest, assets[0].size);
        }
    }

    private static Package ReadInstalledPackage()
    {
        if (!File.Exists(InstallStateFile))
            throw new InvalidOperationException("没有找到可恢复的 Antigravity 汉化记录。");
        var state = new JavaScriptSerializer().Deserialize<InstallMetadata>(File.ReadAllText(InstallStateFile, Encoding.UTF8));
        if (state == null || state.status != "installed")
            throw new InvalidOperationException("没有找到可恢复的 Antigravity 汉化记录。");
        // 恢复只使用安装时的工具版本，避免新版变更恢复协议。
        var installed = state.launcherRelease;
        return installed == null ? Legacy : new Package(installed.tag, installed.digest, installed.size);
    }

    private string PackagePath(Package package)
    {
        if (package.Tag == Legacy.Tag && package.Hash == Legacy.Hash && package.Size == Legacy.Size)
            return Path.Combine(LegacyCache, "upstream.zip");
        return Path.Combine(cache, package.Tag + "-" + package.Hash.Substring(0, 16), "upstream.zip");
    }

    private static bool IsVerifiedPackage(string file, Package package)
    {
        if (!File.Exists(file) || new FileInfo(file).Length != package.Size)
            return false;
        using (FileStream stream = File.OpenRead(file))
            return HasExpectedHash(stream, package);
    }

    private static bool HasExpectedHash(Stream stream, Package package)
    {
        using (var sha = SHA256.Create())
        {
            string digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            return string.Equals(digest, package.Hash, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HttpWebRequest CreateRequest(Uri address, string proxy)
    {
        var request = (HttpWebRequest)WebRequest.Create(address);
        request.Proxy = new WebProxy(proxy);
        request.UserAgent = "AIProxyLauncher-Zhcn";
        request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
        request.AllowAutoRedirect = false;
        request.Timeout = 20000;
        request.ReadWriteTimeout = 20000;
        return request;
    }

    private static HttpWebResponse OpenAsset(Package package, string proxy)
    {
        var address = new Uri(DownloadRoot + package.Tag + "/" + Uri.EscapeDataString(AssetName));
        for (int count = 0; count < 5; count++)
        {
            var response = (HttpWebResponse)CreateRequest(address, proxy).GetResponse();
            if (response.StatusCode == HttpStatusCode.OK)
                return response;
            string location = response.Headers[HttpResponseHeader.Location];
            response.Dispose();
            Uri next;
            // 资产下载跟随 GitHub 的 HTTPS CDN 跳转，拒绝离开官方资产域。
            if (location == null || !Uri.TryCreate(address, location, out next) ||
                next.Scheme != Uri.UriSchemeHttps || !next.IsDefaultPort || next.UserInfo.Length != 0 ||
                !(next.Host == "release-assets.githubusercontent.com" || next.Host == "objects.githubusercontent.com"))
                throw new IOException("GitHub 的汉化包下载地址不在预期范围内。");
            address = next;
        }
        throw new IOException("汉化包下载重定向次数过多。");
    }

    private static void DownloadPackage(string archive, string proxy, IProgress<string> progress, Package package)
    {
        if (proxy == null)
            throw new InvalidOperationException("需要下载或重新校验汉化包，请启动本地代理后重试。");
        Directory.CreateDirectory(Path.GetDirectoryName(archive));
        string pending = archive + ".download";
        try
        {
            progress.Report("正在通过自动识别的代理下载 " + package.Tag + " 汉化包…");
            using (var response = OpenAsset(package, proxy))
            using (Stream input = response.GetResponseStream())
            using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // 发行包版本和长度固定，下载中即限制大小，完成后才校验并启用。
                if (response.ContentLength >= 0 && response.ContentLength != package.Size)
                    throw new IOException("GitHub 返回的汉化包下载信息与预期不符。");
                var buffer = new byte[65536];
                int count;
                int lastPercent = 0;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (output.Length + count > package.Size)
                        throw new IOException("汉化包超出预期大小。");
                    output.Write(buffer, 0, count);
                    int percent = (int)(output.Length * 100 / package.Size);
                    if (percent >= lastPercent + 8)
                    {
                        lastPercent = percent;
                        progress.Report("正在下载汉化包 " + percent + "%…");
                    }
                }
            }
            if (!IsVerifiedPackage(pending, package))
                throw new IOException("汉化包的 SHA-256 校验失败。");
            if (File.Exists(archive))
                File.Replace(pending, archive, null);
            else
                File.Move(pending, archive);
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
        }
    }

    private static void ExtractWindowsPackage(string staging, string archive, Package package)
    {
        string prefix = staging + Path.DirectorySeparatorChar;
        using (FileStream file = File.OpenRead(archive))
        {
            // 下载与执行时再次校验同一个只读句柄，避免解包时换入其他内容。
            if (file.Length != package.Size || !HasExpectedHash(file, package))
                throw new IOException("汉化包缓存校验失败。");
            file.Position = 0;
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
            {
                long totalBytes = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (entry.Name.Length == 0 || !(entry.FullName.StartsWith(Language, StringComparison.Ordinal) ||
                        entry.FullName.StartsWith(Runtime, StringComparison.Ordinal)))
                        continue;
                    if (entry.Length > ArchiveLimit - totalBytes)
                        throw new IOException("解包后的 Windows 汉化组件超出预期大小。");
                    totalBytes += entry.Length;
                    string relative = entry.FullName.Substring(PackageRoot.Length).Replace('/', Path.DirectorySeparatorChar);
                    string full = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || relative.Contains(":"))
                        throw new IOException("汉化包包含无效的解包路径。");
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    using (Stream input = entry.Open())
                    using (var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                }
            }
        }
    }

    private static void RunBridge(string staging, string exe, bool install, Package package)
    {
        string node = Path.Combine(staging, "win启动", "配套程序", "node.exe");
        string language = Path.Combine(staging, "语言包");
        string core = Path.Combine(language, "核心程序");
        string bridge = Path.Combine(core, "launcher-localization.mjs");
        if (!File.Exists(node) || !Directory.Exists(core))
            throw new IOException("汉化包缺少 Windows 运行时或核心文件。");
        using (Stream embedded = typeof(AntigravityLocalization).Assembly.GetManifestResourceStream("LocalizationBridge.mjs"))
        using (var target = new FileStream(bridge, FileMode.CreateNew, FileAccess.Write))
            embedded.CopyTo(target);

        var worker = new ProcessStartInfo(node, "\"" + bridge + "\" " + (install ? "install" : "restore"))
        {
            WorkingDirectory = core,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // 只给这次离线操作指定已定位的安装目录，不启动上游更新、翻译或快捷方式入口。
        worker.EnvironmentVariables["AGY_LANGUAGE_PACK"] = language;
        worker.EnvironmentVariables["AGY_INSTALL_PATH"] = Path.GetDirectoryName(exe);
        worker.EnvironmentVariables["AGY_LAUNCHER_RELEASE"] = new JavaScriptSerializer().Serialize(new InstalledRelease
        {
            tag = package.Tag, digest = "sha256:" + package.Hash, size = package.Size
        });
        worker.EnvironmentVariables["TEMP"] = staging;
        worker.EnvironmentVariables["TMP"] = staging;
        worker.EnvironmentVariables.Remove("NODE_OPTIONS");
        worker.EnvironmentVariables.Remove("NODE_PATH");
        using (Process process = Process.Start(worker))
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(output, errors);
            if (process.ExitCode != 0)
            {
                string details = (errors.Result + "\n" + output.Result).Split('\n')
                    .Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("启动器汉化：", StringComparison.Ordinal));
                throw new InvalidOperationException(details ?? "汉化组件运行失败，退出码 " + process.ExitCode + "。");
            }
        }
    }

    private sealed class Package
    {
        internal readonly string Tag, Hash;
        internal readonly long Size;

        internal Package(string tag, string digest, long size)
        {
            if (tag == null || !Regex.IsMatch(tag, @"\Av[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}\z") ||
                digest == null || !Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z") || size <= 0 || size > ArchiveLimit)
                throw new IOException("汉化包的版本、大小或 SHA-256 摘要无效。");
            Tag = tag;
            Hash = digest.Substring(7).ToLowerInvariant();
            Size = size;
        }
    }

    private sealed class ReleaseMetadata
    {
        public string tag_name { get; set; }
        public bool? draft { get; set; }
        public bool? prerelease { get; set; }
        public ReleaseFile[] assets { get; set; }
    }

    private sealed class ReleaseFile
    {
        public string name { get; set; }
        public string state { get; set; }
        public string digest { get; set; }
        public long size { get; set; }
    }

    private sealed class InstallMetadata
    {
        public string status { get; set; }
        public InstalledRelease launcherRelease { get; set; }
    }

    private sealed class InstalledRelease
    {
        public string tag { get; set; }
        public string digest { get; set; }
        public long size { get; set; }
    }
}
