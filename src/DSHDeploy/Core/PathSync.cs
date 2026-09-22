using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace DSHDeploy.Core
{
    /// <summary>
    /// Keeps PATH consistent between the registry, the running process, and freshly installed
    /// tools. A silent MSI updates the registry and broadcasts a change, but the already-running
    /// deployer keeps its stale copy — so we rebuild PATH in-process instead of asking the user
    /// to reboot.
    /// </summary>
    public static class PathSync
    {
        private const string EnvironmentKey = @"Environment";

        public static string GetUserPath()
        {
            return ReadRegistryValue(Registry.CurrentUser, EnvironmentKey, "Path");
        }

        public static string GetMachinePath()
        {
            return ReadRegistryValue(Registry.LocalMachine, EnvironmentKey, "Path");
        }

        private static string ReadRegistryValue(RegistryKey root, string subKey, string name)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(subKey))
                {
                    if (key == null) return string.Empty;
                    object value = key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    string text = value as string;
                    if (!string.IsNullOrEmpty(text)) return Environment.ExpandEnvironmentVariables(text);
                    // A REG_EXPAND_SZ stored oddly may still come back through the default read.
                    value = key.GetValue(name, string.Empty);
                    return value as string ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("读取注册表 PATH 失败 ({0}): {1}", subKey, ex.Message);
                return string.Empty;
            }
        }

        /// <summary>Deduplicated, order-preserving union of machine, user and current PATH.</summary>
        public static List<string> EffectiveEntries()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            Action<string> add = text =>
            {
                if (string.IsNullOrEmpty(text)) return;
                foreach (string raw in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string entry = Normalize(raw);
                    if (entry.Length == 0) continue;
                    if (seen.Add(entry)) ordered.Add(entry);
                }
            };
            add(GetMachinePath());
            add(GetUserPath());
            add(Environment.GetEnvironmentVariable("PATH"));
            foreach (string known in Which.KnownDirectories) add(known);
            return ordered;
        }

        public static string Normalize(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return string.Empty;
            string value = entry.Trim().Trim('"');
            value = Environment.ExpandEnvironmentVariables(value);
            try
            {
                value = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { }
            if (value.Length == 0) return string.Empty;
            try { return Path.GetFullPath(value); }
            catch { return value; }
        }

        /// <summary>Rebuild this process's PATH. Call after every install.</summary>
        public static void RefreshProcessPath()
        {
            string joined = string.Join(";", EffectiveEntries());
            Environment.SetEnvironmentVariable("PATH", joined);
            Log.Debug("已刷新进程 PATH ({0} 项)", joined.Split(';').Length);
        }

        public static bool IsOnUserPath(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            string wanted = Normalize(directory);
            foreach (string raw in (GetUserPath() ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(Normalize(raw), wanted, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Persist a directory on the user PATH (idempotent, no elevation needed).</summary>
        public static bool AddToUserPath(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
            if (IsOnUserPath(directory)) return true;
            try
            {
                string current = GetUserPath() ?? string.Empty;
                string updated = string.IsNullOrEmpty(current.TrimEnd(';'))
                    ? directory
                    : current.TrimEnd(';') + ";" + directory;
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(EnvironmentKey))
                {
                    if (key == null) return false;
                    key.SetValue("Path", updated, RegistryValueKind.ExpandString);
                }
                Log.Info("已将 {0} 加入用户 PATH", directory);
                RefreshProcessPath();
                BroadcastEnvironmentChange();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("写入用户 PATH 失败: {0}", ex.Message);
                return false;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

        /// <summary>Tell other processes the environment changed, without forcing a reboot.</summary>
        public static void BroadcastEnvironmentChange()
        {
            try
            {
                UIntPtr result;
                SendMessageTimeout(new IntPtr(0xffff), 0x001A /* WM_SETTINGCHANGE */, UIntPtr.Zero,
                    "Environment", 0x0002 /* SMTO_ABORTIFHUNG */, 2000, out result);
            }
            catch (Exception ex)
            {
                Log.Debug("广播环境变更失败: {0}", ex.Message);
            }
        }
    }
}
