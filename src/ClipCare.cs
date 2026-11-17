using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SmartBuffer
{
    // favorites, the optional storage limit, and sorting a desktop clip into the game it really was
    public static class ClipCare
    {
        private const long BytesPerGb = 1000L * 1000 * 1000;
        private const int WritingGraceMinutes = 2;
        private const string DesktopFolder = "Desktop";

        private static string FavoritesPath(Context ctx) => Path.Combine(ctx.AppDir, "favorites.txt");
        private static string GamesPath(Context ctx) => Path.Combine(ctx.AppDir, "games.txt");
        private static string SourcesPath(Context ctx) => Path.Combine(ctx.AppDir, "desktop-sources.txt");

        public static HashSet<string> Favorites(Context ctx)
        {
            return new HashSet<string>(ReadLines(FavoritesPath(ctx)), StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsFavorite(Context ctx, string path)
        {
            return Favorites(ctx).Contains(path);
        }

        public static bool ToggleFavorite(Context ctx, string path)
        {
            HashSet<string> favorites = Favorites(ctx);
            bool now = !favorites.Remove(path);
            if (now)
            {
                favorites.Add(path);
            }
            // a favorite that was deleted or moved doesn't need remembering
            WriteLines(FavoritesPath(ctx), favorites.Where(File.Exists));
            return now;
        }

        public static long LimitBytes(Settings settings)
        {
            return settings.StorageLimitGb * BytesPerGb;
        }

        /// Off unless a limit is set. Permanently deletes the oldest clips (never favorites) until the clips folder fits.
        public static List<string> Clean(Context ctx, Settings settings)
        {
            return CleanTo(ctx, settings.ClipsDir, LimitBytes(settings));
        }

        private static List<string> CleanTo(Context ctx, string clipsDir, long limit)
        {
            var deleted = new List<string>();
            if (limit <= 0 || !Directory.Exists(clipsDir))
            {
                return deleted;
            }
            List<LibraryFile> files = Library.Scan(clipsDir, null).SelectMany(folder => folder.Files).ToList();
            long total = files.Sum(file => file.Size);
            HashSet<string> favorites = Favorites(ctx);
            DateTime recent = DateTime.Now.AddMinutes(-WritingGraceMinutes);
            foreach (LibraryFile file in files.Where(f => !favorites.Contains(f.Path) && f.Modified < recent).OrderBy(f => f.Modified))
            {
                if (total <= limit)
                {
                    break;
                }
                try
                {
                    File.Delete(file.Path);
                    total -= file.Size;
                    deleted.Add(file.Path);
                    ctx.Log("storage limit: deleted " + file.Path);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    ctx.Log("storage limit: couldn't delete " + file.Path + ": " + e.Message);
                }
            }
            return deleted;
        }

        /// The window a desktop clip came from, as the obs script noted it, or null.
        public static string DesktopSource(Context ctx, string clipPath)
        {
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(clipPath)), DesktopFolder, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string name = Path.GetFileName(clipPath);
            return Sources(ctx).LastOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        }

        /// Marks the name as a game for next time and moves every desktop clip that came from it into its own folder.
        public static int SortAsGame(Context ctx, string clipsDir, string game)
        {
            List<string> games = ReadLines(GamesPath(ctx)).ToList();
            if (!games.Contains(game))
            {
                games.Add(game);
                WriteLines(GamesPath(ctx), games);
            }
            string from = Path.Combine(clipsDir, DesktopFolder);
            string to = Path.Combine(clipsDir, game);
            int moved = 0;
            foreach (string name in Sources(ctx).Where(pair => pair.Value == game).Select(pair => pair.Key).Distinct())
            {
                string source = Path.Combine(from, name);
                string rest = name.StartsWith(DesktopFolder + " ", StringComparison.OrdinalIgnoreCase) ? name.Substring(DesktopFolder.Length) : " " + name;
                string target = Path.Combine(to, game + rest);
                if (!File.Exists(source) || File.Exists(target))
                {
                    continue;
                }
                Directory.CreateDirectory(to);
                File.Move(source, target);
                moved++;
            }
            return moved;
        }

        private static IEnumerable<KeyValuePair<string, string>> Sources(Context ctx)
        {
            foreach (string line in ReadLines(SourcesPath(ctx)))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0)
                {
                    yield return new KeyValuePair<string, string>(line.Substring(0, tab), line.Substring(tab + 1));
                }
            }
        }

        private static IEnumerable<string> ReadLines(string path)
        {
            return File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).Where(line => line.Trim() != "") : Enumerable.Empty<string>();
        }

        // no byte order mark, the obs script reads these files too
        private static void WriteLines(string path, IEnumerable<string> lines)
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        public static void SelfTest()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sb-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Context ctx = Context.ForSandbox(dir);
                Directory.CreateDirectory(ctx.AppDir);
                string clips = Path.Combine(dir, "clips");
                Directory.CreateDirectory(Path.Combine(clips, DesktopFolder));
                string a = Path.Combine(clips, DesktopFolder, "Desktop 2026-01-01 10-00-00.mp4");
                string b = Path.Combine(clips, DesktopFolder, "Desktop 2026-01-01 11-00-00.mp4");
                File.WriteAllBytes(a, new byte[10]);
                File.WriteAllBytes(b, new byte[10]);
                File.WriteAllText(SourcesPath(ctx), "Desktop 2026-01-01 10-00-00.mp4\tMinecraft\nDesktop 2026-01-01 11-00-00.mp4\tChrome page\n");
                Check(DesktopSource(ctx, a) == "Minecraft", "desktop source lookup");
                Check(SortAsGame(ctx, clips, "Minecraft") == 1, "moves only that game's clips");
                Check(File.Exists(Path.Combine(clips, "Minecraft", "Minecraft 2026-01-01 10-00-00.mp4")) && File.Exists(b), "renamed into the game folder");
                Check(ReadLines(GamesPath(ctx)).Contains("Minecraft"), "remembered as a game");

                // limit cleanup: oldest goes first, favorites stay
                string old1 = Path.Combine(clips, "Minecraft", "Minecraft 2026-01-01 10-00-00.mp4");
                File.WriteAllBytes(b, new byte[600]);
                File.WriteAllBytes(old1, new byte[600]);
                File.SetLastWriteTime(old1, DateTime.Now.AddDays(-3));
                File.SetLastWriteTime(b, DateTime.Now.AddDays(-2));
                ToggleFavorite(ctx, old1);
                var settings = new Settings { ClipsDir = clips, StorageLimitGb = 0 };
                Check(Clean(ctx, settings).Count == 0, "off by default");
                Check(LimitBytes(new Settings { StorageLimitGb = 2 }) == 2 * BytesPerGb, "limit bytes");
                List<string> gone = CleanTo(ctx, clips, 700);
                Check(gone.Count == 1 && gone[0] == b && !File.Exists(b) && File.Exists(old1), "deletes the oldest non-favorite only");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
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
