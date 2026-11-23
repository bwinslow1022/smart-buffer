using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;

namespace SmartBuffer
{
    public static class Program
    {
        private const int Failed = 1;
        private const int Canceled = 2;

        [STAThread]
        public static int Main(string[] args)
        {
            if (Elevated.IsRequest(args))
            {
                return Elevated.Run(args);
            }

            // the installer runs this last: the tray task points at the new exe, then the tray starts again
            if (args.Contains("--after-install"))
            {
                return AfterInstall(Context.ForThisUser());
            }

            if (args.Contains("--selftest"))
            {
                Highlights.SelfTest();
                ClipCare.SelfTest();
                UpdateCheck.SelfTest();
                Console.WriteLine("self-test ok");
                return 0;
            }

            Context ctx = Context.ForThisUser();
            string action = args.Contains("--remove") ? "remove" : args.Contains("--setup") ? "setup" : null;

            if (args.Contains("--quiet"))
            {
                return RunQuiet(ctx, action ?? "setup", args.Contains("--yes"));
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Contains("--tray"))
            {
                return Companion.Run(ctx, args.Contains("--login"));
            }

            if (Setup.IsInstalled(ctx))
            {
                Companion.EnsureRunning(ctx);
            }
            Application.Run(new MainForm(ctx, action));
            return 0;
        }

        private static int AfterInstall(Context ctx)
        {
            try
            {
                if (!Setup.IsInstalled(ctx))
                {
                    return 0; // a first install, the finish page opens setup
                }
                Settings settings = Settings.Load(ctx);
                WindowsTweaks.SetTrayAtLogin(ctx.InstalledExe, settings.StartAtLogin);
                // an elevated installer would start an elevated tray, so it goes through the task, which runs as the normal user
                bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                if (elevated)
                {
                    Process.Start(new ProcessStartInfo("schtasks.exe", $"/run /tn \"{StartupTask.TrayName}\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
                }
                else
                {
                    Process.Start(new ProcessStartInfo(ctx.InstalledExe, "--tray") { UseShellExecute = false })?.Dispose();
                }
                return 0;
            }
            catch (Exception e)
            {
                ctx.Log("after install: " + e);
                return Failed;
            }
        }

        // no window, so an open obs just stops it instead of asking
        private static int RunQuiet(Context ctx, string action, bool closeObs)
        {
            try
            {
                if (action == "remove")
                {
                    Setup.Remove(ctx, _ => closeObs);
                }
                else
                {
                    Setup.Install(ctx, _ => closeObs);
                }
                return 0;
            }
            catch (SetupException e)
            {
                ctx.Log(e.Message);
                return e.Canceled ? Canceled : Failed;
            }
            catch (Exception e)
            {
                ctx.Log(e.ToString());
                return Failed;
            }
        }
    }
}
