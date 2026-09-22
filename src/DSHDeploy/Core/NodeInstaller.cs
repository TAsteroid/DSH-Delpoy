using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32;

namespace DSHDeploy.Core
{
    /// <summary>
    /// Installs Node.js. The MSI is tried first because it publishes PATH correctly; a verified
    /// portable ZIP into the user profile is the fallback for locked-down machines.
    /// </summary>
    public static class NodeInstaller
    {
        public static bool Install(SourcePlan plan, IUi ui)
        {
            string version = NodeRelease.Version;
            ui.SetStatus("正在安装 Node.js " + version);
            string cache = Downloader.TempDirectory("node");

            string msiPath = null;
            try
            {
                msiPath = DownloadNodeMsi(plan, version, cache, ui);
            }
            catch (DownloadFailure failure)
            {
                Log.Error("Node.js 安装包下载失败:{0}{1}", Environment.NewLine, failure.AttemptDetail());
                Log.Warn("将跳过 MSI, 直接尝试便携版安装");
            }

            if (msiPath != null && InstallMsi(msiPath))
            {
                PathSync.RefreshProcessPath();
                if (NodeReady())
                {
                    ui.SetProgress(30);
                    Log.Info("Node.js 安装成功 (MSI)");
                    return true;
                }
                Log.Warn("MSI 返回成功, 但仍未检测到 node, 尝试便携版安装");
            }

            return InstallPortableZip(plan, version, cache, ui);
        }

        /// <summary>Node mirror URLs in measured order, curated order appended as fallback.</summary>
        public static List<string> NodeUrls(SourcePlan plan, string version, string fileName, bool shasums)
        {
            var urls = new List<string>();
            foreach (string host in plan.NpmHosts)
            {
                bool nodeHost = host.Equals(Sources.ChinaNodeMirror, StringComparison.OrdinalIgnoreCase)
                                || host.Equals(Sources.GlobalNodeMirror, StringComparison.OrdinalIgnoreCase);
                if (!nodeHost) continue;
                AddUnique(urls, Sources.NodeUrlForHost(host, version, shasums ? "SHASUMS256.txt" : fileName));
            }
            foreach (string host in Sources.DefaultHosts(SourceKind.NodeMirror, Region.Global))
            {
                AddUnique(urls, Sources.NodeUrlForHost(host, version, shasums ? "SHASUMS256.txt" : fileName));
            }
            return urls;
        }

        private static void AddUnique(List<string> urls, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!urls.Contains(url)) urls.Add(url);
        }

        private static string DownloadNodeMsi(SourcePlan plan, string version, string cache, IUi ui)
        {
            string fileName = Sources.NodeMsiFileName(version);
            string shasums = FetchShasums(plan, version);
            string expected = shasums == null ? null : Downloader.ParseShaSum(shasums, fileName);
            if (expected == null)
            {
                Log.Warn("未能取得 {0} 的官方校验值, 将仅依赖 HTTPS 传输", fileName);
            }
            return Downloader.DownloadFile(NodeUrls(plan, version, fileName, false), cache, fileName, expected, ui);
        }

        /// <summary>Try each mirror for SHASUMS256.txt; a missing checksum downgrades to HTTPS-only.</summary>
        public static string FetchShasums(SourcePlan plan, string version)
        {
            foreach (string url in NodeUrls(plan, version, null, true))
            {
                string text = Downloader.TryGetString(url);
                if (!string.IsNullOrEmpty(text)) return text;
            }
            return null;
        }

        private static bool InstallMsi(string msiPath)
        {
            string logPath = Path.Combine(Downloader.TempDirectory("logs"), "node-msi.log");
            string arguments = string.Format(
                "/i \"{0}\" /qn /norestart /l*v \"{1}\"",
                msiPath, logPath);

            Log.Info("执行 Node.js MSI 安装: msiexec.exe {0}", arguments);
            RunResult result = Proc.Run("msiexec.exe", arguments, null, null, 900000);

            // 0 = success, 3010 = success but a reboot is pending.
            if (result.ExitCode == 0 || result.ExitCode == 3010)
            {
                Log.Info("MSI 退出码 {0} (安装日志: {1})", result.ExitCode, logPath);
                return true;
            }

            Log.Error("MSI 安装失败, 退出码 {0}", result.ExitCode);
            if (!string.IsNullOrEmpty(result.All)) Log.Error("msiexec 输出: {0}", result.All);
            return false;
        }

