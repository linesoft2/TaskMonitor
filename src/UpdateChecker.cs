using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;

namespace task_monitor
{
    /// <summary>
    /// Startup update check (设置 → 通用 → 检查更新 / 更新源). Once per launch, on a pool
    /// thread, reads the latest release tag from the configured source and, when it is
    /// newer than <see cref="VersionInfo.CurrentVersion"/>, pops an
    /// <see cref="UpdateAvailableDialog"/> on the UI thread (立即更新 opens the releases
    /// page; 不再提醒 persists that exact tag to settings.yaml and it is never prompted
    /// again — a still newer one is). Everything here is best-effort: any failure is
    /// logged and swallowed — the check must never crash, block, or spam the app.
    ///
    /// A check that fails to reach the host at all is retried ONCE, <see cref="RetryDelay"/>
    /// later (see <see cref="ScheduleRetry"/>) — the logon launch is early enough that
    /// Wi-Fi/VPN/DHCP often is not up yet, and a transient miss would otherwise cost the
    /// whole session's update prompt. An answer we simply cannot use (403 rate limit, a
    /// changed page structure) is NOT retried: five minutes changes nothing about it.
    ///
    /// Sources:
    ///  - "github": the releases JSON API — anonymous reads are allowed (a User-Agent
    ///    is required or GitHub 403s).
    ///  - "cnb" (the default): CNB's OpenAPI requires a login even for PUBLIC repos
    ///    (401 anonymous — every releases endpoint declares BearerAuth in the official
    ///    swagger), so the latest tag is read off the WEB layer instead: the
    ///    /-/releases/latest page 307-redirects to .../-/releases/tag/&lt;tag&gt;
    ///    (GitHub's convention) — one HEAD request, tag from the Location header;
    ///    if that redirect ever goes away, the releases list page HTML is scraped for
    ///    tag links as a fallback (the max matched version wins, so page ordering
    ///    doesn't matter). Anonymous release ASSET downloads work, so the releases
    ///    page the dialog opens is all the user needs.
    /// </summary>
    public static class UpdateChecker
    {
        private const string GithubApiLatest = "https://api.github.com/repos/linesoft2/TaskMonitor/releases/latest";
        private const string GithubReleasesPage = "https://github.com/linesoft2/TaskMonitor/releases/latest";
        private const string CnbLatestReleasePage = "https://cnb.cool/linesoft2/TaskMonitor/-/releases/latest";
        private const string CnbReleasesPage = "https://cnb.cool/linesoft2/TaskMonitor/-/releases";

