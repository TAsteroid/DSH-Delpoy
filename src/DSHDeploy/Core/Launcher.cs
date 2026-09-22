using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace DSHDeploy.Core
{
    public sealed class LaunchResult
    {
        public bool Running { get; set; }
        public bool Started { get; set; }
        public string Url { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Hands the machine over to DSH: starts <c>dsh web</c> detached, waits for the port to
    /// accept connections, and opens the browser. An already-listening port is treated as
    /// "DSH is running", not an error.
    /// </summary>
    public static class Launcher
    {
        public const string Host = "127.0.0.1";

        public static string UrlFor(int port)
        {
            return "http://" + Host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/";
        }

        public static LaunchResult Launch(int port, IUi ui, bool noLaunch, string workingDirectory = null)
        {
            string url = UrlFor(port);

            if (IsListening(port))
            {
                Log.Info("端口 {0} 已在监听, 视为 DSH 正在运行", port);
                return new LaunchResult
                {
                    Running = true,
                    Started = false,
                    Url = url,
                    Message = noLaunch
                        ? "DSH 已在运行: " + url + " (已按要求未打开浏览器)"
                        : "DSH 已在运行, 已打开 " + url
                };
            }

            if (noLaunch)
            {
                return new LaunchResult
                {
                    Running = false,
                    Started = false,
                    Url = url,
                    Message = "已按要求跳过启动。手动启动命令: dsh web --port " + port
                };
            }

            string dsh = Which.Find("dsh");
            if (dsh == null)
            {
                return new LaunchResult
                {
                    Running = false,
                    Started = false,
                    Url = url,
                    Message = "未找到 dsh 命令, 无法启动"
                };
            }

            ui.SetStatus("正在启动 DSH");
            string arguments = "web --port " + port.ToString(CultureInfo.InvariantCulture);
            Log.Info("启动: {0} {1}", dsh, arguments);

            // Let dsh open its own browser: the startup URL carries a one-time process token that
            // establishes the session cookie, so we must not substitute a bare URL ourselves.
            if (!Proc.LaunchDetached(dsh, arguments, workingDirectory))
            {
                return new LaunchResult
                {
                    Running = false,
                    Started = false,
                    Url = url,
                    Message = "启动 dsh 失败, 请手动运行: dsh " + arguments
                };
            }

            if (WaitForPort(port, TimeSpan.FromSeconds(90)))
            {
                Log.Info("DSH 已在 {0} 就绪", url);
                return new LaunchResult
                {
                    Running = true,
                    Started = true,
                    Url = url,
                    Message = "DSH 已启动: " + url
                };
            }

            string diagnostics = NewestStartupLog();
            string detail = diagnostics == null
                ? "未能在 90 秒内检测到端口 " + port
                : "未能在 90 秒内检测到端口 " + port + ", 最近一次启动诊断: " + diagnostics;

            Log.Error("{0}", detail);
            return new LaunchResult { Running = false, Started = true, Url = url, Message = detail };
        }

        private static string NewestStartupLog()
        {
            try
            {
                string dir = Path.Combine(MarketInstaller.ResolveDshHome(null), "logs");
                if (!Directory.Exists(dir)) return null;
                var newest = new DirectoryInfo(dir).GetFiles("startup-*.log");
                if (newest.Length == 0) return null;
                Array.Sort(newest, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                return newest[0].FullName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when something is already listening on the port (IPv4 or IPv6 loopback).</summary>
        public static bool IsListening(int port)
        {
            try
            {
                IPEndPoint[] listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                foreach (IPEndPoint endpoint in listeners)
                {
                    if (endpoint.Port != port) continue;
                    if (IPAddress.IsLoopback(endpoint.Address)
                        || endpoint.Address.Equals(IPAddress.Any)
                        || endpoint.Address.Equals(IPAddress.IPv6Any))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("查询监听端口失败: {0}", ex.Message);
            }
            return CanConnect(port);
        }

        public static bool WaitForPort(int port, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            var nextReport = DateTime.UtcNow;
            while (DateTime.UtcNow < deadline)
            {
                if (CanConnect(port)) return true;
                if (DateTime.UtcNow >= nextReport)
                {
                    Log.Debug("等待 DSH 监听端口 {0} ...", port);
                    nextReport = DateTime.UtcNow.AddSeconds(5);
                }
                Thread.Sleep(500);
            }
            return CanConnect(port);
        }

        private static bool CanConnect(int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    IAsyncResult result = client.BeginConnect(Host, port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(700)) return false;
                    client.EndConnect(result);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Best-effort elevation check, mirroring what the app manifest requests.</summary>
        public static bool IsElevated()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
