using System;
using System.IO;

namespace SmartBuffer
{
    public sealed class Context
    {
        public const string AppName = "Smart Buffer";

        public string ConfigDir { get; set; }
        public string AppDir { get; set; }
        public string ClipsDir { get; set; }
        public string RecordingsDir { get; set; }
        public string SandboxObsRoot { get; set; }
        // separate mode: Smart Buffer runs its own portable OBS so an existing OBS setup is never touched
        public string OwnObsRoot { get; set; }
        public bool HasOwnObs => OwnObsRoot != null;
        public string DefaultOwnObsRoot => Path.Combine(AppDir, "OBS");
        public Action<string> Progress { get; set; } = _ => { };

        public bool IsSandbox => SandboxObsRoot != null;
        public string StatePath => Path.Combine(AppDir, "state.json");
        public string SettingsPath => Path.Combine(AppDir, "settings.json");
        public string CommandPath => Path.Combine(AppDir, "command.txt");
        public string StatusPath => Path.Combine(AppDir, "status.txt");
        public string UploadsPath => Path.Combine(AppDir, "uploads.json");
        public string ScriptPath => Path.Combine(AppDir, "smartbuffer.lua");
        public string SoundPath => Path.Combine(AppDir, "saved.wav");
        public string LogPath => Path.Combine(AppDir, "setup.log");
        // the installer puts the app in Program Files; without it, setup copies the exe into AppData.
        // (a per-user install was tried in 0.9.4 and Defender's behavior scan flags it as malware while unsigned)
        public string InstalledExe => ProgramFilesExe() ?? Path.Combine(AppDir, AppName + ".exe");
        public bool IsInstallerInstall => ProgramFilesExe() != null;

        public static string ProgramFilesExe()
        {
            string programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string exe = Path.Combine(programFiles, AppName, AppName + ".exe");
            return File.Exists(exe) ? exe : null;
        }
        public string ProfileDir => Path.Combine(ConfigDir, "basic", "profiles", AppName);
        public string CollectionPath => Path.Combine(ConfigDir, "basic", "scenes", AppName + ".json");
        public string UserIniPath => Path.Combine(ConfigDir, "user.ini");

        public void UseOwnObs()
        {
            OwnObsRoot = DefaultOwnObsRoot;
            ConfigDir = Path.Combine(OwnObsRoot, "config", "obs-studio");
        }

        public void UseSharedObs()
        {
            OwnObsRoot = null;
            ConfigDir = ForThisUserShared().ConfigDir;
        }

        public static Context ForThisUser()
        {
            Context ctx = ForThisUserShared();
            try
            {
                if (State.Load(ctx).SeparateObs)
                {
                    ctx.UseOwnObs();
                }
            }
            catch (Exception)
            {
                // a broken state file just means shared mode, setup rewrites it
            }
            return ctx;
        }

        private static Context ForThisUserShared()
        {
            return new Context
            {
                ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio"),
                AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName),
                ClipsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clips"),
                RecordingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Recordings"),
            };
        }

        // points everything at a portable obs copy so tests never touch the real config
        public static Context ForSandbox(string obsRoot)
        {
            return new Context
            {
                SandboxObsRoot = obsRoot,
                ConfigDir = Path.Combine(obsRoot, "config", "obs-studio"),
                AppDir = Path.Combine(obsRoot, "smartbuffer-app"),
                ClipsDir = Path.Combine(obsRoot, "clips"),
                RecordingsDir = Path.Combine(obsRoot, "recordings"),
            };
        }

        public void Report(string message)
        {
            Log(message);
            Progress(message);
        }

        public void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(AppDir);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }

    public sealed class SetupException : Exception
    {
        public bool Canceled { get; }

        public SetupException(string message, bool canceled = false) : base(message)
        {
            Canceled = canceled;
        }
    }
}