        // Release-tag URL anchor. Used twice: on the /releases/latest 307 Location header
        // (primary) and on the releases list page HTML (fallback). The full repo path in
        // the anchor rules out cross-repo matches on the list page.
        private static readonly Regex CnbTagLink = new Regex(
            @"/linesoft2/TaskMonitor/-/releases/tag/(v?\d+(?:\.\d+){1,3})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>How long after a failed first attempt the single retry runs.</summary>
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

        // The pending retry, null when none is armed. A one-shot System.Threading.Timer —
        // never a sleeping pool thread (5 minutes would occupy one of them) and never a
        // WPF timer (this has nothing to do with the UI thread). Process-scoped: it dies
        // with the process, which is exactly the lifetime wanted.
        private static Timer _retryTimer;

        [DataContract]
        private sealed class GithubRelease
        {
            [DataMember(Name = "tag_name")] public string TagName { get; set; }
        }

        /// <summary>Kick off the one-per-startup check. Returns immediately; the fetch
        /// runs on a pool thread and any dialog is marshaled to the UI thread.</summary>
        public static void CheckOnce(AppSettings config, Action saveConfig)
        {
            if (config.UpdateCheckEnabled == false) return;   // 设置 → 通用 → 检查更新 off
            QueueCheck(config, saveConfig, canRetry: true);
        }

        private static void QueueCheck(AppSettings config, Action saveConfig, bool canRetry)
        {
            ThreadPool.QueueUserWorkItem(_ => CheckOnPoolThread(config, saveConfig, canRetry));
        }

        private static void CheckOnPoolThread(AppSettings config, Action saveConfig, bool canRetry)
        {
            // Read the LIVE settings, not a snapshot: this also runs as the retry, five
            // minutes after startup, so 检查更新 may have been switched off (cancel it —
            // "off means zero traffic" would otherwise quietly leak a request) or the
            // source changed (honor the new one) in the meantime.
            if (config.UpdateCheckEnabled == false) return;
            bool github = string.Equals(config.UpdateSource, "github", StringComparison.OrdinalIgnoreCase);
            string source = github ? "github" : "cnb";
            string latestTag;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                latestTag = github ? FetchGithubLatestTag() : FetchCnbLatestTag();
            }
            catch (Exception ex)
            {
                // Only an unreachable host earns the retry — see IsNetworkFailure.
                if (canRetry && IsNetworkFailure(ex))
                {
                    Logger.Warn($"更新检测：{source} 源连接失败，{RetryDelay.TotalMinutes:0} 分钟后重试一次", ex);
                    ScheduleRetry(config, saveConfig);
                }
                else
                {
                    Logger.Warn($"更新检测：{source} 源读取失败（下次启动重试）", ex);
                }
                return;
            }
            if (string.IsNullOrEmpty(latestTag))
            {
                Logger.Warn($"更新检测：{source} 源未找到版本号（页面结构可能已变化）");
                return;
            }

            if (!Version.TryParse(latestTag.TrimStart('v', 'V'), out Version latest))
            {
                // A tag we cannot parse is NOT "up to date" — saying so would hide a broken
                // release scheme (or a scrape that matched something that is not a version)
                // behind the healthiest-looking line in the log (§5).
                Logger.Warn($"更新检测：{source} 的 tag「{latestTag}」无法解析为版本号，跳过本次检查");
                return;
            }
            if (latest <= VersionInfo.CurrentVersion)
            {
                Logger.Info($"更新检测：已是最新（当前 {VersionInfo.Current}，{source} latest {latestTag}）");
                return;
            }
            if (string.Equals(config.IgnoredUpdateVersion, latestTag, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"更新检测：{latestTag} 已被设为不再提醒，跳过");
                return;
            }
            Logger.Info($"更新检测：发现新版本 {latestTag}（当前 {VersionInfo.Current}，源 {source}）——弹出提醒");

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(new Action(() => ShowPrompt(config, saveConfig, github, latestTag)));
        }

        // One-shot retry, <see cref="RetryDelay"/> out. Armed DISABLED and then Change()d so
        // the callback can never run before _retryTimer holds it (a 5-minute race is
        // theoretical, but the ordering costs nothing). Single-shot is the contract: the
        // retry passes canRetry:false, so a second failure logs and gives up until the next
        // launch — deliberately, this is a startup nicety, not a poller.
        private static void ScheduleRetry(AppSettings config, Action saveConfig)
        {
            try
            {
                Timer timer = null;
                timer = new Timer(_ =>
                {
                    timer.Dispose();
                    _retryTimer = null;
                    try { CheckOnPoolThread(config, saveConfig, canRetry: false); }
                    catch (Exception ex) { Logger.Warn("更新检测：重试失败", ex); }
                }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _retryTimer = timer;
                timer.Change(RetryDelay, Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex) { Logger.Warn("更新检测：重试未能安排", ex); }
        }

        // "We could not reach the host" as opposed to "the host answered with something we
        // cannot use" (403 rate limit, 404, changed page structure) — only the former is
        // worth a second attempt. ProtocolError/TrustFailure are therefore NOT retried,
        // while the DNS/connect/timeout/TLS-handshake family is; an inner
        // SocketException/IOException counts too, since a dropped connection surfaces as
        // WebExceptionStatus.UnknownError with the real cause nested. Status codes, never
        // message text — the message is localized (this machine logs Chinese).
        private static bool IsNetworkFailure(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is SocketException || e is IOException) return true;
                if (!(e is WebException we)) continue;
                switch (we.Status)
                {
                    case WebExceptionStatus.Timeout:
                    case WebExceptionStatus.ConnectFailure:
                    case WebExceptionStatus.NameResolutionFailure:
                    case WebExceptionStatus.ProxyNameResolutionFailure:
                    case WebExceptionStatus.ConnectionClosed:
                    case WebExceptionStatus.KeepAliveFailure:
                    case WebExceptionStatus.SecureChannelFailure:
                        return true;
                }
            }
            return false;
        }

