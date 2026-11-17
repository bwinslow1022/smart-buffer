using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SmartBuffer
{
    // finds the loud moments in a clip or recording (shouting, gunfights, explosions) and cuts each one out
    public static class Highlights
    {
        private const double Silence = -90;
        private const double AboveTypical = 12; // dB over the median level that counts as a moment
        private const int PeakSpacing = 6;      // seconds, two peaks closer than this are the same moment
        private const int Before = 8;
        private const int After = 4;
        private const int MaxMoments = 10;

        public static List<Tuple<int, int>> Make(Context ctx, string source, double seconds, Action<string> status)
        {
            status("Listening for loud moments...");
            double[] levels = Loudness(ctx, source, seconds, p => status($"Listening for loud moments... {p:P0}"));
            List<Tuple<int, int>> moments = Pick(levels);
            string dir = Path.GetDirectoryName(source);
            string name = Path.GetFileNameWithoutExtension(source);
            for (int i = 0; i < moments.Count; i++)
            {
                status($"Cutting highlight {i + 1} of {moments.Count}...");
                int start = moments[i].Item1, end = moments[i].Item2;
                string output = Path.Combine(dir, $"{name} (highlight {i + 1}).mp4");
                // same keyframe trade-off as the trim-only share copy: instant and lossless
                Run(ctx, $"-y -ss {start} -i \"{source}\" -t {end - start} -map 0:v:0 -map 0:a? -c copy -avoid_negative_ts make_zero -movflags +faststart \"{output}\"", null, null);
            }
            return moments;
        }

        // one RMS level per second of the first audio track, in dB
        private static double[] Loudness(Context ctx, string source, double seconds, Action<double> progress)
        {
            var levels = new List<double>();
            Run(ctx, $"-i \"{source}\" -map 0:a:0 -af aresample=8000,asetnsamples=n=8000,astats=metadata=1:reset=1,ametadata=mode=print:key=lavfi.astats.Overall.RMS_level:file=- -f null -", line =>
            {
                const string key = "lavfi.astats.Overall.RMS_level=";
                if (!line.StartsWith(key))
                {
                    return;
                }
                levels.Add(double.TryParse(line.Substring(key.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out double db) && !double.IsInfinity(db) ? Math.Max(db, Silence) : Silence);
                if (seconds > 0)
                {
                    progress(Math.Min(1, levels.Count / seconds));
                }
            }, "This file has no audio, so there's nothing to listen for.");
            return levels.ToArray();
        }

        /// The loudest moments well above the clip's usual level, as [start, end) seconds, merged where they overlap.
        public static List<Tuple<int, int>> Pick(double[] levels)
        {
            var moments = new List<Tuple<int, int>>();
            if (levels.Length == 0)
            {
                return moments;
            }
            double typical = levels.OrderBy(l => l).ElementAt(levels.Length / 2);
            var peaks = new List<int>();
            foreach (int i in Enumerable.Range(0, levels.Length).Where(i => levels[i] >= typical + AboveTypical).OrderByDescending(i => levels[i]))
            {
                if (peaks.Count < MaxMoments && peaks.All(p => Math.Abs(p - i) >= PeakSpacing))
                {
                    peaks.Add(i);
                }
            }
            foreach (int peak in peaks.OrderBy(p => p))
            {
                int start = Math.Max(0, peak - Before), end = Math.Min(levels.Length, peak + After + 1);
                if (moments.Count > 0 && start <= moments[moments.Count - 1].Item2)
                {
                    moments[moments.Count - 1] = Tuple.Create(moments[moments.Count - 1].Item1, end);
                }
                else
                {
                    moments.Add(Tuple.Create(start, end));
                }
            }
            return moments;
        }

        private static void Run(Context ctx, string args, Action<string> line, string noAudio)
        {
            var info = new ProcessStartInfo(ShareEncoder.FfmpegPath(ctx), "-hide_banner -nostats -loglevel error " + args)
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
                // a game running at the same time keeps its cpu, this just takes a bit longer
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
                var errors = new StringBuilder();
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
                process.BeginErrorReadLine();
                string text;
                while ((text = process.StandardOutput.ReadLine()) != null)
                {
                    line?.Invoke(text);
                }
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    ctx.Log("ffmpeg failed: " + errors);
                    bool missingAudio = errors.ToString().Contains("matches no streams");
                    throw new SetupException(missingAudio && noAudio != null ? noAudio : "ffmpeg couldn't read this file: " + errors.ToString().Trim().Split('\n').Last());
                }
            }
        }

        public static void SelfTest()
        {
            double[] quiet = Enumerable.Repeat(-40.0, 120).ToArray();
            Check(Pick(quiet).Count == 0, "a flat clip has no highlights");
            quiet[30] = -20;
            quiet[33] = -22; // same moment as 30
            quiet[100] = -15;
            List<Tuple<int, int>> found = Pick(quiet);
            Check(found.Count == 2, "two separate moments");
            Check(found[0].Item1 == 22 && found[0].Item2 == 35, "moment around second 30: " + found[0]);
            Check(found[1].Item1 == 92 && found[1].Item2 == 105, "moment around second 100: " + found[1]);
            quiet[2] = -10;
            Check(Pick(quiet)[0].Item1 == 0, "start clamps to 0");
        }

        private static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new Exception("self-test failed: " + what);
            }
        }
    }
}
