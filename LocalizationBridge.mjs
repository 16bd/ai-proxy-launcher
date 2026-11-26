import { spawnSync } from "node:child_process";
import { copyFile, mkdtemp, readFile, rename, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { sha256File } from "./src/lib/hash.mjs";
import { readFileFromAsar } from "./src/lib/asar-reader.mjs";
import { localizationFingerprint } from "./src/lib/language-pack.mjs";
import { patchCustomSchemeSource } from "./src/lib/patcher.mjs";
import { getAntigravityUserDataRoot, getStateRoot } from "./src/lib/paths.mjs";
import { readEmbeddedWindowsUi } from "./src/lib/runtime-bundle.mjs";
import { getInstallStatePath, readInstallState, writeInstallState } from "./src/lib/state.mjs";
import { loadDomTranslations } from "./src/lib/translator.mjs";
import { getSettingsPath, writeUserSettings } from "./src/lib/user-settings.mjs";

const core = path.dirname(fileURLToPath(import.meta.url));
const action = process.argv[2];
const installRoot = process.env.AGY_INSTALL_PATH;
const samePath = (left, right) => typeof left === "string" &&
  path.resolve(left).toLowerCase() === path.resolve(right).toLowerCase();
const closedMessage = "Antigravity 正在运行，请彻底退出后重试。";
const asar = path.join(installRoot ?? "", "resources", "app.asar");
const bundle = path.join(getAntigravityUserDataRoot(), "agy_zhcn_ui_main.js");

function replaceOnce(source, original, adapted) {
  const index = source.indexOf(original);
  if (index < 0 || source.indexOf(original, index + original.length) >= 0)
    throw new Error("上游汉化组件的结构不符，已停止。");
  return source.slice(0, index) + adapted + source.slice(index + original.length);
}

async function adaptProcessCheck() {
  const target = path.join(core, "src", "lib", "close-client.mjs");
  const original = await readFile(target, "utf8");
  // 上游检查可能清理子进程，启动器模式使用同一探测逻辑，仅查询正在运行的进程。
  const signature = "export async function clientProcesses() {";
  const readOnly = signature + '\n  if (process.platform === "win32") return inspectWindowsRawProcesses();';
  await writeFile(target, replaceOnce(original, signature, readOnly), "utf8");
}

async function adaptInstall() {
  const target = path.join(core, "scripts", "auto-follow-update.mjs");
  let source = await readFile(target, "utf8");
  // 保留上游的结构校验、备份、原子替换及状态记录，只收窄启动与更新行为。
  source = replaceOnce(source, 'const noLaunch = process.argv.includes("--no-launch");', "const noLaunch = true;");
  source = replaceOnce(source,
    "const syncAutoFollow = async () => applyAutoFollowSetting((await readUserSettings()).settings).catch(() => {});",
    "const syncAutoFollow = async () => {};");
  source = replaceOnce(source,
    "return (await readEmbeddedWindowsUi(inspection)) ?? (await waitForRuntimeUi(inspection.executablePath, setLabel));",
    'const embedded = await readEmbeddedWindowsUi(inspection);\n    if (!embedded) throw new Error("未找到可校验的内置界面资源，无法离线汉化此版本。");\n    return embedded;');
  const reinstall =
    '    info("汉化包有更新，需要先恢复原版文件，再重新应用中文。");\n' +
    '    process.env.AGY_UI_RERUN = "1";\n' +
    '    const restored = spawnSync(process.execPath, [path.join(projectRoot, "src/cli.mjs"), "restore"], { stdio: "inherit", windowsHide: true });\n' +
    '    if (restored.status !== 0) throw new Error("未完成恢复，已停止更新。");\n' +
    '    const updated = spawnSync(process.execPath, [path.join(projectRoot, "scripts/auto-follow-update.mjs")], { stdio: "inherit", windowsHide: true });\n' +
    '    process.exit(updated.status ?? 1);';
  source = replaceOnce(source, reinstall, '    throw new Error("已有汉化与当前词库不一致，请先恢复英文，再安装中文。");');
  source = replaceOnce(source, "await gracefulCloseAntigravity({ owned: closeOwnedMode });",
    "if ((await clientProcesses()).length) throw new Error(" + JSON.stringify(closedMessage) + ");");
  await writeFile(target, source, "utf8");
  return [target];
}

async function adaptRestore() {
  const target = path.join(core, "src", "cli.mjs");
  let source = await readFile(target, "utf8");
  // 复用上游针对原版备份的版本和哈希检查，避免对正在使用的客户端发起关闭操作。
  source = replaceOnce(source, "  await closeClientForChanges();",
    '  const { clientProcesses } = await import("./lib/close-client.mjs");\n' +
    "  if ((await clientProcesses()).length) throw new Error(" + JSON.stringify(closedMessage) + ");");
  source = replaceOnce(source,
    'uiStep("卸载汉化，还原官方英文原版");\n  const inspection = await inspectInstallation();',
    'uiStep("卸载汉化，还原官方英文原版");\n  const inspection = await inspectInstallation({ allowRemoteCompatibility: false });');
  const cleanupStart = "  const daemon = await removeAutoFollowDaemon().catch(() => ({ removed: false }));\n" +
    "  await removePackHookMetadata().catch(() => {});\n" +
    '  if (daemon.removed) uiOk("已清理旧版的后台计划任务");';
  source = replaceOnce(source, cleanupStart,
    "  await removePackHookMetadata().catch(() => {});");
  await writeFile(target, source, "utf8");
  return [target, "restore"];
}

async function verify(install) {
  const state = await readInstallState();
  if (!state || !samePath(state.installRoot, installRoot) ||
      state.status !== (install ? "installed" : "restored")) {
    throw new Error("操作结束后没有找到匹配的汉化状态，需检查安装结果。");
  }
  if (await sha256File(asar) !== (install ? state.patchedAsarSha256 : state.originalAsarSha256))
    throw new Error("操作完成后的应用资源校验失败。");
  if (install) {
    if (!samePath(bundle, state.installedBundlePath) || await sha256File(bundle) !== state.localizedBundleSha256)
      throw new Error("中文界面资源校验失败。");
    if (await sha256File(state.backupPath) !== state.originalAsarSha256)
      throw new Error("原版资源备份校验失败。");
  }
  return state;
}

function runWorker(args) {
  const result = spawnSync(process.execPath, args, { windowsHide: true, stdio: ["ignore", "pipe", "pipe"], encoding: "utf8" });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    const detail = result.stderr.split(/\r?\n/).find(line => line.startsWith("操作失败:") || line.startsWith("Error:"));
    throw new Error(detail ?? "上游汉化组件执行失败，退出码 " + result.status + "。");
  }
}

