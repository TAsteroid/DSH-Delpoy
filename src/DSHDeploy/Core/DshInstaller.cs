using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DSHDeploy.Core
{
    /// <summary>
    /// Installs or upgrades the <c>dsh</c> CLI through npm.
    ///
    /// Version policy: npm's <c>latest</c> tag for this package currently resolves to an older
    /// release than the published <c>alpha</c>, and the plugin market requires the newer web
    /// primitives. The deployer therefore targets <see cref="DshRelease.Spec"/> by default and
    /// never downgrades an installation that is already newer.
    /// </summary>
    public static class DshInstaller
    {
        public static bool Ensure(SourcePlan plan, IUi ui, out string installedVersion, out SemVer resolvedTarget)
        {
            installedVersion = null;
            resolvedTarget = null;

            string existingPath = Which.Find("dsh");
            SemVer existing = existingPath == null ? null : InstalledVersion(existingPath);
            SemVer candidate = ResolveCandidate(plan);
            resolvedTarget = candidate;

            if (existingPath != null)
            {
                installedVersion = existing == null ? null : existing.ToString();
                if (existing != null && candidate != null && !EnvChecker.ShouldInstall(existing, candidate))
                {
                    Log.Info("dsh 已安装且不旧于目标版本 (现有 {0}, 目标 {1}), 保持不变", existing, candidate);
                    ui.SetStatus("dsh 已就绪 " + existing);
                    return true;
                }
                Log.Info("dsh 需要安装/升级: 现有 {0}, 目标 {1}",
                    existing == null ? "(未知)" : existing.ToString(),
                    candidate == null ? DshRelease.Spec : candidate.ToString());
            }
            else
            {
                Log.Info("未检测到 dsh, 准备安装");
            }

            ui.SetStatus("正在安装 dsh");
            string spec = "@deepseek-ai/dsh@" + DshRelease.Spec;
            string npm = Which.Find("npm");
            if (npm == null)
            {
                Log.Error("未找到 npm, 无法安装 dsh (请先安装 Node.js)");
                return false;
            }

            foreach (string registry in plan.NpmHosts)
            {
                Log.Info("使用 registry {0} 安装 {1}", registry, spec);
                var environment = new Dictionary<string, string>
                {
                    { "npm_config_registry", registry },
                    { "npm_config_fund", "false" },
                    { "npm_config_audit", "false" }
                };
                RunResult result = Proc.Run(npm, "install -g " + spec, null, environment, 1800000);
                if (result.Ok)
                {
                    PathSync.RefreshProcessPath();
                    SemVer now = InstalledVersion(Which.Find("dsh"));
                    installedVersion = now == null ? null : now.ToString();
                    Log.Info("dsh 安装完成: {0}", installedVersion ?? "(版本未识别)");
                    ui.SetProgress(75);
                    return true;
                }
                Log.Warn("registry {0} 安装 dsh 失败 (退出码 {1})", registry, result.ExitCode);
                if (!string.IsNullOrEmpty(result.All)) Log.Warn("npm 输出: {0}", result.All);
            }

            Log.Error("dsh 安装失败: 所有 registry 均未成功");
            return false;
        }

        /// <summary>The version the configured dist-tag currently points at, or null when offline.</summary>
        public static SemVer ResolveCandidate(SourcePlan plan)
        {
            // An explicit version (DSH_DEPLOY_DSH_VERSION=0.1.7-alpha.1) needs no registry lookup.
            if (Regex.IsMatch(DshRelease.Spec, @"^\d+\.\d+\.\d+"))
            {
                SemVer exact;
                if (SemVer.TryParse(DshRelease.Spec, out exact)) return exact;
            }

            foreach (string registry in plan.NpmHosts)
            {
                string url = registry + "/@deepseek-ai/dsh/" + DshRelease.Spec;
                string body = Downloader.TryGetString(url, TimeSpan.FromSeconds(25));
                if (string.IsNullOrEmpty(body)) continue;
                string version = Json.GetString(body, "version");
                SemVer parsed;
                if (SemVer.TryParse(version, out parsed))
                {
                    Log.Info("目标版本 {0} = {1}", DshRelease.Spec, parsed);
                    return parsed;
                }
            }
            Log.Warn("无法解析 {0} 对应的版本号, 将按现有情况决定是否安装", DshRelease.Spec);
            return null;
        }

        /// <summary>Read <c>dsh --version</c> for an already resolved executable.</summary>
        public static SemVer InstalledVersion(string dshPath)
        {
            if (string.IsNullOrEmpty(dshPath)) return null;
            RunResult result = Proc.Run(dshPath, "--version", null, null, 60000);
            SemVer parsed;
            if (SemVer.TryParseFromOutput(result.StdOut, out parsed)) return parsed;
            if (SemVer.TryParseFromOutput(result.StdErr, out parsed)) return parsed;
            return null;
        }
    }
}
