using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace DSHDeploy.Core
{
    /// <summary>
    /// Installs the DSH plugin market (<c>dshmarket</c>) into the <c>web</c> profile.
    ///
    /// The supported path is the product's own plugin manager, which forwards to pnpm in the
    /// profile directory and records the bundle in the profile manifest:
    /// <c>dsh plugin --profile web add dshmarket</c>.
    /// </summary>
    public static class MarketInstaller
    {
        public const string PackageName = "dshmarket";

        public sealed class Result
        {
            public bool Success { get; set; }
            public bool AlreadyPresent { get; set; }
            public string Message { get; set; }
        }

        public static string ResolveDshHome(string overridden = null)
        {
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(home)) return home;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }

        public static string ProfileManifestPath(string dshHome, string profile = "web")
        {
            return Path.Combine(dshHome, "profiles", profile, "package.json");
        }

        public static Result Ensure(SourcePlan plan, IUi ui, string dshHome = null)
        {
            dshHome = ResolveDshHome(dshHome);
            string manifest = ProfileManifestPath(dshHome);

            if (IsInstalled(manifest))
            {
                Log.Info("插件市场已安装 ({0})", manifest);
                return new Result
                {
                    Success = true,
                    AlreadyPresent = true,
                    Message = "插件市场 dshmarket 已安装"
                };
            }

            string dsh = Which.Find("dsh");
            if (dsh == null)
            {
                return new Result { Success = false, Message = "未找到 dsh, 无法安装插件市场" };
            }

            ui.SetStatus("正在安装 DSH 插件市场");
            // pnpm reads npm_config_registry; it must be a plain registry, not the Node mirror.
            string registry = Sources.ChinaNpmRegistry;
            foreach (string host in plan.NpmHosts)
            {
                if (host.Equals(Sources.ChinaNpmRegistry, StringComparison.OrdinalIgnoreCase)
                    || host.Equals(Sources.GlobalNpmRegistry, StringComparison.OrdinalIgnoreCase))
                {
                    registry = host;
                    break;
                }
            }

            var environment = new Dictionary<string, string>
            {
                { "npm_config_registry", registry },
                { "npm_config_fund", "false" },
                { "npm_config_audit", "false" }
            };

            // dsh plugin forwards the rest of the command line to pnpm inside the profile dir.
            string arguments = "plugin --profile web add " + PackageName;
            Log.Info("执行: {0} {1} (registry={2})", dsh, arguments, environment["npm_config_registry"]);
            RunResult result = Proc.Run(dsh, arguments, null, environment, 1800000);

            if (!string.IsNullOrEmpty(result.All))
            {
                foreach (string line in result.All.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line)) Log.Debug("pnpm: {0}", line.TrimEnd());
                }
            }

            if (result.ExitCode == 0 && IsInstalled(manifest))
            {
                Log.Info("插件市场安装成功");
                ui.SetProgress(90);
                return new Result { Success = true, Message = "插件市场 dshmarket 安装成功" };
            }

            string logsDirectory = Path.Combine(dshHome, "profiles", "web", ".plugin-manager", "logs");
            string pending = FindPendingBuilds(dshHome);
            string detail = result.ExitCode == 127
                ? "pnpm 未找到, 请确认 pnpm 已安装且在 PATH 中"
                : "pnpm 退出码 " + result.ExitCode;

            if (!string.IsNullOrEmpty(pending))
            {
                detail += "\npnpm 阻止了构建脚本: " + pending
                        + "\n可在 " + Path.Combine(dshHome, "profiles", "web", "pnpm-workspace.yaml")
                        + " 的 allowBuilds 下加入该包名后重试。";
            }

            Log.Error("插件市场安装失败: {0}", detail);
            if (Directory.Exists(logsDirectory)) Log.Error("完整 pnpm 日志位于: {0}", logsDirectory);

            return new Result { Success = false, Message = "插件市场安装失败: " + detail };
        }

        /// <summary>Both the dependency list and the bundle list must name the package for it to load.</summary>
        public static bool IsInstalled(string manifestPath)
        {
            if (!File.Exists(manifestPath)) return false;
            string json;
            try
            {
                json = File.ReadAllText(manifestPath);
            }
            catch (Exception ex)
            {
                Log.Warn("读取 profile 清单失败: {0}", ex.Message);
                return false;
            }
            return JsonContains(json, "dependencies", PackageName)
                && JsonContains(json, "bundles", PackageName);
        }

        /// <summary>
        /// Membership test for a named object or array in a small JSON document. The search is
        /// recursive so a key nested deeper (for example <c>dsh.profile.bundles</c>) is still
        /// found, and a property's own name never matches a longer one such as
        /// <c>"dependencies"</c> when looking for <c>"dsh"</c>.
        /// </summary>
        public static bool JsonContains(string json, string key, string member)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(member)) return false;
            return FindInJson(json, key, member);
        }

        private static bool FindInJson(string json, string key, string member)
        {
            string target = "\"" + key + "\"";
            int searchFrom = 0;
            while (true)
            {
                int nameIndex = json.IndexOf(target, searchFrom, StringComparison.Ordinal);
                if (nameIndex < 0) return false;
                searchFrom = nameIndex + 1;

                int after = nameIndex + target.Length;
                while (after < json.Length && (json[after] == ' ' || json[after] == '\t')) after++;
                if (after >= json.Length || json[after] != ':') continue;

                int valueStart = after + 1;
                while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;
                if (valueStart >= json.Length) continue;

                char open = json[valueStart];
                if (open == '[')
                {
                    int close = FindMatching(json, valueStart, '[', ']');
                    if (close < 0) continue;
                    string body = json.Substring(valueStart + 1, close - valueStart - 1);
                    foreach (string item in SplitArrayItems(body))
                    {
                        if (string.Equals(Unquote(item), member, StringComparison.Ordinal)) return true;
                    }
                }
                else if (open == '{')
                {
                    int close = FindMatching(json, valueStart, '{', '}');
                    if (close < 0) continue;
                    string body = json.Substring(valueStart + 1, close - valueStart - 1);
                    if (body.IndexOf("\"" + member + "\"", StringComparison.Ordinal) >= 0) return true;
                }
            }
        }

        /// <summary>Strip the surrounding quotes from a JSON string scalar; other values pass through.</summary>
        internal static string Unquote(string token)
        {
            if (string.IsNullOrEmpty(token)) return string.Empty;
            string trimmed = token.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
            {
                return trimmed.Substring(1, trimmed.Length - 2);
            }
            return trimmed;
        }

        /// <summary>Find the index of the bracket that closes the one at <paramref name="open"/>.</summary>
        internal static int FindMatching(string text, int open, char openChar, char closeChar)
        {
            // Returns -1 when the closing bracket is not reached by the end of the text, so a
            // truncated document is never mistaken for a complete one.
            int depth = 0;
            bool inString = false;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == openChar) depth++;
                else if (c == closeChar)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>Split a JSON array body into trimmed scalar items (strings or objects).</summary>
        internal static IEnumerable<string> SplitArrayItems(string body)
        {
            var items = new List<string>();
            if (string.IsNullOrWhiteSpace(body)) return items;

            int depth = 0;
            bool inString = false;
            int itemStart = 0;

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; break;
                    case '{':
                    case '[': depth++; break;
                    case '}':
                    case ']': depth--; break;
                    case ',':
                        if (depth == 0)
                        {
                            items.Add(body.Substring(itemStart, i - itemStart).Trim());
                            itemStart = i + 1;
                        }
                        break;
                }
            }
            string tail = body.Substring(itemStart).Trim();
            if (tail.Length > 0) items.Add(tail);
            return items;
        }

        /// <summary>Packages named under <c>allowBuilds</c>, which pnpm has not yet authorised.</summary>
        internal static string FindPendingBuilds(string dshHome)
        {
            // pnpm records pending approvals in the profile's pnpm-workspace.yaml. Official dsh
            // profiles ship build permissions for node-pty; anything else has to be surfaced.
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "node-pty", "node-pty" }
            };
            string workspace = Path.Combine(dshHome, "profiles", "web", "pnpm-workspace.yaml");
            if (!File.Exists(workspace)) return null;
            try
            {
                string text = File.ReadAllText(workspace);
                var pending = new List<string>();
                foreach (Match match in Regex.Matches(text, @"^\s*([@A-Za-z0-9_\-/\.]+):\s*(true|false)\s*$",
                             RegexOptions.Multiline | RegexOptions.CultureInvariant))
                {
                    string name = match.Groups[1].Value;
                    bool allowed = match.Groups[2].Value == "true";
                    if (!allowed && !known.ContainsKey(name)) pending.Add(name);
                }
                return pending.Count == 0 ? null : string.Join(", ", pending.ToArray());
            }
            catch (Exception ex)
            {
                Log.Debug("读取 pnpm-workspace.yaml 失败: {0}", ex.Message);
                return null;
            }
        }
    }
}
