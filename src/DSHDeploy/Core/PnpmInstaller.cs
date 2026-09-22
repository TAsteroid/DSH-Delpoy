using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace DSHDeploy.Core
{
    /// <summary>
    /// Installs pnpm. Three escalating strategies, cheapest first: npm global install (mirror
    /// aware), the Corepack shim that ships with modern Node, and finally the standalone
    /// Windows executable. A working pnpm is never replaced.
    /// </summary>
    public static class PnpmInstaller
    {
        public static bool Install(SourcePlan plan, IUi ui)
        {
            if (IsReady())
            {
                Log.Info("pnpm 已可用, 跳过安装");
                return true;
            }

            ui.SetStatus("正在安装 pnpm");

            if (InstallViaNpm(plan, ui))
            {
                if (Verify()) { ui.SetProgress(60); return true; }
            }

            if (InstallViaCorepack(ui))
            {
                if (Verify()) { ui.SetProgress(60); return true; }
            }

            if (InstallStandalone(plan, ui))
            {
                if (Verify()) { ui.SetProgress(60); return true; }
            }

            Log.Error("pnpm 安装失败: 三种方式均未成功");
            return false;
        }

        public static bool IsReady()
        {
            string path = Which.Find("pnpm");
            if (path == null) return false;
            RunResult result = Proc.Run(path, "--version", null, null, 30000);
            SemVer ignored;
            return result.Ok && SemVer.TryParseFromOutput(result.StdOut, out ignored);
        }

        private static bool Verify()
        {
            PathSync.RefreshProcessPath();
            string path = Which.Find("pnpm");
            if (path == null)
            {
                Log.Error("安装后仍未在 PATH 中找到 pnpm");
                return false;
            }
            RunResult result = Proc.Run(path, "--version", null, null, 30000);
            Log.Info("pnpm 版本: {0}", result.StdOut);
            return result.Ok;
        }

        private static bool InstallViaNpm(SourcePlan plan, IUi ui)
        {
            string npm = Which.Find("npm");
            if (npm == null)
            {
                Log.Warn("未找到 npm, 无法通过 npm 安装 pnpm");
                return false;
            }

            foreach (string registry in plan.NpmHosts)
            {
                Log.Info("使用 registry {0} 通过 npm 安装 pnpm", registry);
                var environment = new Dictionary<string, string>
                {
                    { "npm_config_registry", registry },
                    { "npm_config_fund", "false" },
                    { "npm_config_audit", "false" }
                };
                RunResult result = Proc.Run(npm, "install -g pnpm", null, environment, 900000);
                if (result.Ok)
                {
                    Log.Info("npm 安装 pnpm 成功");
                    return true;
                }
                Log.Warn("registry {0} 安装 pnpm 失败 (退出码 {1})", registry, result.ExitCode);
                if (!string.IsNullOrEmpty(result.All)) Log.Debug("npm 输出: {0}", result.All);
            }
            return false;
        }

        private static bool InstallViaCorepack(IUi ui)
        {
            string corepack = Which.Find("corepack");
            if (corepack == null)
            {
                Log.Warn("未找到 corepack");
                return false;
            }
            ui.SetStatus("正在通过 corepack 启用 pnpm");
            RunResult result = Proc.Run(corepack, "enable pnpm --install-directory \"" + NpmGlobalDirectory() + "\"", null, null, 300000);
            if (result.Ok)
            {
                Log.Info("corepack 启用 pnpm 成功");
                return true;
            }
            // Older corepack builds do not accept --install-directory.
            result = Proc.Run(corepack, "enable pnpm", null, null, 300000);
            if (result.Ok)
            {
                Log.Info("corepack 启用 pnpm 成功");
                return true;
            }
            Log.Warn("corepack 启用 pnpm 失败 (退出码 {0}): {1}", result.ExitCode, result.All);
            return false;
        }

        private static bool InstallStandalone(SourcePlan plan, IUi ui)
        {
            ui.SetStatus("正在下载 pnpm 独立版");
            string target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH", "pnpm");

            // Build the candidate list from the measured pnpm mirror order, then fall back to the
            // curated one so a measurement cannot remove a source that still works.
            var urls = new List<string>();
            foreach (string host in plan.NpmHosts)
            {
                AddUnique(urls, Sources.PnpmStandaloneUrlForHost(host));
            }
            foreach (string host in Sources.DefaultHosts(SourceKind.PnpmMirror, Region.Global))
            {
                AddUnique(urls, Sources.PnpmStandaloneUrlForHost(host));
            }
            AddUnique(urls, Sources.PnpmStandaloneUrlForHost(Sources.GitHubRoot));

            string archive;
            try
            {
                archive = Downloader.DownloadFile(urls, target, Sources.PnpmStandaloneFileName(), null, ui);
            }
            catch (DownloadFailure failure)
            {
                Log.Error("pnpm 独立版下载失败:{0}{1}", Environment.NewLine, failure.AttemptDetail());
                return false;
            }

            // The release ships a zip containing pnpm.exe, so the archive must be unpacked.
            try
            {
                using (var zip = ZipFile.OpenRead(archive))
                {
                    ZipArchiveEntry executable = null;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith("pnpm.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            executable = entry;
                            break;
                        }
                    }
                    if (executable == null)
                    {
                        Log.Error("pnpm 独立版压缩包中未找到 pnpm.exe: {0}", archive);
                        return false;
                    }
                    executable.ExtractToFile(Path.Combine(target, "pnpm.exe"), true);
                }
                Log.Info("pnpm 独立版已解压到 {0}", target);
            }
            catch (Exception ex)
            {
                Log.Error("解压 pnpm 独立版失败: {0}", ex.Message);
                return false;
            }

            PathSync.AddToUserPath(target);
            PathSync.RefreshProcessPath();
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (path.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0)
            {
                Environment.SetEnvironmentVariable("PATH", target + ";" + path);
            }
            return true;
        }

        private static void AddUnique(List<string> urls, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!urls.Contains(url)) urls.Add(url);
        }

        /// <summary>The directory npm symlinks global bins into (<c>%APPDATA%\npm</c> by default).</summary>
        public static string NpmGlobalDirectory()
        {
            string prefix = Environment.GetEnvironmentVariable("PREFIX");
            if (!string.IsNullOrWhiteSpace(prefix)) return prefix;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        }
    }
}
