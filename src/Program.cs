using System;
using System.Linq;
using System.Windows.Forms;

namespace SmartBuffer
{
    public static class Program
    {
        private const int Failed = 1;
        private const int Cancelled = 2;

        [STAThread]
        public static int Main(string[] args)
        {
            if (Elevated.IsRequest(args))
            {
                return Elevated.Run(args);
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
                return e.Cancelled ? Cancelled : Failed;
            }
            catch (Exception e)
            {
                ctx.Log(e.ToString());
                return Failed;
            }
        }
    }
}
