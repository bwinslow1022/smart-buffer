using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SmartBuffer
{
    // Makes a trimmed copy of a clip, optionally squeezed under a file-size limit (Discord uploads).
    // Uses ffmpeg, downloaded once from a pinned release and checked against its SHA-256.
    public static class ShareEncoder
    {
        private const string FfmpegVersion = "9.0.2";
        private const string FfmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";
        private const string FfmpegSha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";
        private const long BytesPerMb = 1000000; // the smaller "MB", so a 20 MB limit is safe either way
        private const double Headroom = 0.95;
        private const int MinVideoKbps = 200;
        private const int Attempts = 3;

        public static string FfmpegPath(Context ctx)
        {
            return Path.Combine(ctx.AppDir, "ffmpeg", "ffmpeg-" + FfmpegVersion + ".exe");
        }

        public static bool HasFfmpeg(Context ctx)
        {
            return File.Exists(FfmpegPath(ctx));
        }

        /// Downloads ffmpeg the first time. progress gets 0..1.
        public static void EnsureFfmpeg(Context ctx, Action<double> progress)
        {
            string exe = FfmpegPath(ctx);
            if (File.Exists(exe))
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(exe));
            string zip = exe + ".download";
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                using (var web = new WebClient())
                using (var done = new ManualResetEventSlim())
                {
                    Exception failure = null;
                    web.Headers[HttpRequestHeader.UserAgent] = "SmartBuffer";
                    web.DownloadProgressChanged += (_, e) => progress(e.TotalBytesToReceive > 0 ? (double)e.BytesReceived / e.TotalBytesToReceive : 0);
                    web.DownloadFileCompleted += (_, e) => { failure = e.Error; done.Set(); };
                    web.DownloadFileAsync(new Uri(FfmpegUrl), zip);
                    done.Wait();
                    if (failure != null)
                    {
                        throw new SetupException("Couldn't download ffmpeg: " + failure.Message);
                    }
                }
                if (!string.Equals(Sha256(zip), FfmpegSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SetupException("The ffmpeg download didn't match its checksum, so it wasn't used.");
                }
                using (ZipArchive archive = ZipFile.OpenRead(zip))
                {
                    ZipArchiveEntry entry = null;
                    foreach (ZipArchiveEntry e in archive.Entries)
                    {
                        if (e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            entry = e;
                        }
                    }
                    if (entry == null)
                    {
                        throw new SetupException("ffmpeg.exe wasn't in the download.");
                    }
                    entry.ExtractToFile(exe + ".tmp", true);
                }
                File.Move(exe + ".tmp", exe);
            }
            finally
            {
                TryDelete(zip);
                TryDelete(exe + ".tmp");
            }
        }

        /// Writes "<name> (share).mp4" next to the source and returns its path.
        /// limitMb 0 = keep the original quality and only trim.
        public static string Make(Context ctx, string source, TimeSpan start, TimeSpan end, int limitMb, Action<double> progress)
        {
            double seconds = (end - start).TotalSeconds;
            if (seconds < 1)
            {
                throw new SetupException("Pick at least one second of video.");
            }
            string output = FreeName(source);
            string range = $"-ss {Secs(start.TotalSeconds)} -i \"{source}\" -t {Secs(seconds)}";
            if (limitMb <= 0)
            {
                // cuts land on the nearest keyframe (every 2 s), in exchange it's instant and lossless
                Run(ctx, $"-y {range} -map 0:v:0 -map 0:a:0? -c copy -avoid_negative_ts make_zero -movflags +faststart \"{output}\"", seconds, progress);
                return output;
            }

            long limit = limitMb * BytesPerMb;
            int maxSeconds = (int)(limit * 8 * Headroom / 1000 / (MinVideoKbps + 64));
            double budget = Headroom;
            bool gpu = true;
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                double totalKbps = limit * 8 * budget / seconds / 1000;
                int audio = totalKbps >= 1600 ? 128 : totalKbps >= 600 ? 96 : 64;
                int video = (int)(totalKbps - audio);
                if (video < MinVideoKbps)
                {
                    throw new SetupException($"That's too long to fit in {limitMb} MB. Trim it to about {maxSeconds} seconds or less.");
                }
                // less bitrate looks better at a lower resolution than as a blocky full-size picture
                int height = video >= 6000 ? 1440 : video >= 3000 ? 1080 : video >= 1400 ? 720 : video >= 700 ? 540 : 360;
                string fps = video < 1000 ? "-r 30 " : "";
                string rate = $"-b:v {video}k -maxrate {video * 3 / 2}k -bufsize {video * 2}k";
                string audioArgs = $"-c:a aac -b:a {audio}k -movflags +faststart";
                string gpuArgs = $"-y -hwaccel cuda -hwaccel_output_format cuda {range} -map 0:v:0 -map 0:a:0? -vf \"scale_cuda=-2:'min(ih,{height})'\" {fps}-c:v h264_nvenc -preset p5 -tune hq -rc vbr -multipass qres {rate} {audioArgs} \"{output}\"";
                string cpuArgs = $"-y {range} -map 0:v:0 -map 0:a:0? -vf \"scale=-2:'min(ih,{height})'\" {fps}-c:v libx264 -preset veryfast {rate} {audioArgs} \"{output}\"";
                if (gpu)
                {
                    try
                    {
                        Run(ctx, gpuArgs, seconds, progress);
                    }
                    catch (SetupException)
                    {
                        gpu = false; // no NVIDIA GPU or driver, the CPU path works everywhere
                    }
                }
                if (!gpu)
                {
                    Run(ctx, cpuArgs, seconds, progress);
                }
                long size = new FileInfo(output).Length;
                if (size <= limit)
                {
                    return output;
                }
                budget *= (double)limit / size * 0.97;
            }
            TryDelete(output);
            throw new SetupException($"Couldn't get it under {limitMb} MB. Trim it a bit shorter and try again.");
        }

        private static void Run(Context ctx, string args, double seconds, Action<double> progress)
        {
            var info = new ProcessStartInfo(FfmpegPath(ctx), "-hide_banner -nostats -loglevel error -progress pipe:1 " + args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (Process process = Process.Start(info))
            {
                // a game running at the same time keeps its cpu, encoding just takes a bit longer
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
                var errors = new StringBuilder();
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
                process.BeginErrorReadLine();
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (line.StartsWith("out_time_us=") && long.TryParse(line.Substring(12), out long us) && us > 0)
                    {
                        progress(Math.Min(1, us / 1e6 / seconds));
                    }
                }
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    ctx.Log("ffmpeg failed: " + errors);
                    string last = errors.ToString().Trim();
                    int cut = last.LastIndexOf('\n');
                    throw new SetupException("ffmpeg couldn't convert this clip: " + (cut >= 0 ? last.Substring(cut + 1) : last));
                }
            }
        }

        private static string FreeName(string source)
        {
            string dir = Path.GetDirectoryName(source);
            string name = Path.GetFileNameWithoutExtension(source);
            string path = Path.Combine(dir, name + " (share).mp4");
            for (int i = 2; File.Exists(path); i++)
            {
                path = Path.Combine(dir, $"{name} (share {i}).mp4");
            }
            return path;
        }

        private static string Secs(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(file))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
