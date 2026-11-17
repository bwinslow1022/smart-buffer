using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SmartBuffer
{
    // one stretch of a clip worth keeping, in seconds
    public sealed class Moment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public double Score { get; set; }
        public int Kills { get; set; }
        public bool Shout { get; set; }
        public bool Keep { get; set; } = true;
        // the loud or kill seconds themselves, without the lead-in and tail
        public double PeakFirst { get; set; }
        public double PeakLast { get; set; }

        public string Why()
        {
            var parts = new List<string>();
            if (Kills > 0)
            {
                parts.Add(Kills == 1 ? "kill" : Kills + " kills");
            }
            if (Shout)
            {
                parts.Add("you reacted");
            }
            return parts.Count == 0 ? "loud" : string.Join(", ", parts);
        }
    }

    // finds the big moments in a clip: kills (once it has heard the game's kill sound), sudden loud fights, and you yelling
    public static class Highlights
    {
        public const int Rate = 8000;
        private const double Silence = -90;
        private const double AboveTypical = 12; // dB over the surrounding minute that counts as loud
        private const int BaselineSeconds = 60; // loudness is compared with this much on either side
        private const int OnsetSeconds = 3;     // a sudden jump from these seconds before scores higher
        private const int PeakSpacing = 6;      // seconds, two peaks closer than this are the same moment
        private const int Before = 8;
        private const int After = 4;
        private const int MaxMoments = 10;
        private const double ShoutBonus = 6;
        private const double KillScore = 20;
        private const double KillGroupSeconds = 8; // kills this close are one multi-kill

        // kill matching works on a coarse spectrogram: 10 ms frames, log energy in a few bands
        private const int Hop = 80;
        private const int Window = 256;
        private const int Bands = 24;
        private const int TemplateFrames = 40; // 0.4 s, about how long a kill sound lasts
        public const double KillMatch = 0.6;
        private const double KillSpacing = 0.3;

        /// Game audio drives it, the mic only adds "you reacted". Track 2 and 3 exist with separate tracks on.
        public static List<Moment> Find(Context ctx, string source, double seconds, string game, Action<string> status)
        {
            status("Listening...");
            short[] gameAudio = Decode(ctx, source, 1, seconds, p => status($"Listening to the game... {p:P0}"))
                ?? Decode(ctx, source, 0, seconds, p => status($"Listening... {p:P0}"));
            if (gameAudio == null)
            {
                throw new SetupException("This file has no audio, so there's nothing to listen for.");
            }
            short[] mic = Decode(ctx, source, 2, seconds, p => status($"Listening to your mic... {p:P0}"));

            List<double> kills = new List<double>();
            float[] template = LoadKillSound(ctx, game);
            if (template != null)
            {
                status("Looking for kills...");
                kills = FindKills(Spectrogram(gameAudio), template);
            }
            return Pick(Levels(gameAudio), mic == null ? null : Levels(mic), kills);
        }

        /// Cuts the kept moments out exactly (re-encoded, so they start on the right frame). Returns the new files.
        public static List<string> Save(Context ctx, string source, IList<Moment> moments, Action<string> status)
        {
            var made = new List<string>();
            string dir = Path.GetDirectoryName(source);
            string name = Path.GetFileNameWithoutExtension(source);
            int number = 1;
            bool gpu = true;
            for (int i = 0; i < moments.Count; i++)
            {
                while (File.Exists(Path.Combine(dir, $"{name} (highlight {number}).mp4")))
                {
                    number++;
                }
                string output = Path.Combine(dir, $"{name} (highlight {number}).mp4");
                status($"Saving highlight {i + 1} of {moments.Count}...");
                string range = $"-ss {Secs(moments[i].Start)} -i \"{source}\" -t {Secs(moments[i].End - moments[i].Start)} -map 0:v:0 -map 0:a?";
                string audio = "-c:a aac -b:a 192k -movflags +faststart";
                if (gpu)
                {
                    try
                    {
                        Run(ctx, $"-y {range} -c:v h264_nvenc -preset p5 -rc vbr -cq 19 -b:v 0 {audio} \"{output}\"", null);
                    }
                    catch (SetupException)
                    {
                        gpu = false; // no nvidia card, the cpu encoder works everywhere
                    }
                }
                if (!gpu)
                {
                    Run(ctx, $"-y {range} -c:v libx264 -preset veryfast -crf 18 {audio} \"{output}\"", null);
                }
                made.Add(output);
            }
            return made;
        }

        public static bool KnowsKillSound(Context ctx, string game)
        {
            return File.Exists(KillSoundPath(ctx, game));
        }

        /// Takes the kill sound from around this point of the game audio, and returns how many kills that finds in the clip.
        public static int LearnKillSound(Context ctx, string source, double at, double seconds, string game, Action<string> status)
        {
            short[] audio = Decode(ctx, source, 1, seconds, p => status($"Listening... {p:P0}"))
                ?? Decode(ctx, source, 0, seconds, p => status($"Listening... {p:P0}"));
            if (audio == null)
            {
                throw new SetupException("This file has no audio.");
            }
            float[,] frames = Spectrogram(audio);
            float[] template = PickTemplate(frames, (int)(at * Rate / Hop));
            if (template == null)
            {
                throw new SetupException("It's quiet there. Pause right as the kill sound plays and try again.");
            }
            string path = KillSoundPath(ctx, game);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                foreach (float value in template)
                {
                    writer.Write(value);
                }
            }
            return FindKills(frames, template).Count;
        }

        public static void ForgetKillSound(Context ctx, string game)
        {
            File.Delete(KillSoundPath(ctx, game));
        }

        private static string KillSoundPath(Context ctx, string game)
        {
            string safe = new string((game ?? "").Where(c => Path.GetInvalidFileNameChars().All(bad => bad != c)).ToArray()).Trim();
            return Path.Combine(ctx.AppDir, "kill-sounds", (safe == "" ? "default" : safe) + ".bin");
        }

        private static float[] LoadKillSound(Context ctx, string game)
        {
            string path = KillSoundPath(ctx, game);
            if (!File.Exists(path))
            {
                return null;
            }
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length != TemplateFrames * Bands * sizeof(float))
            {
                return null; // from an older format, it needs learning again
            }
            var template = new float[TemplateFrames * Bands];
            Buffer.BlockCopy(bytes, 0, template, 0, bytes.Length);
            return template;
        }

        /// The moments, best first up to MaxMoments, then put back in time order and merged where they overlap.
        public static List<Moment> Pick(double[] levels, double[] micLevels, IList<double> kills)
        {
            var candidates = new List<Moment>();
            int length = levels.Length;

            double[] baseline = LocalMedian(levels);
            for (int i = 0; i < length; i++)
            {
                double above = levels[i] - baseline[i];
                if (above < AboveTypical)
                {
                    continue;
                }
                double before = Enumerable.Range(Math.Max(0, i - OnsetSeconds), Math.Min(OnsetSeconds, i)).Select(j => levels[j]).DefaultIfEmpty(levels[i]).Max();
                double jump = Math.Max(0, levels[i] - before);
                candidates.Add(Around(i, i, length, above + jump * 0.5));
            }

            bool[] shouting = new bool[length];
            if (micLevels != null)
            {
                double[] micBaseline = LocalMedian(micLevels);
                for (int i = 0; i < micLevels.Length && i < length; i++)
                {
                    if (micLevels[i] - micBaseline[i] >= AboveTypical)
                    {
                        shouting[i] = true;
                        // yelling on its own is a moment too, just a smaller one
                        Moment alone = Around(i, i, length, micLevels[i] - micBaseline[i] - 4);
                        alone.Shout = true;
                        candidates.Add(alone);
                    }
                }
            }

            // kills close together become one multi-kill moment
            var sorted = kills.OrderBy(k => k).ToList();
            for (int i = 0; i < sorted.Count;)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] - sorted[j] <= KillGroupSeconds)
                {
                    j++;
                }
                Moment group = Around((int)sorted[i], (int)Math.Ceiling(sorted[j]), length, KillScore * (j - i + 1));
                group.Kills = j - i + 1;
                candidates.Add(group);
                i = j + 1;
            }

            foreach (Moment candidate in candidates)
            {
                int from = (int)candidate.PeakFirst - 2, to = (int)candidate.PeakLast + 5;
                for (int s = Math.Max(0, from); s < Math.Min(length, to); s++)
                {
                    if (shouting[s] && !candidate.Shout)
                    {
                        candidate.Shout = true;
                        candidate.Score += ShoutBonus;
                        break;
                    }
                }
            }

            var chosen = new List<Moment>();
            foreach (Moment candidate in candidates.OrderByDescending(c => c.Score))
            {
                if (chosen.Count >= MaxMoments)
                {
                    break;
                }
                Moment near = chosen.FirstOrDefault(c => candidate.PeakFirst - c.PeakLast < PeakSpacing && c.PeakFirst - candidate.PeakLast < PeakSpacing);
                if (near == null)
                {
                    chosen.Add(candidate);
                }
                else if (candidate.Kills > 0 && near.Kills == 0)
                {
                    // a kill in the same fight as a loud second, so the moment is about the kill
                    near.Kills = candidate.Kills;
                    near.Start = Math.Min(near.Start, candidate.Start);
                    near.End = Math.Max(near.End, candidate.End);
                    near.PeakFirst = Math.Min(near.PeakFirst, candidate.PeakFirst);
                    near.PeakLast = Math.Max(near.PeakLast, candidate.PeakLast);
                }
            }

            var merged = new List<Moment>();
            foreach (Moment moment in chosen.OrderBy(m => m.Start))
            {
                Moment last = merged.LastOrDefault();
                if (last != null && moment.Start <= last.End)
                {
                    last.End = Math.Max(last.End, moment.End);
                    last.Kills += moment.Kills;
                    last.Shout |= moment.Shout;
                    last.Score = Math.Max(last.Score, moment.Score);
                }
                else
                {
                    merged.Add(moment);
                }
            }
            return merged;
        }

        private static Moment Around(int first, int last, int length, double score)
        {
            return new Moment { Start = Math.Max(0, first - Before), End = Math.Min(length, last + After + 1), Score = score, PeakFirst = first, PeakLast = last };
        }

        // the median of the minute either side, so a loud match doesn't hide the moments in a quiet one
        private static double[] LocalMedian(double[] levels)
        {
            var result = new double[levels.Length];
            for (int i = 0; i < levels.Length; i++)
            {
                int from = Math.Max(0, i - BaselineSeconds), to = Math.Min(levels.Length, i + BaselineSeconds + 1);
                var window = new double[to - from];
                Array.Copy(levels, from, window, 0, window.Length);
                Array.Sort(window);
                result[i] = window[window.Length / 2];
            }
            return result;
        }

        // RMS in dB for each second
        private static double[] Levels(short[] audio)
        {
            var levels = new double[(audio.Length + Rate - 1) / Rate];
            for (int s = 0; s < levels.Length; s++)
            {
                double sum = 0;
                int from = s * Rate, to = Math.Min(audio.Length, from + Rate);
                for (int i = from; i < to; i++)
                {
                    double v = audio[i] / 32768.0;
                    sum += v * v;
                }
                double rms = Math.Sqrt(sum / Math.Max(1, to - from));
                levels[s] = rms > 0 ? Math.Max(Silence, 20 * Math.Log10(rms)) : Silence;
            }
            return levels;
        }

        /// Log band energies, one row per 10 ms frame.
        public static float[,] Spectrogram(short[] audio)
        {
            int count = Math.Max(0, (audio.Length - Window) / Hop + 1);
            var frames = new float[count, Bands];
            var re = new double[Window];
            var im = new double[Window];
            var hann = Enumerable.Range(0, Window).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1))).ToArray();
            // bands spaced evenly in log frequency from about 150 Hz to 4 kHz
            var edges = Enumerable.Range(0, Bands + 1).Select(b => (int)Math.Round(5 * Math.Pow(Window / 2.0 / 5, (double)b / Bands))).ToArray();
            for (int f = 0; f < count; f++)
            {
                for (int i = 0; i < Window; i++)
                {
                    re[i] = audio[f * Hop + i] / 32768.0 * hann[i];
                    im[i] = 0;
                }
                Fft(re, im);
                for (int b = 0; b < Bands; b++)
                {
                    double energy = 1e-9;
                    for (int k = edges[b]; k < Math.Max(edges[b] + 1, edges[b + 1]); k++)
                    {
                        energy += re[k] * re[k] + im[k] * im[k];
                    }
                    frames[f, b] = (float)Math.Log(energy);
                }
            }
            return frames;
        }

        // the loudest 0.4 s that starts within about half a second of where the video was paused
        private static float[] PickTemplate(float[,] frames, int at)
        {
            int count = frames.GetLength(0);
            int best = -1;
            double bestEnergy = double.MinValue;
            for (int start = Math.Max(0, at - 60); start <= Math.Min(count - TemplateFrames, at + 40); start++)
            {
                double energy = 0;
                for (int f = start; f < start + TemplateFrames; f++)
                {
                    for (int b = 0; b < Bands; b++)
                    {
                        energy += frames[f, b];
                    }
                }
                if (energy > bestEnergy)
                {
                    bestEnergy = energy;
                    best = start;
                }
            }
            // nearly silent means there was nothing to learn from
            if (best < 0 || bestEnergy / (TemplateFrames * Bands) < Math.Log(1e-6))
            {
                return null;
            }
            var template = new float[TemplateFrames * Bands];
            for (int f = 0; f < TemplateFrames; f++)
            {
                for (int b = 0; b < Bands; b++)
                {
                    template[f * Bands + b] = frames[best + f, b];
                }
            }
            return Normalize(template);
        }

        /// Seconds where the kill sound plays: the spectrogram correlates with the learned one above KillMatch.
        public static List<double> FindKills(float[,] frames, float[] template)
        {
            int count = frames.GetLength(0) - TemplateFrames;
            int size = TemplateFrames * Bands;
            var scores = new double[Math.Max(0, count)];
            var patch = new float[size];
            for (int start = 0; start < count; start++)
            {
                for (int f = 0; f < TemplateFrames; f++)
                {
                    for (int b = 0; b < Bands; b++)
                    {
                        patch[f * Bands + b] = frames[start + f, b];
                    }
                }
                Normalize(patch);
                double dot = 0;
                for (int i = 0; i < size; i++)
                {
                    dot += patch[i] * template[i];
                }
                scores[start] = dot / size;
            }

            var found = new List<double>();
            int spacing = (int)(KillSpacing * Rate / Hop);
            for (int i = 0; i < scores.Length; i++)
            {
                if (scores[i] < KillMatch)
                {
                    continue;
                }
                bool peak = true;
                for (int j = Math.Max(0, i - spacing); j < Math.Min(scores.Length, i + spacing + 1) && peak; j++)
                {
                    peak = scores[j] < scores[i] || (scores[j] == scores[i] && j >= i);
                }
                if (peak)
                {
                    found.Add((double)i * Hop / Rate);
                }
            }
            return found;
        }

        // zero mean, unit variance, so loudness doesn't matter, only the shape of the sound
        private static float[] Normalize(float[] values)
        {
            double mean = values.Average();
            double spread = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = spread > 1e-6 ? (float)((values[i] - mean) / spread) : 0;
            }
            return values;
        }

        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                {
                    j ^= bit;
                }
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2 * Math.PI / len;
                for (int i = 0; i < n; i += len)
                {
                    for (int k = 0; k < len / 2; k++)
                    {
                        double wr = Math.Cos(angle * k), wi = Math.Sin(angle * k);
                        int a = i + k, b = i + k + len / 2;
                        double tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }

        /// One audio track as 8 kHz mono samples, or null when the file doesn't have that track.
        private static short[] Decode(Context ctx, string source, int track, double seconds, Action<double> progress)
        {
            var info = new ProcessStartInfo(ShareEncoder.FfmpegPath(ctx),
                $"-hide_banner -nostats -loglevel error -i \"{source}\" -map 0:a:{track} -ac 1 -ar {Rate} -f s16le -")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (Process process = Process.Start(info))
            {
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
                var errors = new StringBuilder();
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
                process.BeginErrorReadLine();
                var samples = new List<short>(seconds > 0 ? (int)(seconds * Rate) + Rate : Rate * 60);
                var buffer = new byte[Rate * 2];
                Stream output = process.StandardOutput.BaseStream;
                int carry = -1;
                int read;
                int shown = -1;
                while ((read = output.Read(buffer, 0, buffer.Length)) > 0)
                {
                    int i = 0;
                    if (carry >= 0)
                    {
                        samples.Add((short)(carry | (buffer[0] << 8)));
                        carry = -1;
                        i = 1;
                    }
                    for (; i + 1 < read; i += 2)
                    {
                        samples.Add((short)(buffer[i] | (buffer[i + 1] << 8)));
                    }
                    if (i < read)
                    {
                        carry = buffer[i];
                    }
                    // whole percents only, every read would flood the ui
                    int percent = seconds > 0 ? (int)Math.Min(100, samples.Count * 100 / (seconds * Rate)) : -1;
                    if (percent != shown)
                    {
                        shown = percent;
                        progress(percent / 100.0);
                    }
                }
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    if (errors.ToString().Contains("matches no streams"))
                    {
                        return null;
                    }
                    ctx.Log("ffmpeg failed: " + errors);
                    throw new SetupException("ffmpeg couldn't read this file: " + errors.ToString().Trim().Split('\n').Last());
                }
                return samples.ToArray();
            }
        }

        private static void Run(Context ctx, string args, Action<string> line)
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
                    throw new SetupException("ffmpeg couldn't make the highlight: " + errors.ToString().Trim().Split('\n').Last());
                }
            }
        }

        private static string Secs(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static void SelfTest()
        {
            double[] quiet = Enumerable.Repeat(-40.0, 120).ToArray();
            Check(Pick(quiet, null, new double[0]).Count == 0, "a flat clip has no highlights");
            quiet[30] = -20;
            quiet[33] = -22; // same moment as 30
            quiet[100] = -15;
            List<Moment> found = Pick(quiet, null, new double[0]);
            Check(found.Count == 2, "two separate moments, got " + found.Count);
            Check(found[0].Start == 22 && found[0].End == 35, "moment around second 30: " + found[0].Start + "-" + found[0].End);
            Check(found[1].Start == 92 && found[1].End == 105, "moment around second 100");
            quiet[2] = -10;
            Check(Pick(quiet, null, new double[0])[0].Start == 0, "start clamps to 0");

            // a loud stretch an hour in doesn't hide a smaller moment in an otherwise quiet part
            double[] match = Enumerable.Repeat(-40.0, 400).ToArray();
            for (int i = 200; i < 400; i++)
            {
                match[i] = -15;
            }
            match[50] = -26;
            Check(Pick(match, null, new double[0]).Any(m => m.Start <= 50 && m.End > 50), "local baseline keeps the quiet-part moment");

            // you yelling right after a loud second marks it, and kills group into one multi-kill
            double[] mic = Enumerable.Repeat(-50.0, 120).ToArray();
            mic[102] = -20;
            List<Moment> both = Pick(Enumerable.Repeat(-40.0, 120).Select((v, i) => i == 100 ? -15 : v).ToArray(), mic, new[] { 40.0, 43.5, 46.0 });
            Check(both.Any(m => m.Shout && m.Start <= 100 && m.End > 100), "shout joins the loud moment");
            Check(both.Any(m => m.Kills == 3), "three close kills are one moment");

            // the kill matcher finds a chirp it learned, buried in noise, and nothing else
            var random = new Random(7);
            var audio = new short[Rate * 20];
            for (int i = 0; i < audio.Length; i++)
            {
                audio[i] = (short)(random.Next(-2000, 2000));
            }
            double[] at = { 3.0, 9.5, 15.25 };
            foreach (double second in at)
            {
                int from = (int)(second * Rate);
                for (int i = 0; i < Rate * 4 / 10; i++)
                {
                    double t = (double)i / Rate;
                    audio[from + i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, audio[from + i] + 12000 * Math.Sin(2 * Math.PI * (600 + 2500 * t) * t)));
                }
            }
            float[,] frames = Spectrogram(audio);
            float[] template = PickTemplate(frames, (int)(3.1 * Rate / Hop));
            List<double> kills = FindKills(frames, template);
            Check(kills.Count == 3 && kills.Zip(at, (k, a) => Math.Abs(k - a) < 0.15).All(ok => ok), "finds the 3 chirps: " + string.Join(", ", kills));
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
