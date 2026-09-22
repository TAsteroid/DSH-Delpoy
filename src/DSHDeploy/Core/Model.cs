using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DSHDeploy.Core
{
    /// <summary>Which mirror family a source belongs to.</summary>
    public enum Region
    {
        China,
        Global
    }

    /// <summary>How the download source was chosen.</summary>
    public enum SourceChoice
    {
        /// <summary>An explicit --region / DSH_DEPLOY_REGION request; nothing is measured.</summary>
        Forced,
        /// <summary>Chosen by the local responsiveness race.</summary>
        Measured,
        /// <summary>Two or more sources came out even; the IP hint broke the tie.</summary>
        Hint,
        /// <summary>Nothing could be measured; the region default order stands.</summary>
        Default
    }

    /// <summary>The independently measured mirror groups.</summary>
    public enum SourceKind
    {
        NpmRegistry,
        NodeMirror,
        GitMirror,
        PnpmMirror
    }

    /// <summary>One measured host, with the numbers that justify its position.</summary>
    public sealed class SourceScore
    {
        /// <summary>The rooted host these URLs are built from (for example <c>https://nodejs.org/dist</c>).</summary>
        public string Host { get; set; }
        public string Label { get; set; }
        public Region Region { get; set; }
        /// <summary>Mirror groups this measurement decides, since one host serves several.</summary>
        public SourceKind[] Measures { get; set; }
        public long LatencyMs { get; set; }
        public long BytesPerMs { get; set; }
        /// <summary>Lower is better; <see cref="long.MaxValue"/> means unmeasurable.</summary>
        public long Score { get; set; }
        public bool Reachable { get; set; }
        public string Detail { get; set; }

        public override string ToString()
        {
            if (!Reachable) return Label + "=不可达";
            return Label + " " + LatencyMs + "ms, " + BytesPerMs + " B/ms";
        }
    }

    /// <summary>Where the IP-based hint suggests the user is; only ever used to break a near-tie.</summary>
    public enum RegionHint
    {
        Unknown,
        China,
        Global
    }

    /// <summary>The resolved mirror order, one ranking per mirror group.</summary>
    public sealed class SourcePlan
    {
        public SourceChoice Choice { get; set; }
        /// <summary>Ordered hosts for the npm ecosystem: registry, Node binaries, pnpm.</summary>
        public List<string> NpmHosts { get; set; }
        /// <summary>Ordered hosts for the Git for Windows installers.</summary>
        public List<string> GitHosts { get; set; }
        public RegionHint Hint { get; set; }
        public bool HintUsed { get; set; }
        public string Detail { get; set; }
        public readonly List<SourceScore> Scores = new List<SourceScore>();

        public string PrimaryNpmHost
        {
            get { return NpmHosts != null && NpmHosts.Count > 0 ? NpmHosts[0] : Sources.GlobalNpmRegistry; }
        }

        public string PrimaryGitHost
        {
            get { return GitHosts != null && GitHosts.Count > 0 ? GitHosts[0] : Sources.ChinaGitMirror; }
        }

        /// <summary>One line per measured host, for the log and the UI.</summary>
        public string DescribeScores()
        {
            var parts = new List<string>();
            foreach (SourceScore score in Scores) parts.Add(score.ToString());
            return string.Join("; ", parts.ToArray());
        }
    }

    /// <summary>
    /// The <c>@deepseek-ai/dsh</c> version this deployer installs.
    ///
    /// Two traps are deliberately avoided:
    ///
    /// 1. npm's <c>latest</c> tag resolves to <c>0.1.5-rc.2</c>, which is <em>older</em> than the
    ///    current releases, and dsh-market requires the newer web primitives. So <c>latest</c>
    ///    is never used.
    /// 2. The <c>alpha</c> tag currently resolves to <c>0.1.7-alpha.1</c>, which the community
    ///    plugin ecosystem does not support yet: across the plugins installed on a working
    ///    profile, no peer range mentions 0.1.7 at all, while several explicitly list
    ///    <c>0.1.6-alpha.1</c> / <c>0.1.6-alpha.2</c>. Installing it breaks most plugins.
    ///
    /// So the default is pinned to a concrete, verified-installable release that the ecosystem
    /// actually declares support for, rather than to a dist-tag that can move under us.
    /// Override with <c>DSH_DEPLOY_DSH_VERSION</c>.
    /// </summary>
    public static class DshRelease
    {
        /// <summary>
        /// Pinned default. Chosen for plugin compatibility, not for being newest:
        /// <c>0.1.7-alpha.1</c> is newer but unsupported by current plugins.
        /// </summary>
        public const string DefaultSpec = "0.1.6-alpha.2";

        /// <summary>
        /// Newest release whose core packages the current plugin ecosystem declares support for.
        /// Whenever the version actually installed is newer than this, plugins may break, so the
        /// deployer says so explicitly instead of silently leaving a broken combination in place.
        /// </summary>
        public const string NewestEcosystemSupported = "0.1.6-alpha.2";

        public static string Spec
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("DSH_DEPLOY_DSH_VERSION");
                return string.IsNullOrWhiteSpace(v) ? DefaultSpec : v.Trim();
            }
        }
    }

    /// <summary>The Node.js release this deployer installs when Node is absent or too old.</summary>
    public static class NodeRelease
    {
        public const string DefaultVersion = "22.20.0";
        public const string MinimumVersion = "22.19.0";

        public static string Version
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("DSH_DEPLOY_NODE_VERSION");
                return string.IsNullOrWhiteSpace(v) ? DefaultVersion : v.Trim();
            }
        }
    }

    /// <summary>Options resolved from the command line.</summary>
    public sealed class Options
    {
        public string ForcedRegion;      // "cn" | "global" | null
        public int Port = 3080;
        public bool Headless;
        public bool SelfTest;
        public bool ShowVersion;
        public bool NoLaunch;
        public bool AssumeYes;
        public bool NoMarket;
        public bool NoApiKey;
        public string DshHome;
        public string NpmRegistry;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "--region":
                        if (next != null) { o.ForcedRegion = next; i++; }
                        break;
                    case "--port":
                        int p;
                        if (next != null && int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out p)) { o.Port = p; i++; }
                        break;
                    case "--dsh-home":
                        if (next != null) { o.DshHome = next; i++; }
                        break;
                    case "--registry":
                        if (next != null) { o.NpmRegistry = next; i++; }
                        break;
                    case "--headless": o.Headless = true; break;
                    case "--self-test": o.SelfTest = true; break;
                    case "--no-launch": o.NoLaunch = true; break;
                    case "--yes": o.AssumeYes = true; break;
                    case "--no-market": o.NoMarket = true; break;
                    case "--no-api-key": o.NoApiKey = true; break;
                    case "--version":
                    case "-V": o.ShowVersion = true; break;
                    case "--help":
                    case "-h":
                        o.ShowVersion = true; break;
                }
            }
            return o;
        }

        public static string HelpText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("DSH-Deploy " + AppInfo.Version + " - DeepSeek Harness 一键部署器");
            sb.AppendLine();
            sb.AppendLine("用法: DSH-Deploy.exe [选项]");
            sb.AppendLine();
            sb.AppendLine("  --region cn|global   强制使用国内/官方源 (覆盖 IP 检测)");
            sb.AppendLine("  --port <n>            DSH Web 端口 (默认 3080)");
            sb.AppendLine("  --registry <url>      npm registry 覆盖");
            sb.AppendLine("  --dsh-home <path>     DSH_HOME 覆盖 (默认 %USERPROFILE%\\.dsh)");
            sb.AppendLine("  --headless            无界面: 直接检测并按同意安装, 需要 --yes");
            sb.AppendLine("  --yes                 对所有安装询问自动同意 (无界面模式)");
            sb.AppendLine("  --no-launch           完成部署但不启动 DSH");
            sb.AppendLine("  --no-market           跳过插件市场安装");
            sb.AppendLine("  --no-api-key          跳过 API Key 询问");
            sb.AppendLine("  --self-test           运行内置自检并退出 (0 通过 / 1 失败)");
            sb.AppendLine("  -V, --version         显示版本");
            return sb.ToString();
        }
    }

    public static class AppInfo
    {
        public const string Version = "1.0.0";
        public const string Product = "DSH Deploy";

        /// <summary>Environment variable name holding the DeepSeek API key.</summary>
        public const string ApiKeyRef = "DEEPSEEK_API_KEY";
    }

    /// <summary>Semantic-version comparison covering the npm prerelease tags the harness ships.</summary>
    public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
    {
        private static readonly Regex Pattern = new Regex(
            @"^\s*[vV]?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]+)?\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public int Major { get; private set; }
        public int Minor { get; private set; }
        public int Patch { get; private set; }
        public string Prerelease { get; private set; }
        public string Original { get; private set; }

        public bool IsPrerelease { get { return !string.IsNullOrEmpty(Prerelease); } }

        private SemVer(int major, int minor, int patch, string prerelease, string original)
        {
            Major = major; Minor = minor; Patch = patch; Prerelease = prerelease; Original = original;
        }

        public static bool TryParse(string text, out SemVer version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            Match m = Pattern.Match(text);
            if (!m.Success) return false;
            int major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            int patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            string pre = m.Groups[4].Success ? m.Groups[4].Value : null;
            // Keep a printable, normalised form: tool output like "v22.20.0" should render as
            // "22.20.0", while the prerelease suffix is preserved.
            string original = m.Value.Trim();
            if (original.Length > 0 && (original[0] == 'v' || original[0] == 'V')) original = original.Substring(1);
            version = new SemVer(major, minor, patch, pre, original);
            return true;
        }

        /// <summary>
        /// Extract the first version-looking token from arbitrary tool output,
        /// e.g. "git version 2.55.0.windows.3" or "v22.20.0" or "dsh/0.1.6-alpha.2 linux".
        /// </summary>
        public static bool TryParseFromOutput(string output, out SemVer version)
        {
            version = null;
            if (string.IsNullOrEmpty(output)) return false;
            var token = new Regex(@"[vV]?\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.\-]+)?", RegexOptions.CultureInvariant);
            foreach (Match m in token.Matches(output))
            {
                SemVer candidate;
                if (TryParse(m.Value, out candidate))
                {
                    version = candidate;
                    return true;
                }
            }
            return false;
        }

        public static int Compare(SemVer a, SemVer b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int c = a.Major.CompareTo(b.Major); if (c != 0) return c;
            c = a.Minor.CompareTo(b.Minor); if (c != 0) return c;
            c = a.Patch.CompareTo(b.Patch); if (c != 0) return c;
            if (!a.IsPrerelease && !b.IsPrerelease) return 0;
            if (!a.IsPrerelease) return 1;   // release outranks prerelease
            if (!b.IsPrerelease) return -1;
            return ComparePrerelease(a.Prerelease, b.Prerelease);
        }

        private static int ComparePrerelease(string left, string right)
        {
            string[] ls = left.Split('.');
            string[] rs = right.Split('.');
            int n = Math.Min(ls.Length, rs.Length);
            for (int i = 0; i < n; i++)
            {
                int c = CompareIdentifier(ls[i], rs[i]);
                if (c != 0) return c;
            }
            return ls.Length.CompareTo(rs.Length);
        }

        /// <summary>SemVer 11: numeric identifiers compare numerically and rank below alphanumeric ones.</summary>
        private static int CompareIdentifier(string left, string right)
        {
            long ln, rn;
            bool lb = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out ln);
            bool rb = long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out rn);
            if (lb && rb) return ln.CompareTo(rn);
            if (lb) return -1;
            if (rb) return 1;
            return string.Compare(left, right, StringComparison.Ordinal);
        }

        public int CompareTo(SemVer other) { return Compare(this, other); }
        public bool Equals(SemVer other) { return Compare(this, other) == 0; }
        public override bool Equals(object obj) { return Equals(obj as SemVer); }
        public override int GetHashCode() { return (Major * 1000000 + Minor * 1000 + Patch).GetHashCode(); }
        public override string ToString() { return string.IsNullOrEmpty(Original) ? (Major + "." + Minor + "." + Patch) : Original; }
    }
}
