using System;
using System.Collections.Generic;

namespace DSHDeploy.Core
{
    /// <summary>
    /// Every download address the deployer can use. Each mirror group has a curated candidate
    /// list; the actual order comes from the responsiveness probe in <see cref="SourceProbe"/>,
    /// so these lists serve as both the probe targets and the fallback ordering.
    /// </summary>
    public static class Sources
    {
        public const string ChinaNpmRegistry = "https://registry.npmmirror.com";
        public const string GlobalNpmRegistry = "https://registry.npmjs.org";
        public const string ChinaNodeMirror = "https://registry.npmmirror.com/-/binary/node";
        public const string GlobalNodeMirror = "https://nodejs.org/dist";
        public const string ChinaGitMirror = "https://registry.npmmirror.com/-/binary/git-for-windows";
        public const string TunaGitMirror = "https://mirrors.tuna.tsinghua.edu.cn/github-release/git-for-windows/git";
        public const string GlobalGitReleases = "https://github.com/git-for-windows/git/releases/download";
        public const string GitHubRoot = "https://github.com";

        /// <summary>Git for Windows release this deployer installs (verified present on the mirrors).</summary>
        public const string GitTag = "v2.55.0.windows.5";
        public const string GitAssetVersion = "2.55.0.5";

        /// <summary>
        /// Probe targets: a small, real artifact per host group. A recent Node release file is
        /// used for the Node mirrors because every one of them serves it and it is only a few
        /// kilobytes, so the measurement is fast but still exercises the real download path.
        /// </summary>
        public static List<ProbeTarget> ProbeTargets(string nodeVersion)
        {
            nodeVersion = string.IsNullOrWhiteSpace(nodeVersion) ? NodeRelease.Version : nodeVersion;
            string nodeFile = "v" + nodeVersion + "/SHASUMS256.txt";

            return new List<ProbeTarget>
            {
                new ProbeTarget
                {
                    Host = ChinaNpmRegistry,
                    Label = Sources.LabelOf(ChinaNpmRegistry),
                    Region = Region.China,
                    SampleUrl = ChinaNodeMirror + "/" + nodeFile,
                    Measures = new[]
                    {
                        SourceKind.NpmRegistry, SourceKind.NodeMirror,
                        SourceKind.PnpmMirror, SourceKind.GitMirror
                    }
                },
                new ProbeTarget
                {
                    Host = GlobalNodeMirror,
                    Label = Sources.LabelOf(GlobalNodeMirror),
                    Region = Region.Global,
                    SampleUrl = GlobalNodeMirror + "/" + nodeFile,
                    Measures = new[] { SourceKind.NodeMirror }
                },
                new ProbeTarget
                {
                    Host = GlobalNpmRegistry,
                    Label = Sources.LabelOf(GlobalNpmRegistry),
                    Region = Region.Global,
                    SampleUrl = GlobalNpmRegistry + "/@deepseek-ai%2Fdsh/" + DshRelease.DefaultSpec,
                    Measures = new[] { SourceKind.NpmRegistry, SourceKind.PnpmMirror }
                },
                new ProbeTarget
                {
                    // A real artifact, not the releases page: the page is a few dozen bytes of
                    // HTML behind a redirect and would report meaningless throughput.
                    Host = GitHubRoot,
                    Label = Sources.LabelOf(GitHubRoot),
                    Region = Region.Global,
                    SampleUrl = GitHubRoot + "/pnpm/pnpm/releases/latest/download/" + PnpmStandaloneFileName(),
                    Measures = new[] { SourceKind.GitMirror, SourceKind.PnpmMirror }
                }
            };
        }

        /// <summary>Curated fallback order for one mirror group, used when nothing could be measured.</summary>
        public static List<string> DefaultHosts(SourceKind kind, Region region)
        {
            bool china = region == Region.China;
            switch (kind)
            {
                case SourceKind.NodeMirror:
                    return china
                        ? new List<string> { ChinaNodeMirror, GlobalNodeMirror }
                        : new List<string> { GlobalNodeMirror, ChinaNodeMirror };
                case SourceKind.GitMirror:
                    return china
                        ? new List<string> { ChinaGitMirror, TunaGitMirror, GlobalGitReleases }
                        : new List<string> { GlobalGitReleases, TunaGitMirror, ChinaGitMirror };
                case SourceKind.PnpmMirror:
                    return new List<string> { ChinaNpmRegistry, GlobalNpmRegistry, GitHubRoot };
                default:
                    return china
                        ? new List<string> { ChinaNpmRegistry, GlobalNpmRegistry }
                        : new List<string> { GlobalNpmRegistry, ChinaNpmRegistry };
            }
        }