        private static bool InstallPortableZip(SourcePlan plan, string version, string cache, IUi ui)
        {
            ui.SetStatus("正在安装便携版 Node.js");
            string fileName = Sources.NodeZipFileName(version);
            string shasums = FetchShasums(plan, version);
            string expected = shasums == null ? null : Downloader.ParseShaSum(shasums, fileName);

            string zipPath;
            try
            {
                zipPath = Downloader.DownloadFile(NodeUrls(plan, version, fileName, false), cache, fileName, expected, ui);
            }
            catch (DownloadFailure failure)
            {
                Log.Error("便携版 Node.js 下载失败:{0}{1}", Environment.NewLine, failure.AttemptDetail());
                return false;
            }

            string target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH", "node");
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.CreateDirectory(target);
                ExtractZipFlattened(zipPath, target);
            }
            catch (Exception ex)
            {
                Log.Error("解压 Node.js 失败: {0}", ex.Message);
                return false;
            }

            string readyPath;
            if (NodeReady(out readyPath) && IsOnPath(target))
            {
                Log.Info("Node.js 便携版安装成功: {0}", target);
                return true;
            }

            // Register the portable directory so future sessions find it too.
            PathSync.AddToUserPath(target);
            PathSync.RefreshProcessPath();
            if (!Environment.GetEnvironmentVariable("PATH").Contains(target))
            {
                Environment.SetEnvironmentVariable("PATH", target + ";" + Environment.GetEnvironmentVariable("PATH"));
            }

            if (NodeReady(out readyPath))
            {
                Log.Info("Node.js 便携版安装成功: {0}", target);
                return true;
            }

            Log.Error("便携版解压后仍未检测到 node.exe (目录: {0})", target);
            return false;
        }

        private static bool IsOnPath(string directory)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string part in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(PathSync.Normalize(part), PathSync.Normalize(directory), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The ZIP contains a single top-level <c>node-vX-win-x64</c> folder we must strip.</summary>
        private static void ExtractZipFlattened(string zipPath, string target)
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                string prefix = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.FullName)) continue;
                    int slash = entry.FullName.IndexOf('/');
                    string root = slash < 0 ? entry.FullName : entry.FullName.Substring(0, slash);
                    if (prefix == null) { prefix = root; continue; }
                    if (!string.Equals(prefix, root, StringComparison.OrdinalIgnoreCase)) { prefix = null; break; }
                }

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string relative = entry.FullName;
                    if (!string.IsNullOrEmpty(prefix) && relative.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        relative = relative.Substring(prefix.Length + 1);
                    }
                    if (string.IsNullOrEmpty(relative)) continue;

                    string destination = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }
                    string parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    entry.ExtractToFile(destination, true);
                }
            }
        }

        /// <summary>True when a usable node.exe is resolvable right now.</summary>
        public static bool NodeReady()
        {
            string ignored;
            return NodeReady(out ignored);
        }

        /// <summary>True when a usable node.exe is resolvable right now; reports its path.</summary>
        public static bool NodeReady(out string path)
        {
            path = Which.Find("node");
            if (path == null)
            {
                // A silent MSI publishes to the registry; pick that up before giving up.
                foreach (string dir in RegistryNodeDirectories())
                {
                    string candidate = Path.Combine(dir, "node.exe");
                    if (File.Exists(candidate)) { path = candidate; return true; }
                }
                return false;
            }
            return File.Exists(path);
        }

        private static IEnumerable<string> RegistryNodeDirectories()
        {
            var results = new List<string>();
            string[] keys =
            {
                @"SOFTWARE\Node.js",
                @"SOFTWARE\WOW6432Node\Node.js"
            };
            foreach (RegistryKey root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (string subKey in keys)
                {
                    try
                    {
                        using (RegistryKey key = root.OpenSubKey(subKey))
                        {
                            if (key == null) continue;
                            object value = key.GetValue("InstallPath") ?? key.GetValue("InstallDir");
                            string dir = value as string;
                            if (!string.IsNullOrEmpty(dir)) results.Add(dir.TrimEnd('\\'));
                        }
                    }
                    catch { }
                }
            }
            return results;
        }
    }
}
