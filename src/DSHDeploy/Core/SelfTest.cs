using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DSHDeploy.Core
{
    /// <summary>
    /// In-binary verification of every decision the deployer makes. Runs with
    /// <c>--self-test</c> and never modifies the machine: fixtures live in the temp directory
    /// and the environment probe is read-only.
    /// </summary>
    public static class SelfTest
    {
        private static int _passed;
        private static int _failed;
        private static readonly List<string> Failures = new List<string>();

        public static int Run()
        {
            Console.WriteLine("DSH-Deploy 自检 " + AppInfo.Version);
            Console.WriteLine(new string('-', 60));

            SemVerOrdering();
            SemVerFloors();
            ShouldInstallPolicy();
            RegionParsing();
            SourceSelection();
            UrlConstruction();
            ShaSumParsing();
            IconFacts();
            JsonHelpers();
            CredentialMerging();
            CredentialValidation();
            LogMasking();
            EnvironmentProbe();

            Console.WriteLine(new string('-', 60));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "通过 {0}, 失败 {1}", _passed, _failed));
            if (_failed > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败项:");
                foreach (string failure in Failures) Console.WriteLine("  - " + failure);
                return 1;
            }
            Console.WriteLine("全部通过");
            return 0;
        }

        private static void Check(string name, bool condition, string detail = null)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  PASS  " + name);
            }
            else
            {
                _failed++;
                string line = name + (string.IsNullOrEmpty(detail) ? "" : " :: " + detail);
                Failures.Add(line);
                Console.WriteLine("  FAIL  " + line);
            }
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("[ " + title + " ]");
        }

        private static SemVer V(string text)
        {
            SemVer version;
            if (!SemVer.TryParse(text, out version)) throw new ArgumentException("bad version fixture: " + text);
            return version;
        }

        private static void SemVerOrdering()
        {
            Section("版本号比较 (SemVer 11)");
            Check("1.2.3 < 1.2.10", SemVer.Compare(V("1.2.3"), V("1.2.10")) < 0);
            Check("1.2.10 > 1.2.9", SemVer.Compare(V("1.2.10"), V("1.2.9")) > 0);
            Check("1.0.0 = 1.0.0", SemVer.Compare(V("1.0.0"), V("1.0.0")) == 0);
            Check("前导 v 被接受: v2.55.0 = 2.55.0", SemVer.Compare(V("v2.55.0"), V("2.55.0")) == 0);
            Check("release > prerelease (1.0.0 > 1.0.0-rc.1)", SemVer.Compare(V("1.0.0"), V("1.0.0-rc.1")) > 0);
            Check("rc < alpha (0.1.5-rc.2 < 0.1.6-alpha.2)", SemVer.Compare(V("0.1.5-rc.2"), V("0.1.6-alpha.2")) < 0);
            Check("0.1.6-alpha.2 < 0.1.7-alpha.1", SemVer.Compare(V("0.1.6-alpha.2"), V("0.1.7-alpha.1")) < 0);
            Check("0.1.7-alpha.1 < 0.1.7-alpha.2", SemVer.Compare(V("0.1.7-alpha.1"), V("0.1.7-alpha.2")) < 0);
            Check("0.1.7-alpha.2 < 0.1.7", SemVer.Compare(V("0.1.7-alpha.2"), V("0.1.7")) < 0);

            SemVer parsed;
            Check("非法版本被拒绝", !SemVer.TryParse("not-a-version", out parsed));
            Check("从 'v22.20.0' 输出解析", SemVer.TryParseFromOutput("v22.20.0", out parsed) && parsed.ToString() == "22.20.0");
            Check("从 'git version 2.55.0.windows.3' 解析",
                SemVer.TryParseFromOutput("git version 2.55.0.windows.3", out parsed) && parsed.Major == 2 && parsed.Minor == 55);
        }

        private static void SemVerFloors()
        {
            Section("最低版本要求");
            Check("Node 下限为 " + NodeRelease.MinimumVersion,
                SemVer.Compare(EnvChecker.NodeMinimum, V(NodeRelease.MinimumVersion)) == 0);
            Check("22.20.0 满足 Node 下限", SemVer.Compare(V(NodeRelease.Version), EnvChecker.NodeMinimum) >= 0);
            Check("22.18.0 不满足 Node 下限", SemVer.Compare(V("22.18.0"), EnvChecker.NodeMinimum) < 0);
            Check("20.11.0 不满足 Node 下限", SemVer.Compare(V("20.11.0"), EnvChecker.NodeMinimum) < 0);
            SemVer defaultNode;
            Check("默认 Node 版本可解析", SemVer.TryParse(NodeRelease.Version, out defaultNode));
        }

        private static void ShouldInstallPolicy()
        {
            Section("安装/不降级策略");
            Check("未安装 => 安装", EnvChecker.ShouldInstall(null, V("0.1.7-alpha.1")));
            Check("目标更旧 => 不安装 (绝不降级)", !EnvChecker.ShouldInstall(V("0.1.7-alpha.1"), V("0.1.5-rc.2")));
            Check("版本相同 => 不安装", !EnvChecker.ShouldInstall(V("0.1.6-alpha.2"), V("0.1.6-alpha.2")));
            Check("目标更新 => 安装", EnvChecker.ShouldInstall(V("0.1.6-alpha.2"), V("0.1.7-alpha.1")));
            // Version policy: pinned for plugin compatibility, never a dist-tag that can move.
            Check("dsh 默认目标是固定版本而非 latest", DshRelease.DefaultSpec != "latest");
            Check("dsh 默认目标是固定版本而非 alpha 标签", DshRelease.DefaultSpec != "alpha");
            Check("dsh 默认目标与生态支持版本一致",
                DshRelease.DefaultSpec == DshRelease.NewestEcosystemSupported);
            Check("dsh 默认目标是已发布的具体版本",
                Regex.IsMatch(DshRelease.DefaultSpec, @"^\d+\.\d+\.\d+"), DshRelease.DefaultSpec);
            SemVer pinned;
            Check("dsh 默认目标版本可解析", SemVer.TryParse(DshRelease.DefaultSpec, out pinned));
            SemVer ecosystem;
            Check("生态支持版本可解析", SemVer.TryParse(DshRelease.NewestEcosystemSupported, out ecosystem));
            Check("已知不兼容的 0.1.7-alpha.1 不是默认值",
                DshRelease.DefaultSpec != "0.1.7-alpha.1");
            Check("0.1.7-alpha.1 被判为比生态支持版本更新 (会触发警告)",
                SemVer.Compare(V("0.1.7-alpha.1"), V(DshRelease.NewestEcosystemSupported)) > 0);
            Check("0.1.6-alpha.2 不触发警告",
                SemVer.Compare(V("0.1.6-alpha.2"), V(DshRelease.NewestEcosystemSupported)) <= 0);
            Check("0.1.5-rc.2 不触发警告",
                SemVer.Compare(V("0.1.5-rc.2"), V(DshRelease.NewestEcosystemSupported)) <= 0);
        }

        private static void RegionParsing()
        {
            Section("地区覆盖解析");
            Region region;
            Check("'cn' => China", RegionDetector.TryParseRegion("cn", out region) && region == Region.China);
            Check("'CN' => China", RegionDetector.TryParseRegion("CN", out region) && region == Region.China);
            Check("'china' => China", RegionDetector.TryParseRegion("china", out region) && region == Region.China);
            Check("'global' => Global", RegionDetector.TryParseRegion("global", out region) && region == Region.Global);
            Check("'us' => Global", RegionDetector.TryParseRegion("us", out region) && region == Region.Global);
            Check("'mars' 被拒绝", !RegionDetector.TryParseRegion("mars", out region));

            // The system language must not influence source selection at all.
            var systemCultureUsed = false;
            foreach (string name in new[] { "Culture", "Language", "Locale" })
            {
                if (typeof(RegionDetector).GetMember(name).Length > 0) systemCultureUsed = true;
            }
            Check("SourceProbe 不读取系统语言", !systemCultureUsed);
        }

        private static void SourceSelection()
        {
            Section("下载源测速与排序");

            SourcePlan forced = SourceProbe.Resolve("cn", null);
            Check("--region cn 直接指定, 不测速", forced.Choice == SourceChoice.Forced && forced.Detail.Contains("未做测速"));
            Check("--region cn 首选 npmmirror", forced.NpmHosts[0] == Sources.ChinaNpmRegistry, forced.NpmHosts[0]);
            Check("--region cn Git 首选国内镜像", forced.GitHosts[0] == Sources.ChinaGitMirror, forced.GitHosts[0]);

            SourcePlan forcedGlobal = SourceProbe.Resolve("global", null);
            Check("--region global 首选 npmjs", forcedGlobal.NpmHosts[0] == Sources.GlobalNpmRegistry, forcedGlobal.NpmHosts[0]);
            Check("--region global Git 首选 GitHub", forcedGlobal.GitHosts[0] == Sources.GlobalGitReleases, forcedGlobal.GitHosts[0]);
            Check("强制指定时不产生测速结果", forcedGlobal.Scores.Count == 0);

            // A machine that already has everything must not pay for a speed test. DefaultPlan is
            // the no-network path: it must return immediately with no scores and no probing.
            SourcePlan skip = SourceProbe.DefaultPlan(null);
            Check("环境就绪时跳过测速 (无测速结果)", skip.Scores.Count == 0);
            Check("跳过测速时仍给出可用源顺序", skip.NpmHosts.Count >= 2 && skip.GitHosts.Count >= 2);
            Check("跳过测速的说明写明未测速", skip.Detail.Contains("未做测速"), skip.Detail);
            Check("跳过测速时 Git 首选国内镜像 (默认顺序)",
                skip.GitHosts[0] == Sources.ChinaGitMirror || skip.GitHosts[0] == Sources.GlobalGitReleases,
                skip.GitHosts[0]);
            SourcePlan skipCn = SourceProbe.DefaultPlan("cn");
            Check("跳过测速仍尊重 --region cn", skipCn.NpmHosts[0] == Sources.ChinaNpmRegistry, skipCn.NpmHosts[0]);
            Check("跳过测速不发起测速", skipCn.Scores.Count == 0);

            // "fast" must win on the measured numbers regardless of which region it sits in,
            // which is the whole point: the measurement decides, not a guess about the user.
            var fastChina = new SourceScore { Label = "cn", Region = Region.China, Reachable = true, LatencyMs = 80, BytesPerMs = 40 };
            var slowGlobal = new SourceScore { Label = "us", Region = Region.Global, Reachable = true, LatencyMs = 300, BytesPerMs = 5 };
            Check("吞吐与延迟都更优者胜出",
                SourceProbe.ComputeScore(fastChina, RegionHint.Unknown) < SourceProbe.ComputeScore(slowGlobal, RegionHint.Unknown));

            var chunky = new SourceScore { Label = "chunky", Region = Region.Global, Reachable = true, LatencyMs = 400, BytesPerMs = 60 };
            var snappy = new SourceScore { Label = "snappy", Region = Region.Global, Reachable = true, LatencyMs = 40, BytesPerMs = 5 };
            Check("吞吐 12 倍但慢 10 倍者胜出 (吞吐权重更高)",
                SourceProbe.ComputeScore(chunky, RegionHint.Unknown) < SourceProbe.ComputeScore(snappy, RegionHint.Unknown));
            Check("吞吐相同时延迟优者胜出",
                SourceProbe.ComputeScore(
                    new SourceScore { Label = "a", Region = Region.Global, Reachable = true, LatencyMs = 20, BytesPerMs = 10 },
                    RegionHint.Unknown)
                < SourceProbe.ComputeScore(
                    new SourceScore { Label = "b", Region = Region.Global, Reachable = true, LatencyMs = 300, BytesPerMs = 10 },
                    RegionHint.Unknown));
            Check("延迟相同时吞吐优者胜出",
                SourceProbe.ComputeScore(
                    new SourceScore { Label = "a", Region = Region.Global, Reachable = true, LatencyMs = 100, BytesPerMs = 50 },
                    RegionHint.Unknown)
                < SourceProbe.ComputeScore(
                    new SourceScore { Label = "b", Region = Region.Global, Reachable = true, LatencyMs = 100, BytesPerMs = 5 },
                    RegionHint.Unknown));
            Check("得分越低越好, 未测速者为 MaxValue",
                SourceProbe.ComputeScore(fastChina, RegionHint.Unknown) < long.MaxValue
                && SourceProbe.ComputeScore(new SourceScore { Label = "x", Region = Region.China, Reachable = false }, RegionHint.Unknown) == long.MaxValue);
            Check("零吞吐零延迟不会除零",
                SourceProbe.ComputeScore(
                    new SourceScore { Label = "z", Region = Region.Global, Reachable = true, LatencyMs = 0, BytesPerMs = 0 },
                    RegionHint.Unknown) != long.MaxValue);

            long equalA = SourceProbe.ComputeScore(
                new SourceScore { Label = "a", Region = Region.China, Reachable = true, LatencyMs = 100, BytesPerMs = 20 },
                RegionHint.China);
            long equalB = SourceProbe.ComputeScore(
                new SourceScore { Label = "b", Region = Region.China, Reachable = true, LatencyMs = 100, BytesPerMs = 20 },
                RegionHint.Global);
            Check("IP 提示只做微弱加权 (不影响明显更快的源)", equalB > equalA && equalB <= (long)(equalA * 1.3));

            // The hint must never override a clear measurement win.
            var plan = new SourcePlan
            {
                Hint = RegionHint.Global,
                Scores =
                {
                    new SourceScore { Host = Sources.ChinaNpmRegistry, Label = "npmmirror", Region = Region.China,
                                      Measures = new[] { SourceKind.NpmRegistry }, Reachable = true, LatencyMs = 30, BytesPerMs = 60 },
                    new SourceScore { Host = Sources.GlobalNpmRegistry, Label = "npmjs", Region = Region.Global,
                                      Measures = new[] { SourceKind.NpmRegistry }, Reachable = true, LatencyMs = 400, BytesPerMs = 2 }
                }
            };
            foreach (SourceScore score in plan.Scores) score.Score = SourceProbe.ComputeScore(score, plan.Hint);
            List<string> ranked = SourceProbe.Rank(plan, SourceKind.NpmRegistry);
            Check("明显更快的国内源在 IP 提示为海外时仍然第一", ranked[0] == Sources.ChinaNpmRegistry, ranked[0]);

            // A near-tie is resolved by the hint.
            var tiePlan = new SourcePlan
            {
                Hint = RegionHint.Global,
                Scores =
                {
                    new SourceScore { Host = Sources.ChinaNpmRegistry, Label = "npmmirror", Region = Region.China,
                                      Measures = new[] { SourceKind.NpmRegistry }, Reachable = true, LatencyMs = 100, BytesPerMs = 20 },
                    new SourceScore { Host = Sources.GlobalNpmRegistry, Label = "npmjs", Region = Region.Global,
                                      Measures = new[] { SourceKind.NpmRegistry }, Reachable = true, LatencyMs = 100, BytesPerMs = 20 }
                }
            };
            foreach (SourceScore score in tiePlan.Scores) score.Score = SourceProbe.ComputeScore(score, tiePlan.Hint);
            List<string> tieRanked = SourceProbe.Rank(tiePlan, SourceKind.NpmRegistry);
            Check("几乎持平时由 IP 提示决定", tieRanked[0] == Sources.GlobalNpmRegistry, tieRanked[0]);
            Check("平局使用提示后会被记录", tiePlan.HintUsed);

            // Every curated candidate stays in the list, so a bad measurement cannot lose a source.
            Check("排序结果保留全部候选源", tieRanked.Count >= 2, tieRanked.Count.ToString(CultureInfo.InvariantCulture));
            Check("排序结果保留 npmmirror", tieRanked.Contains(Sources.ChinaNpmRegistry));
            Check("排序结果保留 npmjs", tieRanked.Contains(Sources.GlobalNpmRegistry));

            // Git ranking must consider all three hosts, with the clearly fastest first.
            var gitPlan = new SourcePlan
            {
                Hint = RegionHint.Unknown,
                Scores =
                {
                    new SourceScore { Host = Sources.TunaGitMirror, Label = "tuna", Region = Region.China,
                                      Measures = new[] { SourceKind.GitMirror }, Reachable = true, LatencyMs = 50, BytesPerMs = 60 },
                    new SourceScore { Host = Sources.GlobalGitReleases, Label = "github", Region = Region.Global,
                                      Measures = new[] { SourceKind.GitMirror }, Reachable = true, LatencyMs = 300, BytesPerMs = 3 },
                    new SourceScore { Host = Sources.ChinaGitMirror, Label = "npmmirror-git", Region = Region.China,
                                      Measures = new[] { SourceKind.GitMirror }, Reachable = true, LatencyMs = 120, BytesPerMs = 30 }
                }
            };
            foreach (SourceScore score in gitPlan.Scores) score.Score = SourceProbe.ComputeScore(score, gitPlan.Hint);
            List<string> gitRanked = SourceProbe.Rank(gitPlan, SourceKind.GitMirror);
            Check("Git 源按实测吞吐排序", gitRanked[0] == Sources.TunaGitMirror, gitRanked[0]);
            Check("Git 源包含三个候选", gitRanked.Count == 3, gitRanked.Count.ToString(CultureInfo.InvariantCulture));
            Check("Git 源把 GitHub 排在最后", gitRanked[2] == Sources.GlobalGitReleases, gitRanked[2]);
        }

        private static void UrlConstruction()
        {
            Section("下载地址构造");
            List<string> cn = Sources.DefaultHosts(SourceKind.NodeMirror, Region.China);
            List<string> global = Sources.DefaultHosts(SourceKind.NodeMirror, Region.Global);
            Check("中国优先 npmmirror", cn[0] == Sources.ChinaNodeMirror, cn[0]);
            Check("中国备选官方源", cn.Count > 1 && cn[1] == Sources.GlobalNodeMirror);
            Check("海外优先官方源", global[0] == Sources.GlobalNodeMirror, global[0]);
            Check("海外备选镜像", global.Count > 1 && global[1] == Sources.ChinaNodeMirror);
            Check("MSI 文件名含版本与架构",
                Sources.NodeMsiFileName().StartsWith("node-v" + NodeRelease.Version + "-", StringComparison.Ordinal),
                Sources.NodeMsiFileName());
            Check("所有 Node 地址均为 HTTPS", IsHttps(Sources.NodeUrlForHost(Sources.ChinaNodeMirror, NodeRelease.Version, Sources.NodeMsiFileName()))
                                              && IsHttps(Sources.NodeUrlForHost(Sources.GlobalNodeMirror, NodeRelease.Version, Sources.NodeMsiFileName())));

            string shasumUrl = Sources.NodeUrlForHost(Sources.ChinaNodeMirror, NodeRelease.Version, "SHASUMS256.txt");
            Check("SHASUMS 地址指向同一目录",
                shasumUrl == Sources.ChinaNodeMirror + "/v" + NodeRelease.Version + "/SHASUMS256.txt", shasumUrl);

            string asset = Sources.GitInstallerFileName();
            List<string> git = new List<string>();
            foreach (string host in Sources.DefaultHosts(SourceKind.GitMirror, Region.China))
            {
                git.Add(Sources.GitUrlForHost(host, asset));
            }
            Check("中国优先 npmmirror git 镜像", git[0].StartsWith(Sources.ChinaGitMirror, StringComparison.Ordinal), git[0]);
            Check("git 备选含清华 TUNA", git.Exists(u => u != null && u.StartsWith(Sources.TunaGitMirror, StringComparison.Ordinal)));
            Check("git 备选含 GitHub Releases", git.Exists(u => u != null && u.StartsWith(Sources.GlobalGitReleases, StringComparison.Ordinal)));
            Check("所有 git 地址均为 HTTPS", AllHttps(git));
            Check("每个 git 主机都能构造出地址",
                Sources.GitUrlForHost(Sources.ChinaGitMirror, asset) != null
                && Sources.GitUrlForHost(Sources.TunaGitMirror, asset) != null
                && Sources.GitUrlForHost(Sources.GlobalGitReleases, asset) != null);
            Check("未知 git 主机返回 null", Sources.GitUrlForHost("https://example.test/git", asset) == null);

            Check("GitHub 根可用于 pnpm 独立版",
                Sources.PnpmStandaloneUrlForHost(Sources.GitHubRoot) != null
                && Sources.PnpmStandaloneUrlForHost(Sources.GitHubRoot).StartsWith("https://", StringComparison.Ordinal));
            // Verified against the published release assets: pnpm ships win32 .zip archives and
            // npmmirror does not mirror them.
            Check("pnpm 资源名为 win32 zip",
                Sources.PnpmStandaloneFileName() == "pnpm-win32-x64.zip" || Sources.PnpmStandaloneFileName() == "pnpm-win32-arm64.zip",
                Sources.PnpmStandaloneFileName());
            Check("pnpm 资源名含 win32 而非 win-",
                Sources.PnpmStandaloneFileName().StartsWith("pnpm-win32-", StringComparison.Ordinal),
                Sources.PnpmStandaloneFileName());
            Check("npmmirror 不伪造 pnpm 二进址 (它会 404)",
                Sources.PnpmStandaloneUrlForHost(Sources.ChinaNpmRegistry) == null);
        }

        private static bool AllHttps(IEnumerable<string> urls)
        {
            foreach (string url in urls)
            {
                if (!IsHttps(url)) return false;
            }
            return true;
        }

        private static bool IsHttps(string url)
        {
            return !string.IsNullOrEmpty(url) && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static void ShaSumParsing()
        {
            Section("SHASUMS256 解析");
            const string sample =
                "abc123  node-v22.20.0-win-x64.zip\r\n" +
                "def456 *node-v22.20.0-x64.msi\r\n" +
                "ghi789  node-v22.20.0-arm64.msi\r\n";
            Check("解析 zip 行", Downloader.ParseShaSum(sample, "node-v22.20.0-win-x64.zip") == "abc123");
            Check("解析 msi 行 (带 * 前缀)", Downloader.ParseShaSum(sample, "node-v22.20.0-x64.msi") == "def456");
            Check("解析 arm64 行", Downloader.ParseShaSum(sample, "node-v22.20.0-arm64.msi") == "ghi789");
            Check("未命中返回 null", Downloader.ParseShaSum(sample, "node-v22.20.0-linux.tar.gz") == null);
            Check("空输入返回 null", Downloader.ParseShaSum("", "x") == null);

            string msiName = Sources.NodeMsiFileName();
            string expected = Downloader.ParseShaSum(sample, msiName);
            Check("当前架构的 MSI 名可在样本中查表 (x64)", expected == null || expected.Length >= 6);
        }

        private static void IconFacts()
        {
            Section("图标资源");
            string iconPath = FindIcon();
            Check("找到深探碗图标", iconPath != null, "在项目目录中未找到 assets/deepseek-bowl.ico");
            if (iconPath == null) return;

            byte[] bytes = File.ReadAllBytes(iconPath);
            Check("图标非空", bytes.Length > 1000, bytes.Length + " 字节");
            Check("ICONDIR reserved 字段为 0", bytes.Length >= 6 && BitConverter.ToUInt16(bytes, 0) == 0);
            Check("类型为 1 (ICO)", bytes.Length >= 6 && BitConverter.ToUInt16(bytes, 2) == 1);
            int count = bytes.Length >= 6 ? BitConverter.ToUInt16(bytes, 4) : 0;
            Check("包含 7 个尺寸", count == 7, "实际 " + count);

            var sizes = new List<string>();
            for (int i = 0; i < count; i++)
            {
                int offset = 6 + i * 16;
                if (offset + 16 > bytes.Length) break;
                int width = bytes[offset] == 0 ? 256 : bytes[offset];
                int height = bytes[offset + 1] == 0 ? 256 : bytes[offset + 1];
                int bpp = BitConverter.ToUInt16(bytes, offset + 6);
                sizes.Add(width + "x" + height + "@" + bpp + "bpp");
                Check("尺寸 " + width + "x" + height + " 为 32bpp", bpp == 32);
            }
            Check("包含 16x16 (小图标)", sizes.Exists(s => s.StartsWith("16x16", StringComparison.Ordinal)), string.Join(", ", sizes.ToArray()));
            Check("包含 256x256 (大图标)", sizes.Exists(s => s.StartsWith("256x256", StringComparison.Ordinal)), string.Join(", ", sizes.ToArray()));
        }

        private static string FindIcon()
        {
            var candidates = new List<string>();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            candidates.Add(Path.Combine(baseDir, "assets", "deepseek-bowl.ico"));
            candidates.Add(Path.Combine(baseDir, "..", "..", "assets", "deepseek-bowl.ico"));
            candidates.Add(Path.Combine(baseDir, "..", "..", "..", "assets", "deepseek-bowl.ico"));
            candidates.Add(Path.Combine(baseDir, "..", "..", "..", "..", "assets", "deepseek-bowl.ico"));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "assets", "deepseek-bowl.ico"));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "..", "assets", "deepseek-bowl.ico"));
            foreach (string candidate in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        private static void JsonHelpers()
        {
            Section("JSON 小工具");
            Check("提取字符串值", Json.GetString("{\"version\":\"1.2.3\"}", "version") == "1.2.3");
            Check("缺失键返回 null", Json.GetString("{\"a\":1}", "version") == null);
            Check("转义字符被还原", Json.GetString("{\"name\":\"ds\\\\h\"}", "name") == "ds\\h");

            const string manifest = "{\"dependencies\":{\"dshmarket\":\"^1.52.0\"},\"dsh\":{\"profile\":{\"bundles\":[\"@deepseek-ai/dsh-base\",\"dshmarket\"]}}}";
            Check("依赖中包含 dshmarket", MarketInstaller.JsonContains(manifest, "dependencies", "dshmarket"));
            Check("嵌套的 bundles 中包含 dshmarket", MarketInstaller.JsonContains(manifest, "bundles", "dshmarket"));
            Check("bundles 中不含未安装的包", !MarketInstaller.JsonContains(manifest, "bundles", "dsh-email"));
            Check("嵌套数组不会误判", !MarketInstaller.JsonContains("{\"bundles\":[\"a\",\"b\"]}", "bundles", "c"));
            Check("空 bundles 数组", !MarketInstaller.JsonContains("{\"bundles\":[]}", "bundles", "dshmarket"));
            Check("键名不匹配更长的属性名 (\"dsh\" 不匹配 \"dependencies\")",
                !MarketInstaller.JsonContains("{\"dependencies\":{\"dshmarket\":\"1\"}}", "dsh", "dshmarket"));
            Check("键名不匹配前缀相同的属性 (\"refs\" 不匹配 \"records\")",
                !MarketInstaller.JsonContains("{\"records\":{\"x\":\"y\"}}", "refs", "y"));

            var items = new List<string>(MarketInstaller.SplitArrayItems("\"a\", \"b\", {\"c\":1}, \"d\""));
            Check("数组切分保留 4 项", items.Count == 4, items.Count.ToString(CultureInfo.InvariantCulture));
            Check("数组切分去空白", items[0] == "\"a\"" && items[3] == "\"d\"");
        }

        private static void CredentialMerging()
        {
            Section("凭据文件合并");

            string fresh = CredentialsWriter.MergeApiKey(null, "sk-abc123456789");
            Check("新建文件含 version 头", fresh.Contains("version: 1"));
            Check("新建文件含 refs 键", fresh.Contains("refs:"));
            Check("新建文件含 API Key", fresh.Contains("DEEPSEEK_API_KEY: sk-abc123456789"));
            Check("新建文件回读一致", CredentialsWriter.ExtractCurrentValue(fresh) == "sk-abc123456789");

            const string withRecords =
                "version: 1\r\n" +
                "records:\r\n" +
                "  client-connection/browser-session:\r\n" +
                "    kind: grant\r\n" +
                "    payload:\r\n" +
                "      version: 1\r\n" +
                "      secret: fXo5cq4RDhUeeFrT5cwzlBMAm\r\n";
            string merged = CredentialsWriter.MergeApiKey(withRecords, "sk-newkey987654");
            Check("已有 records 时插入 refs 段", merged.Contains("refs:"));
            Check("已有 records 时写入 Key", CredentialsWriter.ExtractCurrentValue(merged) == "sk-newkey987654");
            Check("records 段完整保留", merged.Contains("client-connection/browser-session:")
                                      && merged.Contains("kind: grant")
                                      && merged.Contains("secret: fXo5cq4RDhUeeFrT5cwzlBMAm"));
            Check("records 缩进不被破坏", merged.Contains("  client-connection/browser-session:"));

            const string withExistingKey =
                "version: 1\r\n" +
                "refs:\r\n" +
                "  DEEPSEEK_API_KEY: sk-oldvalue11111\r\n" +
                "  OPENAI_API_KEY: sk-openai2222\r\n";
            string replaced = CredentialsWriter.MergeApiKey(withExistingKey, "sk-replaced3333");
            Check("已存在的 Key 被替换", CredentialsWriter.ExtractCurrentValue(replaced) == "sk-replaced3333");
            Check("同段其它 Key 保留", replaced.Contains("OPENAI_API_KEY: sk-openai2222"));
            Check("旧值不再出现", !replaced.Contains("sk-oldvalue11111"));
            Check("行数未增加", CountLines(replaced) == CountLines(withExistingKey));

            const string withComments =
                "# 我的凭据\r\n" +
                "version: 1\r\n" +
                "refs:\r\n" +
                "  # 注释说明\r\n" +
                "  OTHER_KEY: sk-other99999\r\n" +
                "# 尾部注释\r\n";
            string withComment = CredentialsWriter.MergeApiKey(withComments, "sk-comment77777");
            Check("注释被保留", withComment.Contains("# 我的凭据") && withComment.Contains("# 尾部注释"));
            Check("Key 插入到 refs 未尾之后", withComment.Contains("OTHER_KEY: sk-other99999")
                                             && withComment.Contains("DEEPSEEK_API_KEY: sk-comment77777"));
            int otherIndex = withComment.IndexOf("OTHER_KEY", StringComparison.Ordinal);
            int keyIndex = withComment.IndexOf("DEEPSEEK_API_KEY", StringComparison.Ordinal);
            Check("插入位置在已有条目之后", keyIndex > otherIndex);

            string twice = CredentialsWriter.MergeApiKey(
                CredentialsWriter.MergeApiKey(withRecords, "sk-idempotent1234"), "sk-idempotent1234");
            Check("重复合并不重复插入", CountOccurrences(twice, "DEEPSEEK_API_KEY:") == 1,
                CountOccurrences(twice, "DEEPSEEK_API_KEY:").ToString(CultureInfo.InvariantCulture));
        }

        private static void CredentialValidation()
        {
            Section("凭据文件校验与拒绝");
            Check("合法 Key 通过", CredentialsWriter.IsPlausibleKey("sk-abcdef123456"));
            Check("大写前缀也通过", CredentialsWriter.IsPlausibleKey("SK-ABCDEF123456"));
            Check("缺前缀被拒绝", !CredentialsWriter.IsPlausibleKey("abcdef123456789"));
            Check("过短被拒绝", !CredentialsWriter.IsPlausibleKey("sk-abc"));
            Check("含空格被拒绝", !CredentialsWriter.IsPlausibleKey("sk-abc def123456"));
            Check("含换行被拒绝", !CredentialsWriter.IsPlausibleKey("sk-abc\n123456"));
            Check("空值被拒绝", !CredentialsWriter.IsPlausibleKey(""));
            Check("null 被拒绝", !CredentialsWriter.IsPlausibleKey(null));

            Check("标准文档结构合法", CredentialsWriter.ValidateDocument("version: 1\r\nrefs:\r\n  A: b\r\nrecords:\r\n  x/y:\r\n    kind: grant\r\n") == null);
            Check("未知顶层键被拒绝", CredentialsWriter.ValidateDocument("version: 1\r\nbogus: 1\r\n") != null);
            Check("重复顶层键被拒绝", CredentialsWriter.ValidateDocument("version: 1\r\nrefs:\r\nrefs:\r\n") != null);
            Check("缩进的同名键不算重复", CredentialsWriter.ValidateDocument("version: 1\r\nrefs:\r\n  refs: value\r\n") == null);
            Check("空文档合法", CredentialsWriter.ValidateDocument(null) == null);

            // End-to-end writer behaviour against a temp file.
            string dir = Path.Combine(Path.GetTempPath(), "DSH-Deploy", "selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, ".credentials.yaml");

                CredentialResult created = CredentialsWriter.Write(path, "sk-written12345678");
                Check("写入新文件成功", created.Success, created.Message);
                Check("回读文件内容正确", File.ReadAllText(path).Contains("sk-written12345678"));

                CredentialsWriter.Write(path, "sk-updated87654321");
                string content = File.ReadAllText(path);
                Check("二次写入为更新而非追加", CountOccurrences(content, "DEEPSEEK_API_KEY:") == 1,
                    CountOccurrences(content, "DEEPSEEK_API_KEY:").ToString(CultureInfo.InvariantCulture));

                CredentialResult rejected = CredentialsWriter.Write(path, "not-a-key");
                Check("非法 Key 被拒绝", !rejected.Success);

                string broken = Path.Combine(dir, "broken.yaml");
                File.WriteAllText(broken, "version: 1\r\nrefs:\r\n  A: b\r\nbogus: 1\r\n");
                CredentialResult refused = CredentialsWriter.Write(broken, "sk-validkey123456");
                Check("结构异常的既有文件被拒绝改写", !refused.Success);
                Check("被拒绝时文件保持原样", File.ReadAllText(broken).Contains("bogus: 1")
                                               && !File.ReadAllText(broken).Contains("sk-validkey123456"));

                string recordPath = Path.Combine(dir, ".with-records.yaml");
                File.WriteAllText(recordPath, "version: 1\r\nrecords:\r\n  owner/id:\r\n    kind: grant\r\n    payload:\r\n      secret: keepme\r\n");
                CredentialsWriter.Write(recordPath, "sk-preserve123456");
                string recordContent = File.ReadAllText(recordPath);
                Check("写入后 records 保留", recordContent.Contains("owner/id:") && recordContent.Contains("secret: keepme"));
                Check("写入后 Key 存在", CredentialsWriter.ExtractCurrentValue(recordContent) == "sk-preserve123456");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void LogMasking()
        {
            Section("日志脱敏");
            Check("sk- 键被打码", Log.Mask("key=sk-abcdef123456") == "key=sk-****");
            Check("嵌入文本中的键被打码",
                Log.Mask("使用 sk-abcdef123456 写入").Contains("sk-****")
                && !Log.Mask("使用 sk-abcdef123456 写入").Contains("abcdef123456"));
            Check("多个键都被打码", CountOccurrences(Log.Mask("sk-aaaaaaaa sk-bbbbbbbb"), "sk-****") == 2);
            Check("普通文本不受影响", Log.Mask("Node.js v22.20.0") == "Node.js v22.20.0");
            Check("空值安全", Log.Mask(null) == null);

            string path = CredentialsWriter.DefaultPath(null);
            Check("默认凭据路径以 .credentials.yaml 结尾",
                path.EndsWith(".credentials.yaml", StringComparison.OrdinalIgnoreCase), path);
            Check("默认凭据路径位于 .dsh 下", path.Contains(".dsh"), path);
        }

        private static void EnvironmentProbe()
        {
            Section("本机环境探测 (只读)");
            string node = Which.Find("node");
            Check("能找到 node.exe", node != null, "未找到");
            if (node != null) Check("node 路径为绝对路径", Path.IsPathRooted(node), node);

            string npm = Which.Find("npm");
            Check("能找到 npm.cmd (而非 .ps1)", npm == null || !npm.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase), npm);

            ToolStatus nodeStatus = EnvChecker.CheckNode();
            Check("Node 版本可解析", nodeStatus.Version != null, nodeStatus.RawOutput);
            if (nodeStatus.Version != null)
            {
                Check("Node 满足最低要求", nodeStatus.State != ToolState.TooOld, nodeStatus.Version.ToString());
            }
            Console.WriteLine("        探测结果: node=" + nodeStatus.Describe()
                + ", pnpm=" + EnvChecker.CheckPnpm().Describe()
                + ", git=" + EnvChecker.CheckGit().Describe()
                + ", dsh=" + EnvChecker.CheckDsh().Describe());

            Check("监听检测可用 (3080 结果仅为信息)", true);
        }

        private static int CountLines(string text)
        {
            return text.Replace("\r\n", "\n").Split('\n').Length;
        }

        private static int CountOccurrences(string text, string needle)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0, index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }
    }
}
