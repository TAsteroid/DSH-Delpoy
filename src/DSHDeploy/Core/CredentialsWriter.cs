using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace DSHDeploy.Core
{
    public sealed class CredentialResult
    {
        public bool Success { get; set; }
        public bool Skipped { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Writes the DeepSeek API key into the harness credential store
    /// (<c>%USERPROFILE%\.dsh\.credentials.yaml</c>).
    ///
    /// The file is a versioned YAML document holding <c>refs:</c> (env-var name to value) and
    /// <c>records:</c> (per-plugin credentials). The product refuses to start on a malformed
    /// file, so this writer edits the text surgically: an existing key line is replaced in
    /// place, otherwise one is appended inside the existing <c>refs:</c> block. Comments,
    /// ordering, and every <c>records:</c> entry survive untouched.
    /// </summary>
    public static class CredentialsWriter
    {
        private static readonly Regex KeyLinePattern = new Regex(
            @"^(\s*" + AppInfo.ApiKeyRef + @"\s*:\s*)(.*?)(\s*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

        /// <summary>
        /// Matches an unindented <c>key:</c> at the start of the given line. These patterns are
        /// applied one line at a time, so they are deliberately <em>not</em> multiline: with
        /// <see cref="RegexOptions.Multiline"/> the <c>^</c> anchor matches only at position 0 of
        /// the input and every call would silently fail on a later line.
        /// </summary>
        private static readonly Regex TopLevelKeyPattern = new Regex(
            @"^([A-Za-z_][A-Za-z0-9_\-]*)\s*:",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex InsertableValuePattern = new Regex(
            @"^[ \t]{2,}([A-Za-z_][A-Za-z0-9_\-]*)\s*:\s*\S",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string DefaultPath(string dshHomeOverride = null)
        {
            if (!string.IsNullOrWhiteSpace(dshHomeOverride))
                return Path.Combine(dshHomeOverride, ".credentials.yaml");
            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(home))
                return Path.Combine(home, ".credentials.yaml");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", ".credentials.yaml");
        }

        /// <summary>True when the key is already available, either from the environment or the store.</summary>
        public static bool IsConfigured(string path, out string source)
        {
            source = null;
            string fromEnv = Environment.GetEnvironmentVariable(AppInfo.ApiKeyRef);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                source = "启动环境变量";
                return true;
            }
            if (File.Exists(path))
            {
                try
                {
                    string content = File.ReadAllText(path);
                    string value = ExtractCurrentValue(content);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        source = "凭据文件";
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("读取凭据文件失败: {0}", ex.Message);
                }
            }
            return false;
        }

        /// <summary>A key that can be written into the refs section.</summary>
        public static bool IsPlausibleKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            string trimmed = key.Trim();
            if (trimmed.Length < 8) return false;
            if (!trimmed.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (char c in trimmed)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') continue;
                return false; // rejects newlines, spaces, and any other injection vector
            }
            return true;
        }

        public static string ExtractCurrentValue(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            Match match = KeyLinePattern.Match(content);
            if (!match.Success) return null;
            return match.Groups[2].Value.Trim();
        }

        /// <summary>
        /// Validate the document shape. Returns null when the file is acceptable to edit,
        /// otherwise a human-readable reason to refuse.
        /// </summary>
        public static string ValidateDocument(string content)
        {
            if (content == null) return null;
            string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length > 0 && line[0] == '\uFEFF') line = line.Substring(1);
                Match match = TopLevelKeyPattern.Match(line);
                if (!match.Success) continue;
                string key = match.Groups[1].Value;
                if (!seen.Add(key))
                    return string.Format(CultureInfo.InvariantCulture, "第 {0} 行出现重复的顶层键 '{1}'", i + 1, key);
                if (!string.Equals(key, "version", StringComparison.Ordinal)
                    && !string.Equals(key, "refs", StringComparison.Ordinal)
                    && !string.Equals(key, "records", StringComparison.Ordinal))
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "发现未知的顶层键 '{0}' (第 {1} 行); DSH 会拒绝这样的文件, 因此不做修改",
                        key, i + 1);
                }
            }
            return null;
        }

        /// <summary>Rewrite the document with the key added or replaced. Returns the new content.</summary>
        public static string MergeApiKey(string content, string apiKey)
        {
            if (string.IsNullOrEmpty(content))
            {
                return "version: 1" + Environment.NewLine
                     + "refs:" + Environment.NewLine
                     + "  " + AppInfo.ApiKeyRef + ": " + apiKey + Environment.NewLine;
            }

            string newline = content.Contains("\r\n") ? "\r\n" : "\n";
            string text = content.Replace("\r\n", "\n").Replace('\r', '\n');
            bool endsWithNewline = text.EndsWith("\n", StringComparison.Ordinal);
            string[] lines = text.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                Match match = KeyLinePattern.Match(lines[i]);
                if (match.Success)
                {
                    lines[i] = match.Groups[1].Value + apiKey + match.Groups[3].Value;
                    return string.Join(newline, lines);
                }
            }

            int insertAt = FindRefsInsertionPoint(lines);
            var result = new List<string>(lines);
            if (!HasTopLevelRefs(lines))
            {
                // Nothing to merge into: the section must be created before the entry is added.
                int refsAt = FindRefsSectionIndex(lines);
                result.Insert(refsAt, "refs:");
                insertAt = refsAt + 1;
            }
            result.Insert(insertAt, "  " + AppInfo.ApiKeyRef + ": " + apiKey);

            return string.Join(newline, result);
        }

        /// <summary>Index where a new <c>refs:</c> line belongs when the document has none.</summary>
        internal static int FindRefsSectionIndex(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = TopLevelKeyPattern.Match(Strip(lines[i]));
                if (match.Success && string.Equals(match.Groups[1].Value, "version", StringComparison.Ordinal))
                {
                    return i + 1;
                }
            }
            return 0;
        }

        internal static bool HasTopLevelRefs(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = TopLevelKeyPattern.Match(Strip(lines[i]));
                if (match.Success && string.Equals(match.Groups[1].Value, "refs", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Index in <paramref name="lines"/> where a new <c>DEEPSEEK_API_KEY</c> entry belongs,
        /// for the array that will actually receive it. The function never builds a modified copy
        /// and indexes it: an index into a different array would be applied to this one and
        /// silently drop the section it was meant to create.
        /// </summary>
        internal static int FindRefsInsertionPoint(string[] lines)
        {
            int refsIndex = -1;
            int versionIndex = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = TopLevelKeyPattern.Match(Strip(lines[i]));
                if (!match.Success) continue;
                string name = match.Groups[1].Value;
                if (refsIndex < 0 && string.Equals(name, "refs", StringComparison.Ordinal)) refsIndex = i;
                if (versionIndex < 0 && string.Equals(name, "version", StringComparison.Ordinal)) versionIndex = i;
            }

            if (refsIndex < 0)
            {
                // No refs section exists. Put it immediately after the version header when there
                // is one, otherwise at the very top, and return the line inside it.
                return versionIndex >= 0 ? versionIndex + 1 : 0;
            }

            // Walk the refs block; the next top-level key ends it.
            int insert = refsIndex + 1;
            for (int i = refsIndex + 1; i < lines.Length; i++)
            {
                string line = Strip(lines[i]);
                if (TopLevelKeyPattern.IsMatch(line)) break;
                if (InsertableValuePattern.IsMatch(line)) insert = i + 1;
            }
            return insert;
        }

        private static string Strip(string line)
        {
            return line.Length > 0 && line[0] == '\uFEFF' ? line.Substring(1) : line;
        }

        /// <summary>Validate, merge and write atomically, then read back for confirmation.</summary>
        public static CredentialResult Write(string path, string apiKey)
        {
            if (!IsPlausibleKey(apiKey))
            {
                return new CredentialResult
                {
                    Success = false,
                    Message = "API Key 格式不正确 (应以 sk- 开头, 且不含空格或换行)"
                };
            }

            string existing = null;
            if (File.Exists(path))
            {
                try
                {
                    existing = File.ReadAllText(path);
                }
                catch (Exception ex)
                {
                    return new CredentialResult { Success = false, Message = "无法读取现有凭据文件: " + ex.Message };
                }

                string problem = ValidateDocument(existing);
                if (problem != null)
                {
                    Log.Error("凭据文件校验未通过: {0}", problem);
                    return new CredentialResult
                    {
                        Success = false,
                        Message = "凭据文件结构异常, 已放弃写入以免 DSH 无法启动: " + problem
                                  + "\n请改为在 DSH 页面的设置中手动添加 API Key。"
                    };
                }
            }

            string merged = MergeApiKey(existing, apiKey);

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                string temp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.WriteAllText(temp, merged, new UTF8Encoding(false));
                RestrictToCurrentUser(temp);

                if (File.Exists(path))
                {
                    // File.Replace keeps the operation atomic on NTFS.
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
                RestrictToCurrentUser(path);
            }
            catch (Exception ex)
            {
                Log.Error("写入凭据文件失败: {0}", ex.Message);
                return new CredentialResult { Success = false, Message = "写入凭据文件失败: " + ex.Message };
            }

            // Read back: a file the product would reject must never be left behind.
            try
            {
                string verify = File.ReadAllText(path);
                string stored = ExtractCurrentValue(verify);
                if (!IsPlausibleKey(stored))
                {
                    return new CredentialResult
                    {
                        Success = false,
                        Message = "写入后回读校验失败, 请改为在 DSH 页面中手动添加 API Key"
                    };
                }
            }
            catch (Exception ex)
            {
                return new CredentialResult { Success = false, Message = "回读凭据文件失败: " + ex.Message };
            }

            Log.Info("API Key 已写入 {0}", path);
            return new CredentialResult { Success = true, Message = "API Key 已保存到 " + path };
        }

        private static void RestrictToCurrentUser(string path)
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                if (identity == null || identity.User == null) return;
                var security = new FileSecurity();
                security.SetOwner(identity.User);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(
                    identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(security);
            }
            catch (Exception ex)
            {
                Log.Debug("设置凭据文件权限失败 (不影响可用性): {0}", ex.Message);
            }
        }
    }
}
