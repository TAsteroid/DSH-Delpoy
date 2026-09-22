using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DSHDeploy.Core
{
    /// <summary>
    /// The whole deployment as a linear, resumable sequence. Both the wizard and <c>--headless</c>
    /// drive this, so the two paths can never drift apart.
    /// </summary>
    public sealed class Deployment
    {
        private readonly Options _options;
        private readonly IUi _ui;
        private readonly Outcome _outcome = new Outcome();
        private SourcePlan _plan;

        public Deployment(Options options, IUi ui)
        {
            _options = options;
            _ui = ui;
        }

        public Outcome Outcome { get { return _outcome; } }

        public Outcome Run()
        {
            _outcome.LogPath = Log.Path;

            if (!Launcher.IsElevated())
            {
                // The manifest requests elevation; reaching here means it was bypassed.
                Log.Warn("当前进程未获得管理员权限, 部分安装步骤可能失败");
            }

            // ---------------------------------------------------------------- step 1: detect
            // Detection comes first and is cheap, because it decides whether a speed test is
            // needed at all: on a machine that already has everything, probing every mirror
            // would just add seconds of waiting for downloads that never happen.
            _ui.SetStatus("正在检测前置环境");
            _ui.SetProgress(5);
            EnvChecker.Snapshot snapshot = EnvChecker.Detect();
            ReportSnapshot(snapshot);

            bool needsEnvironmentWork = snapshot.Node.NeedsAction
                                     || snapshot.Git.NeedsAction
                                     || snapshot.Pnpm.NeedsAction
                                     || snapshot.Dsh.NeedsAction;
            bool marketReady = _options.NoMarket
                            || MarketInstaller.IsInstalled(MarketInstaller.ProfileManifestPath(
                                   MarketInstaller.ResolveDshHome(_options.DshHome)));

            if (needsEnvironmentWork || !marketReady)
            {
                // Source selection is a measurement, not a guess about the user's location.
                _plan = SourceProbe.Resolve(_options.ForcedRegion, _ui);
                Log.Info("下载源: {0} ({1})", _plan.Choice, _plan.Detail);
                Log.Info("测速结果: {0}", _plan.DescribeScores());
                Log.Info("npm 源顺序: {0}", string.Join(" -> ", _plan.NpmHosts.ToArray()));
                Log.Info("Git 源顺序: {0}", string.Join(" -> ", _plan.GitHosts.ToArray()));
                _outcome.Actions.Add("下载源: " + DescribePlan(_plan));
                foreach (SourceScore score in _plan.Scores)
                {
                    if (score.Reachable) _outcome.Actions.Add("测速 " + score.Label + ": " + score.Detail);
                }
            }
            else
            {
                // Nothing to download: skip the measurement entirely.
                _plan = SourceProbe.DefaultPlan(_options.ForcedRegion);
                string note = _options.NoMarket
                    ? "环境已就绪且已按要求跳过插件市场, 未进行测速"
                    : "环境与插件市场均已就绪, 未进行测速";
                Log.Info("{0}", note);
                _outcome.Actions.Add(note);
            }

            // ---------------------------------------------------------------- step 2: install
            _ui.SetProgress(30);
            if (!EnsurePrerequisites(snapshot))
            {
                _outcome.Success = false;
                _outcome.Summary = "前置环境未满足, 部署已中止。";
                return _outcome;
            }

            // ---------------------------------------------------------------- step 3: market
            _ui.SetProgress(80);
            if (!_options.NoMarket)
            {
                MarketInstaller.Result market = MarketInstaller.Ensure(_plan, _ui, _options.DshHome);
                _outcome.Actions.Add(market.Message);
                if (!market.Success)
                {
                    // A missing market must not block using DSH.
                    Log.Warn("插件市场未安装成功, 继续启动 DSH");
                    _outcome.Actions.Add("提示: 可在 DSH 页面中重试安装插件市场");
                }
            }
            else
            {
                _outcome.Actions.Add("已按要求跳过插件市场安装");
            }

            // ---------------------------------------------------------------- step 4: api key
            _ui.SetProgress(88);
            HandleApiKey();

            // ---------------------------------------------------------------- step 5: launch
            _ui.SetProgress(95);
            LaunchResult launch = Launcher.Launch(_options.Port, _ui, _options.NoLaunch);
            _outcome.Url = launch.Url;
            _outcome.Actions.Add(launch.Message);
            // --no-launch means exactly that: never open a browser, not even for a DSH that was
            // already running.
            if (launch.Running && !_options.NoLaunch) Proc.OpenUrl(launch.Url);

            _outcome.Success = launch.Running || _options.NoLaunch;
            _outcome.Summary = _outcome.Success
                ? "部署完成" + (launch.Running ? ", DSH 已在浏览器中打开。" : ".")
                : "环境已就绪, 但 DSH 启动未确认。";
            _ui.SetProgress(100);
            return _outcome;
        }

        /// <summary>
        /// A dsh newer than <see cref="DshRelease.NewestEcosystemSupported"/> is left in place —
        /// silently downgrading someone's install would be worse — but the user is told plainly
        /// that current plugins may not work with it, and how to move to the supported version.
        /// </summary>
        private void WarnIfNewerThanEcosystem()
        {
            string installedPath = Which.Find("dsh");
            SemVer installed = DshInstaller.InstalledVersion(installedPath);
            SemVer supported;
            if (installed == null || !SemVer.TryParse(DshRelease.NewestEcosystemSupported, out supported)) return;
            if (SemVer.Compare(installed, supported) <= 0) return;

            string note = "注意: 当前 dsh " + installed + " 比插件生态普遍支持的 "
                        + DshRelease.NewestEcosystemSupported + " 更新, 部分插件可能无法加载。"
                        + " 如需回退: npm i -g @deepseek-ai/dsh@" + DshRelease.NewestEcosystemSupported;
            Log.Warn("{0}", note);
            _outcome.Actions.Add(note);
        }

        private void ReportSnapshot(EnvChecker.Snapshot snapshot)
        {
            foreach (ToolStatus status in snapshot.All)
            {
                if (status == null) continue;
                string line = string.Format(CultureInfo.InvariantCulture, "{0}: {1}", status.DisplayName, status.Describe());
                if (status.Path != null) line += "  [" + status.Path + "]";
                if (status.State == ToolState.Ok) Log.Info("{0}", line);
                else Log.Warn("{0}", line);
            }
        }

        /// <summary>
        /// Ask about every component that is missing or too old, then install the agreed ones.
        /// A single "no" stops the whole deployment, as specified.
        /// </summary>
        private bool EnsurePrerequisites(EnvChecker.Snapshot snapshot)
        {
            bool nodeActionable = false;
            if (snapshot.Node.NeedsAction)
            {
                string message = BuildNodePrompt(snapshot.Node);
                if (!_ui.Consent("安装 Node.js", message))
                {
                    Log.Warn("用户拒绝安装 Node.js");
                    return false;
                }
                nodeActionable = true;
            }

            bool gitActionable = false;
            if (snapshot.Git.NeedsAction)
            {
                string message = "检测到 Git " + snapshot.Git.Describe() + "。\r\n\r\n"
                               + "是否现在自动安装 Git for Windows?\r\n"
                               + "下载源: " + Sources.LabelOf(_plan.GitHosts[0]) + " (已按测速选择)\r\n"
                               + "\r\n选择\"否\"将退出程序。";
                if (!_ui.Consent("安装 Git", message))
                {
                    Log.Warn("用户拒绝安装 Git");
                    return false;
                }
                gitActionable = true;
            }

            // pnpm can only be installed after Node exists, so it is decided after Node is handled.
            bool pnpmActionable = false;
            if (snapshot.Pnpm.NeedsAction)
            {
                string message = "检测到 pnpm " + snapshot.Pnpm.Describe() + "。\r\n\r\n"
                               + "pnpm 是 DSH 管理插件所必需的包管理器。\r\n"
                               + "是否现在自动安装 pnpm?\r\n\r\n选择\"否\"将退出程序。";
                if (!_ui.Consent("安装 pnpm", message))
                {
                    Log.Warn("用户拒绝安装 pnpm");
                    return false;
                }
                pnpmActionable = true;
            }

            bool dshActionable = false;
            if (snapshot.Dsh.NeedsAction)
            {
                string message = "未检测到 dsh (DeepSeek Harness 命令行)。\r\n\r\n"
                               + "将通过 npm 安装 @deepseek-ai/dsh@" + DshRelease.Spec + "。\r\n"
                               + "下载源: " + Sources.LabelOf(_plan.PrimaryNpmHost) + " (已按测速选择)\r\n"
                               + "\r\n选择\"否\"将退出程序。";
                if (!_ui.Consent("安装 dsh", message))
                {
                    Log.Warn("用户拒绝安装 dsh");
                    return false;
                }
                dshActionable = true;
            }

            if (nodeActionable)
            {
                if (!NodeInstaller.Install(_plan, _ui))
                {
                    Log.Error("Node.js 安装失败");
                    return false;
                }
                PathSync.RefreshProcessPath();
                // Installing Node can change what npm/pnpm resolve to, so re-evaluate them.
                snapshot.Pnpm = EnvChecker.CheckPnpm();
                snapshot.Dsh = EnvChecker.CheckDsh();
                pnpmActionable = pnpmActionable || snapshot.Pnpm.NeedsAction;
                dshActionable = dshActionable || snapshot.Dsh.NeedsAction;
            }

            if (gitActionable)
            {
                if (!GitInstaller.Install(_plan, _ui)) Log.Warn("Git 安装未成功, 继续后续步骤");
            }

            if (pnpmActionable)
            {
                if (!PnpmInstaller.Install(_plan, _ui))
                {
                    Log.Error("pnpm 安装失败");
                    return false;
                }
            }

            if (dshActionable)
            {
                string installed;
                SemVer resolvedTarget;
                if (!DshInstaller.Ensure(_plan, _ui, out installed, out resolvedTarget))
                {
                    Log.Error("dsh 安装失败");
                    return false;
                }
                _outcome.Actions.Add("dsh " + (installed ?? DshRelease.Spec) + " 已就绪");
                WarnIfNewerThanEcosystem();
            }
            else
            {
                _outcome.Actions.Add("dsh " + (snapshot.Dsh.Version ?? (object)"已安装").ToString() + " 已存在, 未改动");
                WarnIfNewerThanEcosystem();
            }

            // Final verification: never claim success on an environment we cannot use. When
            // nothing was installed there is nothing to re-probe, so the already reported
            // snapshot stands.
            bool anyInstall = nodeActionable || gitActionable || pnpmActionable || dshActionable;
            PathSync.RefreshProcessPath();
            EnvChecker.Snapshot final = anyInstall ? EnvChecker.Detect() : snapshot;
            if (anyInstall) ReportSnapshot(final);
            if (final.Node.NeedsAction || final.Pnpm.NeedsAction || final.Dsh.NeedsAction)
            {
                Log.Error("环境校验未通过: node={0}, pnpm={1}, dsh={2}",
                    final.Node.Describe(), final.Pnpm.Describe(), final.Dsh.Describe());
                _outcome.Actions.Add("警告: Node/pnpm/dsh 中有项目仍不可用");
                return false;
            }
            if (final.Git.NeedsAction)
            {
                Log.Warn("Git 仍不可用, DSH 可以启动, 但部分依赖 Git 的能力会受限");
                _outcome.Actions.Add("提示: Git 未就绪, 部分插件功能可能受限");
            }
            return true;
        }

        private string BuildNodePrompt(ToolStatus node)
        {
            var sb = new StringBuilder();
            if (node.State == ToolState.Missing)
            {
                sb.AppendLine("未检测到 Node.js。");
            }
            else if (node.State == ToolState.TooOld)
            {
                sb.AppendLine("检测到 Node.js " + node.Version + ", 但 DSH 需要 " + NodeRelease.MinimumVersion + " 或更高版本。");
                sb.AppendLine();
                sb.AppendLine("注意: 你已安装的旧版本不会被卸载, 新版会安装到默认位置并覆盖 PATH 指向。");
            }
            else
            {
                sb.AppendLine("检测到 Node.js, 但无法识别其版本号。");
            }
            sb.AppendLine();
            sb.AppendLine("是否现在自动安装 Node.js " + NodeRelease.Version + " (LTS)?");
            sb.AppendLine("下载源: " + NodeMirrorLabel() + " (已按测速选择)");
            sb.AppendLine();
            sb.AppendLine("选择\"否\"将退出程序。");
            return sb.ToString();
        }

        /// <summary>The Node mirror the measurement put first, for the consent prompt.</summary>
        private string NodeMirrorLabel()
        {
            foreach (string host in _plan.NpmHosts)
            {
                if (host.Equals(Sources.ChinaNodeMirror, StringComparison.OrdinalIgnoreCase)
                    || host.Equals(Sources.GlobalNodeMirror, StringComparison.OrdinalIgnoreCase))
                {
                    return Sources.LabelOf(host);
                }
            }
            return Sources.LabelOf(Sources.GlobalNodeMirror);
        }

        /// <summary>One-line summary of what source selection decided.</summary>
        private static string DescribePlan(SourcePlan plan)
        {
            switch (plan.Choice)
            {
                case SourceChoice.Forced:
                    return plan.Detail;
                case SourceChoice.Default:
                    return plan.Detail;
                case SourceChoice.Hint:
                    return "已测速并按最快源排序 (平局由 IP 归属决定): npm="
                           + Sources.LabelOf(plan.PrimaryNpmHost) + ", Git=" + Sources.LabelOf(plan.PrimaryGitHost);
                default:
                    return "已测速并按最快源排序: npm="
                           + Sources.LabelOf(plan.PrimaryNpmHost) + ", Git=" + Sources.LabelOf(plan.PrimaryGitHost);
            }
        }

        private void HandleApiKey()
        {
            if (_options.NoApiKey)
            {
                _outcome.Actions.Add("已按要求跳过 API Key 设置");
                return;
            }

            string path = CredentialsWriter.DefaultPath(_options.DshHome);
            string source;
            if (CredentialsWriter.IsConfigured(path, out source))
            {
                Log.Info("DeepSeek API Key 已配置 (来源: {0}), 跳过询问", source);
                _outcome.Actions.Add("API Key 已存在 (" + source + "), 未改动");
                return;
            }

            const string prompt =
                "尚未配置 DeepSeek API Key。\r\n\r\n" +
                "现在添加吗? 添加后即可直接开始对话。\r\n" +
                "API Key 可在 https://platform.deepseek.com 获取 (以 sk- 开头)。\r\n\r\n" +
                "选择\"否\"也没关系: 稍后可以在 DSH 页面的设置中添加。";

            string key = _ui.AskString("添加 DeepSeek API", prompt, true);
            if (string.IsNullOrWhiteSpace(key))
            {
                Log.Info("用户选择稍后添加 API Key");
                _outcome.Actions.Add("未设置 API Key, 可稍后在 DSH 页面中设置");
                return;
            }

            CredentialResult result = CredentialsWriter.Write(path, key.Trim());
            _outcome.Actions.Add(result.Message);
            if (!result.Success) Log.Warn("{0}", result.Message);
        }
    }
}
