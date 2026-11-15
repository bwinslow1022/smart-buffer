using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;

namespace SmartBuffer
{
    // the one bit that needs admin: closing a tray obs, installing obs, the startup task
    public static class Elevated
    {
        public const string ModeSetup = "setup";
        public const string ModeTask = "task";
        public const string ModeRemove = "remove";

        private const string Flag = "--elevated";
        private const int ObsCloseTimeoutMs = 20000;

        public static bool IsRequest(string[] args)
        {
            return args.Length > 0 && args[0] == Flag;
        }

        public static void Request(Context ctx, string mode, bool closeObs, Settings settings)
        {
            Directory.CreateDirectory(ctx.AppDir);
            string resultFile = Path.Combine(ctx.AppDir, "elevated.result");
            File.Delete(resultFile);

            string sid = WindowsIdentity.GetCurrent().User.Value;
            string args = $"{Flag} {mode} {sid} {(closeObs ? "close" : "keep")} {(settings.RunAsAdmin ? 1 : 0)} \"{resultFile}\" \"{ctx.OwnObsRoot ?? ""}\"";
            var info = new ProcessStartInfo(Assembly.GetEntryAssembly().Location, args)
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            try
            {
                using (Process process = Process.Start(info))
                {
                    process.WaitForExit();
                }
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                throw new SetupException("This needs admin approval. Nothing was changed.", cancelled: true);
            }

            string result = File.Exists(resultFile) ? File.ReadAllText(resultFile).Trim() : "The admin step didn't finish.";
            File.Delete(resultFile);
            if (result != "ok")
            {
                throw new SetupException(result);
            }
        }

        public static int Run(string[] args)
        {
            string mode = args[1];
            string sid = args[2];
            bool closeObs = args[3] == "close";
            bool runAsAdmin = args[4] == "1";
            string resultFile = args[5];
            // Smart Buffer's own portable OBS lives in a folder the user can write to, so it never runs as admin
            string own = args.Length > 6 && Obs.IsOwnCopy(args[6]) ? args[6] : null;
            if (own != null)
            {
                runAsAdmin = false;
            }

            try
            {
                var ctx = new Context { OwnObsRoot = own };
                string root = own ?? Obs.FindInstalledRoot();
                if (closeObs && !Obs.Close(ctx, ObsCloseTimeoutMs))
                {
                    throw new SetupException("OBS didn't close. Close it yourself and run this again.");
                }

                if (mode == ModeSetup)
                {
                    if (own == null)
                    {
                        ObsInstaller.EnsureInstalled();
                        root = Obs.FindInstalledRoot();
                    }
                    StartupTask.Register(sid, root, runAsAdmin);
                }
                else if (mode == ModeTask)
                {
                    StartupTask.Register(sid, root, runAsAdmin);
                }
                else
                {
                    StartupTask.Delete();
                }

                File.WriteAllText(resultFile, "ok");
                return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(resultFile, e.Message);
                return 1;
            }
        }
    }
}
