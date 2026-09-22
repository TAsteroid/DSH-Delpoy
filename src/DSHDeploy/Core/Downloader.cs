using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;

namespace DSHDeploy.Core
{
    public sealed class DownloadFailure : Exception
    {
        public readonly List<string> Attempts = new List<string>();

        public DownloadFailure(string message, List<string> attempts)
            : base(message)
        {
            if (attempts != null) Attempts.AddRange(attempts);
        }

        public string AttemptDetail()
        {
            return Attempts.Count == 0 ? "(没有可用源)" : string.Join(Environment.NewLine, Attempts);
        }
    }

    /// <summary>
    /// Downloads with mirror fallback, bounded retries and optional integrity checking.
    /// HTTPS is required for every host except the two used for region lookup (see Region.cs).
    /// </summary>
    public static class Downloader
    {
        /// <summary>Retry attempts per URL.</summary>
        public const int RetriesPerUrl = 3;

        static Downloader()
        {
            // Several mirrors and the -for-windows installers still need TLS 1.2 explicitly
            // on older Windows images.
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }
        }

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Deploy/" + AppInfo.Version);
            return client;
        }

        /// <summary>Download the first URL that yields a usable file. Returns the local path.</summary>
        public static string DownloadFile(
            IEnumerable<string> urls,
            string destinationDirectory,
            string suggestedName,
            string expectedSha256 = null,
            IUi ui = null,
            TimeSpan? timeout = null)
        {
            List<string> attempts = new List<string>();
            Directory.CreateDirectory(destinationDirectory);
            bool first = true;

            foreach (string url in urls)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (first)
                {
                    // C# 5 target: no null-conditional operator.
                    if (ui != null) ui.SetStatus("正在下载 " + suggestedName);
                    first = false;
                }
                string target = Path.Combine(destinationDirectory, suggestedName);

                for (int attempt = 1; attempt <= RetriesPerUrl; attempt++)
                {
                    try
                    {
                        Log.Info("下载 {0} (第 {1}/{2} 次)", url, attempt, RetriesPerUrl);
                        DownloadOnce(url, target, timeout ?? TimeSpan.FromMinutes(10));

                        var info = new FileInfo(target);
                        if (!info.Exists || info.Length == 0)
                            throw new IOException("下载结果为空");

                        if (!string.IsNullOrEmpty(expectedSha256))
                        {
                            string actual = Sha256File(target);
                            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                            {
                                TryDelete(target);
                                throw new InvalidDataException(
                                    "SHA-256 校验失败: 期望 " + expectedSha256 + ", 实际 " + actual);
                            }
                            Log.Info("SHA-256 校验通过: {0}", actual);
                        }

                        Log.Info("已下载 {0} ({1:N0} 字节)", target, info.Length);
                        return target;
                    }
                    catch (Exception ex)
                    {
                        attempts.Add("  " + url + "  =>  " + ex.Message);
                        Log.Warn("下载失败: {0} ({1})", url, ex.Message);
                        TryDelete(target);
                        if (attempt < RetriesPerUrl)
                        {
                            Thread.Sleep(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                        }
                    }
                }
            }

            throw new DownloadFailure("无法从任何镜像下载 " + suggestedName, attempts);
        }

        private static void DownloadOnce(string url, string target, TimeSpan timeout)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝非 HTTPS 下载地址: " + url);

            using (HttpClient client = CreateClient(timeout))
            using (HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using (Stream remote = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var local = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    remote.CopyTo(local);
                }
            }
        }

        /// <summary>Fetch a small text resource (version lists, checksum files). Returns null on failure.</summary>
        public static string TryGetString(string url, TimeSpan? timeout = null)
        {
            try
            {
                using (HttpClient client = CreateClient(timeout ?? TimeSpan.FromSeconds(20)))
                {
                    return client.GetStringAsync(url).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                Log.Debug("GET {0} 失败: {1}", url, ex.Message);
                return null;
            }
        }

        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Pick a hash out of an npm/github <c>SHASUMS256.txt</c> line list.</summary>
        public static string ParseShaSum(string shasumsText, string fileName)
        {
            if (string.IsNullOrEmpty(shasumsText) || string.IsNullOrEmpty(fileName)) return null;
            foreach (string raw in shasumsText.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int split = line.IndexOfAny(new[] { ' ', '\t' });
                if (split <= 0) continue;
                string hash = line.Substring(0, split).Trim();
                string name = line.Substring(split).Trim();
                if (name.StartsWith("*", StringComparison.Ordinal)) name = name.Substring(1);
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return hash;
            }
            return null;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static string TempDirectory(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "DSH-Deploy", name);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
