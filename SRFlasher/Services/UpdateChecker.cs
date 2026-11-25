using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SRFlasher.Services
{
    /// <summary>
    /// GitHub update checker. Queries the public releases API once a day
    /// (configurable off in Settings) and reports a newer release tag.
    /// Privacy: the only request is a plain GET to api.github.com — no
    /// device data, no serials, no logs ever leave the machine.
    /// </summary>
    internal static class UpdateChecker
    {
        public const string RepoOwner = "sreejith19931";
        public const string RepoName = "SRFlasher";
        /// <summary>Landing page for "open releases" buttons (user-facing).</summary>
        public const string ReleasesUrl = "https://github.com/" + RepoOwner + "/" + RepoName + "/releases";
        private const string LatestApiUrl = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

        public sealed class UpdateInfo
        {
            public string LatestTag { get; set; }
            public string ReleaseUrl { get; set; }
        }

        /// <summary>
        /// Decide whether a release tag is newer than the running version.
        /// Pure and internal so the test project can lock it down.
        /// Accepts "v1.1", "v1.1.0", "1.2", "V3" — anything Version.TryParse
        /// can read after the leading v is dropped. Garbage (null/empty/other
        /// text) is never reported as an update.
        /// </summary>
        internal static bool IsNewerVersion(string latestTag, string currentVersion)
        {
            if (string.IsNullOrWhiteSpace(latestTag) || string.IsNullOrWhiteSpace(currentVersion))
                return false;

            var latestTrimmed = latestTag.TrimStart();
            if (latestTrimmed.Length > 0 && (latestTrimmed[0] == 'v' || latestTrimmed[0] == 'V'))
                latestTrimmed = latestTrimmed.Substring(1);

            Version latest;
            Version current;
            if (!Version.TryParse(latestTrimmed.Trim(), out latest)) return false;
            if (!Version.TryParse(currentVersion, out current)) return false;

            return latest > current;
        }

        /// <summary>
        /// Ask GitHub for the latest published release. Returns null when the
        /// check was cancelled, no release exists yet, or anything failed —
        /// the caller treats "unknown" exactly like "up to date".
        /// </summary>
        public static async Task<UpdateInfo> FetchLatestAsync(CancellationToken ct)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(LatestApiUrl);
                request.UserAgent = RepoName + "/1.0 (update check; +https://github.com/" + RepoOwner + "/" + RepoName + ")";
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;
                request.Accept = "application/vnd.github+json";

                using (ct.Register(() => request.Abort()))
                using (var response = await request.GetResponseAsync().ConfigureAwait(false))
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var json = await reader.ReadToEndAsync().ConfigureAwait(false);
                    return ParseLatestRelease(json);
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception)
            {
                // Offline, rate-limited, proxy trouble: silent for the
                // automatic check, surfaced as a status line for manual ones.
                return null;
            }
        }

        /// <summary>
        /// Minimal JSON extraction — the releases/latest payload is well
        /// known: "tag_name":"v1.1" and an html_url pointing at the tag.
        /// No dependency on a JSON library, C# 7.3 friendly.
        /// </summary>
        internal static UpdateInfo ParseLatestRelease(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var tag = ExtractJsonString(json, "tag_name");
            if (string.IsNullOrEmpty(tag)) return null;

            // Prefer the release html_url; fall back to the plain releases page.
            var urlMarker = "html_url\":\"";
            var url = string.Empty;
            var searchStart = 0;
            while (true)
            {
                var at = json.IndexOf(urlMarker, searchStart, StringComparison.Ordinal);
                if (at < 0) break;
                var start = at + urlMarker.Length;
                var end = json.IndexOf('"', start);
                if (end < 0) break;
                var candidate = json.Substring(start, end - start);
                if (candidate.Contains("/releases/tag/") || candidate.EndsWith("/releases", StringComparison.Ordinal))
                {
                    url = candidate;
                    break;
                }
                searchStart = end;
            }

            return new UpdateInfo { LatestTag = tag, ReleaseUrl = string.IsNullOrEmpty(url) ? ReleasesUrl : url };
        }

        /// <summary>Read a top-level-or-nested "name":"value" pair as plain text.</summary>
        private static string ExtractJsonString(string json, string name)
        {
            var marker = "\"" + name + "\":\"";
            var at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return null;
            var start = at + marker.Length;
            var end = json.IndexOf('"', start);
            return end < 0 ? null : json.Substring(start, end - start);
        }
    }
}
