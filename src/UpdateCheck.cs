using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SmartBuffer
{
    public sealed class ReadyUpdate
    {
        public string Version { get; set; }
        public string Setup { get; set; }
    }

    // downloads a newer release in the background; installing still takes one click and one Windows "Yes",
    // since the app lives in Program Files. GitHub is only asked for the latest release, nothing about the user is sent
    public static class UpdateCheck
    {
        public const string ReleasesPage = "https://github.com/bwinslow1022/smart-buffer/releases/latest";
        private const string LatestApi = "https://api.github.com/repos/bwinslow1022/smart-buffer/releases/latest";
        private const string AssetName = "SmartBuffer-Setup.exe";
        private const int CacheHours = 6;
        private const int TimeoutMs = 10000;
        private const long MaxSetupBytes = 50L * 1000 * 1000;
        // every release lists its installer's hash, an update that doesn't match it is never run
        private static readonly Regex HashLine = new Regex(@"SHA-256:\s*`?([0-9a-fA-F]{64})");

        private static string CachePath(Context ctx) => Path.Combine(ctx.AppDir, "update-check.txt");
        private static string UpdateDir(Context ctx) => Path.Combine(ctx.AppDir, "update");

        /// A verified installer for a newer version, downloaded if needed, or null. Runs on the calling thread.
        public static ReadyUpdate Prepare(Context ctx)
        {
            try
            {
                string[] latest = Cached(ctx) ?? Fetch(ctx);
                if (latest == null || !IsNewer(latest[0], Assembly.GetExecutingAssembly().GetName().Version) || latest[1] == "" || latest[2] == "")
                {
                    return null;
                }
                string version = latest[0].TrimStart('v', 'V');
                string setup = Path.Combine(UpdateDir(ctx), "SmartBuffer-Setup-" + version + ".exe");
                if (!File.Exists(setup) || !HashMatches(setup, latest[2]))
                {
                    Download(ctx, latest[1], setup, latest[2]);
                }
                return new ReadyUpdate { Version = version, Setup = setup };
            }
            catch (Exception e) when (e is WebException || e is IOException || e is InvalidOperationException || e is ArgumentException || e is UnauthorizedAccessException)
            {
                ctx.Log("update check: " + e.Message);
                return null;
            }
        }

        /// Runs the installer silently, Windows asks for approval first. False if that was declined.
        public static bool Install(ReadyUpdate update)
        {
            try
            {
                Process.Start(new ProcessStartInfo(update.Setup, "/S") { UseShellExecute = true, Verb = "runas" })?.Dispose();
                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        public static bool IsNewer(string tag, Version current)
        {
            return Version.TryParse((tag ?? "").TrimStart('v', 'V'), out Version latest) && latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        }

        // tag, installer url, installer hash
        private static string[] Cached(Context ctx)
        {
            string path = CachePath(ctx);
            if (!File.Exists(path))
            {
                return null;
            }
            string[] parts = File.ReadAllText(path).Split('\t');
            return parts.Length == 4 && long.TryParse(parts[0], out long ticks) && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromHours(CacheHours)
                ? parts.Skip(1).Select(p => p.Trim()).ToArray()
                : null;
        }

        private static string[] Fetch(Context ctx)
        {
            using (var reader = new StreamReader(Request(LatestApi).GetResponseStream()))
            {
                var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                string tag = json.TryGetValue("tag_name", out object value) ? value as string ?? "" : "";
                string body = json.TryGetValue("body", out object notes) ? notes as string ?? "" : "";
                string url = "";
                if (json.TryGetValue("assets", out object assets) && assets is IEnumerable list)
                {
                    foreach (Dictionary<string, object> asset in list.OfType<Dictionary<string, object>>())
                    {
                        if (asset.TryGetValue("name", out object name) && (name as string) == AssetName && asset.TryGetValue("browser_download_url", out object link))
                        {
                            url = link as string ?? "";
                        }
                    }
                }
                Match hash = HashLine.Match(body);
                var latest = new[] { tag, url, hash.Success ? hash.Groups[1].Value.ToLowerInvariant() : "" };
                Directory.CreateDirectory(ctx.AppDir);
                File.WriteAllText(CachePath(ctx), DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "\t" + string.Join("\t", latest));
                return latest;
            }
        }

        private static void Download(Context ctx, string url, string target, string hash)
        {
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("unexpected download address");
            }
            Directory.CreateDirectory(UpdateDir(ctx));
            foreach (string old in Directory.GetFiles(UpdateDir(ctx)))
            {
                File.Delete(old);
            }
            string temp = target + ".download";
            using (WebResponse response = Request(url))
            using (Stream input = response.GetResponseStream())
            using (FileStream output = File.Create(temp))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxSetupBytes)
                    {
                        throw new InvalidOperationException("update download is too big");
                    }
                    output.Write(buffer, 0, read);
                }
            }
            if (!HashMatches(temp, hash))
            {
                File.Delete(temp);
                throw new InvalidOperationException("update didn't match its published hash, so it wasn't used");
            }
            File.Move(temp, target);
        }

        private static bool HashMatches(string path, string expected)
        {
            using (var sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static WebResponse Request(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "SmartBuffer";
            request.Timeout = TimeoutMs;
            if (url == LatestApi)
            {
                request.Accept = "application/vnd.github+json";
            }
            return request.GetResponse();
        }

        public static void SelfTest()
        {
            var current = new Version(0, 9, 2, 0);
            if (!IsNewer("v0.9.3", current) || IsNewer("v0.9.2", current) || IsNewer("v0.8.9", current) || IsNewer("garbage", current) || !IsNewer("1.0", current))
            {
                throw new Exception("self-test failed: update version compare");
            }
            Match found = HashLine.Match("notes\n\nSHA-256: `" + new string('a', 64) + "`\n");
            if (!found.Success || found.Groups[1].Value.Length != 64)
            {
                throw new Exception("self-test failed: hash line in release notes");
            }
        }
    }
}