        public static Region RegionOf(string host)
        {
            if (host == null) return Region.Global;
            if (host.StartsWith(ChinaNpmRegistry, StringComparison.OrdinalIgnoreCase)) return Region.China;
            if (host.StartsWith(TunaGitMirror, StringComparison.OrdinalIgnoreCase)) return Region.China;
            return Region.Global;
        }

        /// <summary>Human label for a host, used in messages.</summary>
        public static string LabelOf(string host)
        {
            if (string.IsNullOrEmpty(host)) return "(未指定)";
            try { return new Uri(host).Host; }
            catch { return host; }
        }

        // ------------------------------------------------------------------ url builders

        public static string GitUrlForHost(string host, string asset)
        {
            if (host == null) return null;
            if (host.Equals(ChinaGitMirror, StringComparison.OrdinalIgnoreCase))
                return ChinaGitMirror + "/" + GitTag + "/" + asset;
            if (host.Equals(TunaGitMirror, StringComparison.OrdinalIgnoreCase))
                return TunaGitMirror + "/Git%20for%20Windows%20" + GitTag + "/" + asset;
            if (host.Equals(GlobalGitReleases, StringComparison.OrdinalIgnoreCase))
                return GlobalGitReleases + "/" + GitTag + "/" + asset;
            return null;
        }

        public static string NodeUrlForHost(string host, string version, string fileName)
        {
            return host == null ? null : host + "/v" + version + "/" + fileName;
        }

        public static string PnpmStandaloneUrlForHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;
            // Verified against pnpm's GitHub release assets: Windows ships .zip archives, not a
            // bare .exe. npmmirror does not mirror these, so GitHub is the only source.
            if (host.Equals(GitHubRoot, StringComparison.OrdinalIgnoreCase))
                return GitHubRoot + "/pnpm/pnpm/releases/latest/download/" + PnpmStandaloneFileName();
            return null;
        }

        public static string NodeArchToken()
        {
            string arch = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? string.Empty).ToUpperInvariant();
            if (arch.Contains("ARM64")) return "arm64";
            if (arch.Contains("X86") && !arch.Contains("AMD64")) return "x86";
            return "x64";
        }

        public static bool IsArm64()
        {
            return NodeArchToken() == "arm64";
        }

        public static string NodeMsiFileName(string version = null)
        {
            version = string.IsNullOrWhiteSpace(version) ? NodeRelease.Version : version;
            return "node-v" + version + "-" + NodeArchToken() + ".msi";
        }

        public static string NodeZipFileName(string version = null)
        {
            version = string.IsNullOrWhiteSpace(version) ? NodeRelease.Version : version;
            return "node-v" + version + "-win-" + NodeArchToken() + ".zip";
        }

        public static string GitInstallerFileName()
        {
            return "Git-" + GitAssetVersion + "-" + (IsArm64() ? "arm64" : "64-bit") + ".exe";
        }

        /// <summary>
        /// pnpm's Windows release archive name. Verified against the published release assets:
        /// the platform token is <c>win32</c> and the archive is a zip containing pnpm.exe.
        /// </summary>
        public static string PnpmStandaloneFileName()
        {
            return "pnpm-" + (IsArm64() ? "win32-arm64" : "win32-x64") + ".zip";
        }
    }

    /// <summary>One host to measure, with the sample URL used and the groups it decides.</summary>
    public sealed class ProbeTarget
    {
        /// <summary>The rooted host this measurement ranks.</summary>
        public string Host { get; set; }
        public string Label { get; set; }
        public Region Region { get; set; }
        public string SampleUrl { get; set; }
        public SourceKind[] Measures { get; set; }
    }
}
