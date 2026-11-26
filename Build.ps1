param([string]$Destination = (Join-Path $PSScriptRoot 'AIProxyLauncher.exe'))

$ErrorActionPreference = 'Stop'
# 使用 Windows 自带的 .NET Framework 编译器，产物运行时无需额外安装依赖。
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$binary = [IO.Path]::GetFullPath($Destination)
$sources = 'ProxyLauncher.cs', 'LauncherUi.cs', 'AntigravityLocalization.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
$icon = Join-Path $PSScriptRoot 'Launcher.ico'
$bridge = Join-Path $PSScriptRoot 'LocalizationBridge.mjs'
# 桥接脚本随 EXE 构建，点击安装时查询上游版本并按摘要选择下载或缓存。
& $compiler /nologo /warnaserror+ /target:winexe /optimize+ /codepage:65001 "/out:$binary" "/win32icon:$icon" `
    "/resource:$bridge,LocalizationBridge.mjs" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /reference:System.IO.Compression.dll /reference:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）" }
