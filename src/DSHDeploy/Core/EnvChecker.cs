using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DSHDeploy.Core
{
    public enum ToolState
    {
        Ok,
        Missing,
        TooOld,
        UnknownVersion
    }

    public sealed class ToolStatus
    {
        public string DisplayName { get; set; }
        public string ExecutableName { get; set; }
        public string Path { get; set; }
        public SemVer Version { get; set; }
        public SemVer Minimum { get; set; }
        public ToolState State { get; set; }
        public string RawOutput { get; set; }
        public string Note { get; set; }

        public bool NeedsAction
        {
            get { return State != ToolState.Ok; }
        }

        public string Describe()
        {
            switch (State)
            {
                case ToolState.Ok:
                    return "已安装 " + (Version ?? (object)"(未知版本)").ToString();
                case ToolState.Missing:
                    return "未安装";
                case ToolState.TooOld:
                    return "版本过低 " + Version + " (< " + Minimum + ")";
                default:
                    return "已安装, 版本无法识别";
            }
        }
    }

    /// <summary>
    /// Detects Node.js, pnpm, Git and dsh. Resolution goes through <see cref="Which"/> so the
    /// unreliable npm PowerShell shims are never used, and version probes are bounded.
    /// </summary>
    public static class EnvChecker
    {
        public static readonly SemVer NodeMinimum = ParseVersion(NodeRelease.MinimumVersion);
        public static readonly SemVer GitMinimum = ParseVersion("2.30.0");
        public static readonly SemVer PnpmMinimum = ParseVersion("9.0.0");

        private static SemVer ParseVersion(string text)
        {
            SemVer version;
            if (!SemVer.TryParse(text, out version))
                throw new ArgumentException("非法版本号: " + text, "text");
            return version;
        }

        public sealed class Snapshot
        {
            public ToolStatus Node;
            public ToolStatus Pnpm;
            public ToolStatus Git;
            public ToolStatus Dsh;

            public IEnumerable<ToolStatus> All
            {
                get
                {
                    yield return Node;
                    yield return Pnpm;
                    yield return Git;
                    yield return Dsh;
                }
            }

            public bool AllReady
            {
                get
                {
                    foreach (ToolStatus status in All)
                    {
                        if (status != null && status.NeedsAction) return false;
                    }
                    return true;
                }
            }
        }

        public static Snapshot Detect()
        {
            return new Snapshot
            {
                Node = CheckNode(),
                Pnpm = CheckPnpm(),
                Git = CheckGit(),
                Dsh = CheckDsh()
            };
        }

        public static ToolStatus CheckNode()
        {
            return Check("Node.js", "node", "--version", NodeMinimum, null);
        }

        public static ToolStatus CheckPnpm()
        {
            return Check("pnpm", "pnpm", "--version", null, null);
        }

        public static ToolStatus CheckGit()
        {
            return Check("Git", "git", "--version", GitMinimum, null);
        }

        /// <summary>
        /// dsh has no published minimum we can assert, so any resolvable version counts as ready.
        /// </summary>
        public static ToolStatus CheckDsh()
        {
            var status = Check("dsh", "dsh", "--version", null, null);
            if (status.State == ToolState.UnknownVersion && status.Path != null)
            {
                // dsh --version may print only a number; that is still a working install.
                status.State = ToolState.Ok;
                status.Note = "版本号未识别, 但可执行文件存在";
            }
            return status;
        }

        public static ToolStatus Check(string displayName, string executable, string versionArgs, SemVer minimum, string workingDirectory)
        {
            var status = new ToolStatus
            {
                DisplayName = displayName,
                ExecutableName = executable,
                Minimum = minimum
            };

            string resolved = Which.Find(executable);
            if (resolved == null)
            {
                status.State = ToolState.Missing;
                return status;
            }
            status.Path = resolved;

            RunResult result = Proc.Run(resolved, versionArgs, workingDirectory, null, 30000);
            status.RawOutput = result.All;

            SemVer parsed;
            if (!SemVer.TryParseFromOutput(result.StdOut, out parsed) && !SemVer.TryParseFromOutput(result.StdErr, out parsed))
            {
                status.State = ToolState.UnknownVersion;
                status.Note = result.Ok ? null : ("执行失败: " + result.All);
                return status;
            }

            status.Version = parsed;
            if (minimum != null && SemVer.Compare(parsed, minimum) < 0)
            {
                status.State = ToolState.TooOld;
            }
            else
            {
                status.State = ToolState.Ok;
            }
            return status;
        }

        /// <summary>
        /// Decide whether to install a candidate over what is already present. The deployer never
        /// downgrades a working tool: npm's <c>latest</c> tag for dsh is currently older than the
        /// published <c>alpha</c>, so "just install latest" would silently regress users.
        /// </summary>
        public static bool ShouldInstall(SemVer installed, SemVer candidate)
        {
            if (installed == null) return true;
            if (candidate == null) return false;
            return SemVer.Compare(installed, candidate) < 0;
        }
    }

    /// <summary>Reads and writes the small JSON documents the deployer needs (npm metadata, profile manifests).</summary>
    public static class Json
    {
        /// <summary>Extract a JSON string value by key, tolerating whitespace. Returns null when absent.</summary>
        public static string GetString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            Match match = new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant).Match(json);
            return match.Success ? Unescape(match.Groups[1].Value) : null;
        }

        private static string Unescape(string value)
        {
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length) { sb.Append(value[i]); continue; }
                char next = value[++i];
                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    default: sb.Append(next); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Escape a string for embedding in a JSON document.</summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