async function installOffline(args, release) {
  // 客户端读取此文件，补译、标题翻译和后台更新都保持关闭。
  await writeUserSettings({
    autoFollow: "off", packAutoUpdate: "off", onlineTranslate: false,
    titleAutoTranslate: false, titleWriteBack: false,
  });
  runWorker(args);
  const state = await verify(true);
  await writeInstallState({ ...state, launcherRelease: release });
}

async function upgradeOffline(state, args, restoreArgs, release, clientProcesses) {
  // 在改动旧版前先验证原始备份、客户端内置界面和新词库的兼容性。
  await verify(true);
  patchCustomSchemeSource((await readFileFromAsar(state.backupPath, "dist/customScheme.js")).toString("utf8"));
  if (!await readEmbeddedWindowsUi({ appAsarPath: asar }, { log: () => {} }))
    throw new Error("未找到可校验的内置界面资源，无法离线升级。");
  await loadDomTranslations();

  // 暂存旧版可用的汉化文件，升级失败时原位恢复，原版备份继续留存。
  const snapshot = await mkdtemp(path.join(getStateRoot(), "launcher-upgrade-"));
  const targets = [asar, bundle, getSettingsPath(),
    path.join(getAntigravityUserDataRoot(), "agy_zhcn_pack_hook.json"), getInstallStatePath()];
  const copies = await Promise.all(targets.map(async (target, index) => {
    const saved = path.join(snapshot, String(index));
    const exists = await copyFile(target, saved).then(() => true, error => {
      if (error.code !== "ENOENT" || index === 0 || index === 1 || index === 4) throw error;
      return false;
    });
    return { target, saved, exists };
  }));
  try {
    runWorker(restoreArgs);
    await verify(false);
    await installOffline(args, release);
  } catch (error) {
    try {
      const current = await readInstallState();
      const hash = await sha256File(asar);
      const expectedHash = hash === state.originalAsarSha256 || hash === state.patchedAsarSha256 ||
        (samePath(current?.installRoot, installRoot) && current.originalAsarSha256 === state.originalAsarSha256 &&
          hash === current.patchedAsarSha256);
      if (!expectedHash || (await clientProcesses()).length)
        throw new Error("Antigravity 在升级期间被启动或被其他程序改动。");
      for (const { target, saved, exists } of copies) {
        if (!exists) {
          await rm(target, { force: true });
          continue;
        }
        const tmp = target + "." + process.pid + ".launcher-rollback";
        try {
          await copyFile(saved, tmp);
          await rename(tmp, target);
        } finally {
          await rm(tmp, { force: true }).catch(() => {});
        }
      }
      await verify(true);
    } catch (rollback) {
      throw new Error("升级失败，无法恢复旧版。快照位置 " + snapshot + "。原因 " + rollback.message);
    }
    await rm(snapshot, { recursive: true, force: true }).catch(() => {});
    throw new Error("新版未能安装，已恢复原有汉化。" + error.message);
  }
  await rm(snapshot, { recursive: true, force: true }).catch(() => {});
}

try {
  if (process.platform !== "win32" || !installRoot || !["install", "restore"].includes(action))
    throw new Error("汉化操作参数不完整。");
  delete process.env.AGY_SILENT_LAUNCH;
  delete process.env.AGY_UI_RERUN;
  await adaptProcessCheck();
  const { clientProcesses } = await import("./src/lib/close-client.mjs");
  if ((await clientProcesses()).length)
    throw new Error(closedMessage);
  const install = action === "install";
  const state = await readInstallState();
  if (!install && !(state?.status === "installed" && samePath(state.installRoot, installRoot)))
    throw new Error("没有找到此安装可恢复的汉化记录。");
  // 安装前同时确认新版保留恢复入口，避免安装以后才发现结构不兼容。
  const restoreArgs = await adaptRestore();
  if (install) {
    const release = JSON.parse(process.env.AGY_LAUNCHER_RELEASE ?? "null");
    if (!release?.tag) throw new Error("缺少已验证的汉化包版本记录。");
    const args = await adaptInstall();
    const previous = state?.status === "installed" && samePath(state.installRoot, installRoot)
      && await sha256File(asar) === state.patchedAsarSha256;
    if (previous && state.toolFingerprint !== await localizationFingerprint("win32"))
      await upgradeOffline(state, args, restoreArgs, release, clientProcesses);
    else
      await installOffline(args, release);
  } else {
    runWorker(restoreArgs);
    await verify(false);
  }
} catch (error) {
  console.error("启动器汉化：" + error.message);
  process.exitCode = 1;
}
