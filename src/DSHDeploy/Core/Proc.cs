using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DSHDeploy.Core
{
    public sealed class RunResult
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; }
        public string StdErr { get; set; }
        public bool TimedOut { get; set; }

        public string All
        {
            get
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(StdOut)) sb.Append(StdOut);
                if (!string.IsNullOrEmpty(StdErr))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(StdErr);
                }
                return sb.ToString().Trim();
            }
        }

        public bool Ok { get { return ExitCode == 0 && !TimedOut; } }
    }

    /// <summary>
    /// Bounded child-process execution. Everything runs through an explicit absolute path:
    /// the PowerShell shims shipped by npm (<c>npm.ps1</c>, <c>dsh.ps1</c>) are unreliable on
    /// stock Windows, so we always drive the <c>.cmd</c> counterparts.
    /// </summary>
    public static class Proc
    {
        public static RunResult Run(
            string fileName,
            string arguments,
            string workingDirectory = null,
            IDictionary<string, string> environment = null,
            int timeoutMs = 600000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
            if (environment != null)
            {
                foreach (var kv in environment)
                {
                    if (kv.Value == null) psi.EnvironmentVariables.Remove(kv.Key);
                    else psi.EnvironmentVariables[kv.Key] = kv.Value;
                }
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var result = new RunResult();
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = psi;
                    var outDone = new System.Threading.AutoResetEvent(false);
                    var errDone = new System.Threading.AutoResetEvent(false);
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) outDone.Set(); else lock (stdout) stdout.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null) errDone.Set(); else lock (stderr) stderr.AppendLine(e.Data);
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    try { process.StandardInput.Close(); } catch { }

                    if (!process.WaitForExit(timeoutMs))
                    {
                        result.TimedOut = true;
                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(5000); } catch { }
                    }
                    outDone.WaitOne(2000);
                    errDone.WaitOne(2000);
                    if (!result.TimedOut)
                    {
                        try { result.ExitCode = process.ExitCode; } catch { result.ExitCode = -1; }
                    }
                    else
                    {
                        result.ExitCode = -1;
                    }
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.StdErr = ex.Message;
            }
            result.StdOut = stdout.ToString().Trim();
            result.StdErr = stderr.ToString().Trim();
            return result;
        }

        /// <summary>
        /// Start a process detached from this one so it survives the deployer exiting.
        /// Used to hand the DSH web server over to the user.
        /// </summary>
        public static bool LaunchDetached(string fileName, string arguments, string workingDirectory = null, IDictionary<string, string> environment = null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments ?? string.Empty,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
                if (environment != null)
                {
                    foreach (var kv in environment)
                    {
                        if (kv.Value != null) psi.EnvironmentVariables[kv.Key] = kv.Value;
                    }
                }
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("启动进程失败 {0}: {1}", fileName, ex.Message);
                return false;
            }
        }

        public static bool OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("拒绝打开非 HTTP 地址: {0}", url);
                return false;
            }
            try
            {
                var psi = new ProcessStartInfo { FileName = url, UseShellExecute = true };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("无法打开浏览器: {0}", ex.Message);
                return false;
            }
        }
    }

    /// <summary>Locates executables by absolute path, avoiding shell/PATH shim ambiguity.</summary>
    public static class Which
    {
        private static readonly string[] Extensions = { ".cmd", ".exe", ".bat", "" };

        /// <summary>Well-known install directories a fresh install may not have published to PATH yet.</summary>
        public static IEnumerable<string> KnownDirectories
        {
            get
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                yield return Path.Combine(pf, "nodejs");
                yield return Path.Combine(pf86, "nodejs");
                yield return Path.Combine(appData, "npm");
                yield return Path.Combine(pf, "Git", "cmd");
                yield return Path.Combine(pf86, "Git", "cmd");
                yield return Path.Combine(local, "Git", "cmd");
                yield return Path.Combine(local, "DSH", "node");
                yield return Path.Combine(local, "DSH", "pnpm");
                yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
            }
        }

        /// <summary>Resolve a tool to an absolute path, honouring the current PATH then known directories.</summary>
        public static string Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (File.Exists(name)) return name;

            foreach (string dir in SearchDirectories())
            {
                foreach (string ext in Extensions)
                {
                    try
                    {
                        string candidate = Path.Combine(dir, name + ext);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }
            return null;
        }

        private static IEnumerable<string> SearchDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string part in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim().Trim('"');
                if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
                yield return trimmed;
            }
            foreach (string dir in KnownDirectories)
            {
                if (!string.IsNullOrEmpty(dir) && seen.Add(dir)) yield return dir;
            }
        }
    }
}
