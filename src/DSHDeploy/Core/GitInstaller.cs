using System;
using System.IO;

namespace DSHDeploy.Core
{
    /// <summary>
    /// Installs Git for Windows with the Inno Setup silent switches. The installer is fetched
    /// from a China mirror or GitHub depending on the detected region.
    /// </summary>
    public static class GitInstaller
    {
        /// <summary>
        /// Silent, non-interactive switches. <c>/VERYSILENT</c> suppresses every page;
        /// <c>/NORESTART</c> keeps the machine usable right after deployment.
        /// </summary>
        public const string SilentSwitches = "/VERYSILENT /NORESTART /NOCANCEL /SP- /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS";

        public static bool Install(SourcePlan plan, IUi ui)
        {
            ui.SetStatus("正在安装 Git");
            string fileName = Sources.GitInstallerFileName();
            string cache = Downloader.TempDirectory("git");

            string installer;
            try
            {
                installer = Downloader.DownloadFile(GitUrls(plan, fileName), cache, fileName, null, ui);
            }
            catch (DownloadFailure failure)
            {
                Log.Error("Git 安装包下载失败:{0}{1}", Environment.NewLine, failure.AttemptDetail());
                return false;
            }

            string logPath = Path.Combine(Downloader.TempDirectory("logs"), "git-install.log");
            string arguments = SilentSwitches + " /LOG=\"" + logPath + "\"";
            Log.Info("执行 Git 安装: \"{0}\" {1}", installer, arguments);

            RunResult result = Proc.Run(installer, arguments, null, null, 900000);
            PathSync.RefreshProcessPath();

            if (result.ExitCode == 0 || result.ExitCode == 3010)
            {
                ui.SetProgress(45);
                Log.Info("Git 安装完成 (退出码 {0}, 日志: {1})", result.ExitCode, logPath);
            }
            else
            {
                Log.Error("Git 安装失败, 退出码 {0}", result.ExitCode);
                if (!string.IsNullOrEmpty(result.All)) Log.Error("安装器输出: {0}", result.All);
            }

            string git = GitInstaller.FindGit();
            if (git != null)
            {
                Log.Info("Git 可用: {0}", git);
                return true;
            }

            Log.Error("安装后仍未检测到 git.exe");
            return false;
        }

        /// <summary>
        /// Installer URLs in measured order, with the curated order appended so a bad
        /// measurement can never remove a working source.
        /// </summary>
        public static System.Collections.Generic.List<string> GitUrls(SourcePlan plan, string asset)
        {
            var urls = new System.Collections.Generic.List<string>();
            foreach (string host in plan.GitHosts)
            {
                AddUnique(urls, Sources.GitUrlForHost(host, asset));
            }
            foreach (string host in Sources.DefaultHosts(SourceKind.GitMirror, Region.Global))
            {
                AddUnique(urls, Sources.GitUrlForHost(host, asset));
            }
            return urls;
        }

        private static void AddUnique(System.Collections.Generic.List<string> urls, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!urls.Contains(url)) urls.Add(url);
        }

        /// <summary>
        /// Resolve git.exe specifically (not gitk/git-gui) across the well-known layouts,
        /// since a fresh install may not have refreshed our process PATH yet.
        /// </summary>
        public static string FindGit()
        {
            string direct = Which.Find("git");
            string gitExe = null;
            if (!string.IsNullOrEmpty(direct)
                && Path.GetFileName(direct).Equals("git.exe", StringComparison.OrdinalIgnoreCase))
            {
                gitExe = direct;
            }
            else if (!string.IsNullOrEmpty(direct))
            {
                // `git` resolved to cmd/git.cmd — fall through to the sibling exe when present.
                string sibling = Path.Combine(Path.GetDirectoryName(direct) ?? string.Empty, "git.exe");
                if (File.Exists(sibling)) gitExe = sibling;
            }

            if (gitExe != null && File.Exists(gitExe)) return gitExe;

            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe")
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