        // UI thread. ShowDialog blocks only this dispatcher frame; all three outcomes are
        // read off the dialog's Result (✕ / Esc = Later, nothing persisted).
        private static void ShowPrompt(AppSettings config, Action saveConfig, bool github, string latestTag)
        {
            try
            {
                var dlg = new UpdateAvailableDialog(VersionInfo.Current, latestTag);
                dlg.ShowDialog();
                switch (dlg.Result)
                {
                    case UpdateAvailableDialog.Choice.Update:
                        string url = github ? GithubReleasesPage : CnbLatestReleasePage;
                        Logger.Info($"更新检测：用户选择立即更新——打开发布页 {url}");
                        try { Process.Start(url); }
                        catch (Exception ex) { Logger.Warn("打开发布页失败", ex); }
                        break;
                    case UpdateAvailableDialog.Choice.Ignore:
                        Logger.Info($"更新检测：用户选择不再提醒 {latestTag}");
                        config.IgnoredUpdateVersion = latestTag;
                        saveConfig();
                        break;
                    // Later: nothing persisted — the next startup asks again.
                }
            }
            catch (Exception ex) { Logger.Warn("更新提醒显示失败", ex); }
        }

        private static string FetchGithubLatestTag()
        {
            var req = (HttpWebRequest)WebRequest.Create(GithubApiLatest);
            ApplyHeaders(req, "application/vnd.github+json");
            using (var resp = req.GetResponse())
            using (var stream = resp.GetResponseStream())
            {
                var ser = new DataContractJsonSerializer(typeof(GithubRelease));
                return ((GithubRelease)ser.ReadObject(stream))?.TagName;
            }
        }

        // Primary: CNB's /-/releases/latest web page 307-redirects to
        // .../-/releases/tag/<tag> (same convention as GitHub) — a single HEAD request,
        // the tag comes off the Location header, no HTML parsing at all.
        // Fallback: scrape the releases list page for tag links (in case the redirect
        // ever goes away). Either way a miss is a graceful "check skipped", not an error.
        private static string FetchCnbLatestTag()
        {
            string tag = FetchCnbTagFromLatestRedirect();
            if (tag != null) return tag;
            Logger.Warn("更新检测：cnb latest 重定向未给出 tag，回退到刮 releases 列表页");
            return FetchCnbTagFromListPage();
        }

        private static string FetchCnbTagFromLatestRedirect()
        {
            var req = (HttpWebRequest)WebRequest.Create(CnbLatestReleasePage);
            req.Method = "HEAD";
            req.AllowAutoRedirect = false;   // the tag IS the redirect — don't follow it
            ApplyHeaders(req, "text/html");
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                int code = (int)resp.StatusCode;
                if (code < 300 || code >= 400) return null;
                var m = CnbTagLink.Match(resp.Headers["Location"] ?? "");
                return m.Success ? m.Groups[1].Value : null;
            }
        }

        private static string FetchCnbTagFromListPage()
        {
            var req = (HttpWebRequest)WebRequest.Create(CnbReleasesPage);
            ApplyHeaders(req, "text/html");
            string html;
            using (var resp = req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                html = reader.ReadToEnd();

            Version best = null;
            string bestTag = null;
            foreach (Match m in CnbTagLink.Matches(html))
            {
                if (!Version.TryParse(m.Groups[1].Value.TrimStart('v', 'V'), out Version v)) continue;
                if (best == null || v > best) { best = v; bestTag = m.Groups[1].Value; }
            }
            return bestTag;
        }

        // System proxy is honored by default (unlike ClashSampler's loopback endpoint,
        // these are public-internet URLs — a user's proxy SHOULD apply). 10s cap keeps a
        // hung connection from leaking a pool thread forever.
        private static void ApplyHeaders(HttpWebRequest req, string accept)
        {
            req.UserAgent = "TaskMonitor/" + VersionInfo.Current;
            req.Accept = accept;
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
        }
    }
}
