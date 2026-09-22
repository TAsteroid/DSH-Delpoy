using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace DSHDeploy.Core
{
    /// <summary>
    /// Chooses where to download from by measuring how each candidate host actually responds
    /// from this machine, then taking the fastest.
    ///
    /// Deliberately <b>not</b> based on the system language: a user may run an English or
    /// Japanese Windows while sitting in China (or the reverse), so the UI language says nothing
    /// about which mirror will be fast. Each mirror group is ranked independently, so a machine
    /// that reaches one mirror quickly and another slowly gets the right host per group.
    ///
    /// An IP-based hint is consulted only to break a near-tie, never to select a slow host.
    /// </summary>
    public static class SourceProbe
    {
        /// <summary>Samples taken per host (first successful sample also bounds the wait).</summary>
        public const int Samples = 2;

        /// <summary>Per-sample ceiling; a host slower than this is unusable anyway.</summary>
        public const int SampleTimeoutMs = 6000;

        /// <summary>
        /// Maximum bytes read per sample. Large enough that the throughput figure reflects the
        /// real artifact rather than request overhead, small enough that probing a handful of
        /// hosts costs a couple of megabytes in total.
        /// </summary>
        public const int MaxSampleBytes = 512 * 1024;

        /// <summary>Relative gap below which two candidates count as tied.</summary>
        private const double TieThreshold = 0.15;

        /// <summary>Score penalty applied to a host outside the hinted region.</summary>
        private const double HintWeight = 0.15;

        static SourceProbe()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }
        }

        /// <summary>
        /// Build the mirror order. An explicit override short-circuits everything (no
        /// measurement, no network); otherwise every host is raced with <paramref name="ui"/>
        /// reporting progress.
        /// </summary>
        public static SourcePlan Resolve(string forcedRegion, IUi ui)
        {
            Region forced;
            if (!string.IsNullOrWhiteSpace(forcedRegion) && RegionDetector.TryParseRegion(forcedRegion, out forced))
            {
                return Forced(forced, "--region " + forcedRegion);
            }

            string env = Environment.GetEnvironmentVariable("DSH_DEPLOY_REGION");
            if (!string.IsNullOrWhiteSpace(env) && RegionDetector.TryParseRegion(env, out forced))
            {
                return Forced(forced, "DSH_DEPLOY_REGION=" + env);
            }

            var plan = new SourcePlan { Choice = SourceChoice.Default };
            List<ProbeTarget> targets = Sources.ProbeTargets(NodeRelease.Version);

            if (ui != null) ui.SetStatus("正在测速, 选择最快的下载源");
            int done = 0;
            foreach (ProbeTarget target in targets)
            {
                done++;
                if (ui != null)
                {
                    ui.SetStatus(string.Format(CultureInfo.InvariantCulture,
                        "正在测速 ({0}/{1}): {2}", done, targets.Count, target.Label));
                    ui.SetProgress(12 + done * 8 / Math.Max(1, targets.Count));
                }
                plan.Scores.Add(Measure(target));
            }

            string hintDetail;
            plan.Hint = TryIpHint(out hintDetail);
            Log.Debug("IP 归属提示: {0}", string.IsNullOrEmpty(hintDetail) ? "无" : hintDetail);

            int reachable = 0;
            foreach (SourceScore score in plan.Scores)
            {
                score.Score = ComputeScore(score, plan.Hint);
                if (score.Reachable) reachable++;
                Log.Info("测速 {0}: {1}", score.Label, score.Detail);
            }

            if (reachable < 2)
            {
                // Not enough evidence to order anything, so the curated default order stands.
                Region fallback = plan.Hint == RegionHint.China ? Region.China : Region.Global;
                plan.NpmHosts = Sources.DefaultHosts(SourceKind.NpmRegistry, fallback);
                plan.GitHosts = Sources.DefaultHosts(SourceKind.GitMirror, fallback);
                plan.Choice = SourceChoice.Default;
                plan.Detail = "可测速的下载源不足, 沿用默认顺序 (" + fallback + ")";
                return plan;
            }

            plan.NpmHosts = Rank(plan, SourceKind.NpmRegistry);
            plan.GitHosts = Rank(plan, SourceKind.GitMirror);
            plan.Choice = plan.HintUsed ? SourceChoice.Hint : SourceChoice.Measured;
            plan.Detail = "按本地测速结果排序";
            return plan;
        }

        private static SourcePlan Forced(Region region, string detail)
        {
            return new SourcePlan
            {
                Choice = SourceChoice.Forced,
                NpmHosts = Sources.DefaultHosts(SourceKind.NpmRegistry, region),
                GitHosts = Sources.DefaultHosts(SourceKind.GitMirror, region),
                Detail = "按 " + detail + " 指定, 未做测速"
            };
        }

        /// <summary>
        /// A plan that performs no measurement at all. Used when detection shows there is
        /// nothing to download, so the deferring case never pays for a speed test.
        /// </summary>
        public static SourcePlan DefaultPlan(string forcedRegion)
        {
            Region region = Region.Global;
            Region parsed;
            if (!string.IsNullOrWhiteSpace(forcedRegion) && RegionDetector.TryParseRegion(forcedRegion, out parsed))
            {
                region = parsed;
            }
            else
            {
                string env = Environment.GetEnvironmentVariable("DSH_DEPLOY_REGION");
                if (!string.IsNullOrWhiteSpace(env) && RegionDetector.TryParseRegion(env, out parsed)) region = parsed;
            }

            return new SourcePlan
            {
                Choice = SourceChoice.Default,
                NpmHosts = Sources.DefaultHosts(SourceKind.NpmRegistry, region),
                GitHosts = Sources.DefaultHosts(SourceKind.GitMirror, region),
                Detail = "无需下载, 未做测速"
            };
        }

        /// <summary>
        /// Hosts that decide a group, best first. The curated list is always appended, so a bad
        /// measurement can never remove a source that might still work.
        /// </summary>
        internal static List<string> Rank(SourcePlan plan, SourceKind kind)
        {
            List<string> fallback = Sources.DefaultHosts(kind, Region.Global);
            var best = new Dictionary<string, SourceScore>(StringComparer.OrdinalIgnoreCase);

            // A host sampled under several targets keeps its best (lowest) score.
            foreach (SourceScore score in plan.Scores)
            {
                if (!score.Reachable || !Decides(score, kind) || string.IsNullOrEmpty(score.Host)) continue;
                SourceScore current;
                if (!best.TryGetValue(score.Host, out current) || score.Score < current.Score)
                {
                    best[score.Host] = score;
                }
            }

            var ordered = new List<string>();
            if (best.Count > 0)
            {
                var ranking = new List<SourceScore>(best.Values);
                bool hintUsed = false;
                ranking.Sort((a, b) => Compare(a, b, plan.Hint, fallback, ref hintUsed));
                if (ranking.Count > 1 && IsTie(ranking[0].Score, ranking[1].Score))
                {
                    // The measurement cannot separate the leaders, so prefer the hinted region.
                    plan.HintUsed = true;
                    var byHint = new List<SourceScore>(ranking);
                    byHint.Sort((a, b) =>
                    {
                        int aRank = HintPenalty(a.Region, plan.Hint);
                        int bRank = HintPenalty(b.Region, plan.Hint);
                        return aRank != bRank ? aRank.CompareTo(bRank) : a.Score.CompareTo(b.Score);
                    });
                    ranking = byHint;
                }
                foreach (SourceScore score in ranking)
                {
                    if (!ordered.Contains(score.Host)) ordered.Add(score.Host);
                }
            }

            foreach (string host in fallback)
            {
                if (!ordered.Contains(host)) ordered.Add(host);
            }
            return ordered;
        }

        private static int Compare(SourceScore a, SourceScore b, RegionHint hint, List<string> fallback, ref bool hintUsed)
        {
            int byScore = a.Score.CompareTo(b.Score);
            if (byScore != 0) return byScore;
            int aRank = HintPenalty(a.Region, hint);
            int bRank = HintPenalty(b.Region, hint);
            if (aRank != bRank) return aRank.CompareTo(bRank);
            return fallback.IndexOf(a.Host).CompareTo(fallback.IndexOf(b.Host));
        }

        private static bool Decides(SourceScore score, SourceKind kind)
        {
            if (score.Measures == null) return false;
            foreach (SourceKind measured in score.Measures)
            {
                if (measured == kind) return true;
            }
            return false;
        }

        private static int HintPenalty(Region region, RegionHint hint)
        {
            if (hint == RegionHint.Unknown) return 1; // neutral: no host is favoured
            bool hinted = (hint == RegionHint.China) == (region == Region.China);
            return hinted ? 0 : 1;
        }

        private static bool IsTie(long a, long b)
        {
            if (a == long.MaxValue || b == long.MaxValue) return false;
            long worst = Math.Max(a, b);
            if (worst <= 0) return true;
            return (double)Math.Abs(a - b) / worst <= TieThreshold;
        }

        /// <summary>
        /// Relative importance of throughput versus latency when ranking sources. Throughput
        /// leads because installers are tens of megabytes; latency matters as the secondary
        /// signal, both because it reflects responsiveness and because it is measured on the
        /// same samples.
        /// </summary>
        private const double ThroughputWeight = 6.0;
        private const double LatencyWeight = 1.0;

        /// <summary>
        /// Lower is better. Both terms are quality factors (bigger = better), so the whole
        /// expression is reciprocated; summing an already-inverted latency term would let a host
        /// with a fraction of the throughput still outrank a clearly faster one.
        /// </summary>
        internal static long ComputeScore(SourceScore score, RegionHint hint)
        {
            if (!score.Reachable) return long.MaxValue;
            long latency = Math.Max(1, score.LatencyMs);
            long throughput = Math.Max(0, score.BytesPerMs);

            // Reference scales keep both terms O(1) so the weights mean what they say.
            const double ReferenceBytesPerMs = 50.0;
            const double ReferenceLatencyMs = 200.0;
            double throughputQuality = throughput / ReferenceBytesPerMs;
            double latencyQuality = ReferenceLatencyMs / latency;

            double quality = ThroughputWeight * throughputQuality + LatencyWeight * latencyQuality;
            if (quality <= 0) return long.MaxValue;

            double penalty = 1.0 + HintWeight * HintPenalty(score.Region, hint);
            double value = 100000.0 / quality * penalty;
            if (value <= 0) return long.MaxValue;
            if (value > (double)long.MaxValue / 1000.0) return long.MaxValue;
            return (long)(value * 1000.0);
        }

        /// <summary>Sample one host: time to first byte plus achieved throughput.</summary>
        public static SourceScore Measure(ProbeTarget target)
        {
            var score = new SourceScore
            {
                Host = target.Host,
                Label = target.Label,
                Region = target.Region,
                Measures = target.Measures,
                Score = long.MaxValue
            };

            var latencies = new List<long>();
            var rates = new List<long>();
            string lastError = null;

            for (int i = 0; i < Samples; i++)
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(target.SampleUrl);
                    request.Method = "GET";
                    request.Timeout = SampleTimeoutMs;
                    request.ReadWriteTimeout = SampleTimeoutMs;
                    request.UserAgent = "DSH-Deploy/" + AppInfo.Version + " (speed-test)";
                    request.AllowAutoRedirect = true;
                    request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                    var stopwatch = Stopwatch.StartNew();
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        long firstByteMs = stopwatch.ElapsedMilliseconds;
                        long bytes = 0;
                        using (var stream = response.GetResponseStream())
                        {
                            if (stream != null)
                            {
                                var buffer = new byte[8192];
                                int read;
                                while (bytes < MaxSampleBytes
                                       && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    bytes += read;
                                }
                            }
                        }
                        long totalMs = stopwatch.ElapsedMilliseconds;

                        if (bytes <= 0) continue;
                        latencies.Add(Math.Max(1, firstByteMs));
                        rates.Add(Math.Max(0, bytes / Math.Max(1, totalMs - firstByteMs)));
                        score.Reachable = true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Log.Debug("测速 {0} 失败: {1}", target.Label, ex.Message);
                }
            }

            if (score.Reachable)
            {
                score.LatencyMs = Median(latencies);
                score.BytesPerMs = Median(rates);
                var kinds = new List<string>();
                foreach (SourceKind kind in target.Measures) kinds.Add(kind.ToString());
                score.Detail = score.LatencyMs + "ms, " + score.BytesPerMs + " B/ms ["
                               + string.Join(",", kinds.ToArray()) + "]";
            }
            else
            {
                score.Detail = lastError == null ? "无响应" : "不可达: " + lastError;
            }
            return score;
        }

        private static long Median(List<long> values)
        {
            if (values.Count == 0) return 0;
            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>
        /// Country hint from a free lookup over HTTPS. This is a weak signal used only for
        /// near-ties; it never promotes a host that measured slow.
        /// </summary>
        public static RegionHint TryIpHint(out string detail)
        {
            detail = null;
            const string endpoint = "https://ipapi.co/json/";
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(endpoint);
                request.Timeout = 4000;
                request.ReadWriteTimeout = 4000;
                request.UserAgent = "DSH-Deploy/" + AppInfo.Version;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new System.IO.StreamReader(response.GetResponseStream()))
                {
                    string body = reader.ReadToEnd();
                    Match match = Regex.Match(body ?? string.Empty,
                        @"""country(?:_code)?""\s*:\s*""([A-Za-z]{2})""",
                        RegexOptions.CultureInvariant);
                    if (!match.Success) return RegionHint.Unknown;
                    string code = match.Groups[1].Value.ToUpperInvariant();
                    detail = code;
                    return code == "CN" ? RegionHint.China : RegionHint.Global;
                }
            }
            catch (Exception ex)
            {
                detail = "不可用 (" + ex.Message + ")";
                return RegionHint.Unknown;
            }
        }
    }

    /// <summary>Parses an explicit region override. The system language is never consulted.</summary>
    public static class RegionDetector
    {
        public static bool TryParseRegion(string text, out Region region)
        {
            region = Region.Global;
            if (string.IsNullOrWhiteSpace(text)) return false;
            switch (text.Trim().ToLowerInvariant())
            {
                case "cn":
                case "china":
                case "zh":
                    region = Region.China;
                    return true;
                case "global":
                case "intl":
                case "international":
                case "us":
                case "en":
                    region = Region.Global;
                    return true;
                default:
                    return false;
            }
        }
    }
}
