using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartBuffer
{
    public sealed class MainForm : Form
    {
        private static readonly Color Background = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel = Color.FromArgb(39, 39, 42);
        private static readonly Color Text1 = Color.FromArgb(244, 244, 245);
        private static readonly Color Muted = Color.FromArgb(161, 161, 170);
        private static readonly Color Accent = Color.FromArgb(109, 94, 245);

        private readonly Context ctx;
        private readonly string autoRun;
        private readonly TextBox log;
        private readonly Button setup;
        private readonly Button remove;
        private readonly Button clips;
        private readonly CheckBox separate;

        public MainForm(Context ctx, string autoRun)
        {
            this.ctx = ctx;
            this.autoRun = autoRun;
            Settings settings = Settings.Load(ctx);

            Text = Context.AppName;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(480, 386);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Background;
            ForeColor = Text1;
            Font = new Font("Segoe UI", 9.75F);

            Controls.Add(new Label
            {
                Text = Context.AppName,
                Font = new Font("Segoe UI Semibold", 20F),
                Location = new Point(24, 18),
                AutoSize = true,
            });
            Controls.Add(new Label
            {
                Text = $"Records in the background with OBS, like ShadowPlay.\nPress {settings.SaveHotkey} to save a clip and {settings.OverlayHotkey} for settings.",
                ForeColor = Muted,
                Location = new Point(26, 64),
                Size = new Size(430, 44),
            });

            bool installed = Setup.IsInstalled(ctx);
            separate = new CheckBox
            {
                Text = "I already use OBS for streaming or recording. Give Smart Buffer its own separate copy of OBS.",
                ForeColor = Text1,
                Location = new Point(26, 110),
                Size = new Size(430, 40),
                AutoSize = false,
                Checked = installed ? ctx.HasOwnObs : Setup.HasExistingObsSetup(),
                // locked with AutoCheck, not Enabled: a disabled checkbox ignores ForeColor and goes unreadable on dark
                AutoCheck = !installed,
                Cursor = installed ? Cursors.Default : Cursors.Hand,
            };
            separate.CheckedChanged += (_, __) => ShowIntro();
            separate.Click += (_, __) =>
            {
                if (!separate.AutoCheck)
                {
                    MessageBox.Show(this, "Smart Buffer is already set up. To switch modes, click Remove first, then tick this and set up again.", Context.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            Controls.Add(separate);

            log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Panel,
                ForeColor = Muted,
                Location = new Point(26, 158),
                Size = new Size(428, 144),
                TabStop = false,
            };
            Controls.Add(log);
            ShowIntro();

            setup = NewButton(Setup.IsInstalled(ctx) ? "Set up again" : "Set up", new Point(26, 318), new Size(150, 42));
            setup.BackColor = Accent;
            setup.FlatAppearance.BorderSize = 0;
            setup.Font = new Font("Segoe UI Semibold", 10.5F);
            setup.Click += (_, __) => RunSetup();

            clips = NewButton("Open clips", new Point(186, 318), new Size(130, 42));
            clips.Visible = Setup.IsInstalled(ctx);
            clips.Click += (_, __) => OpenClips();

            remove = NewButton("Remove", new Point(326, 318), new Size(128, 42));
            remove.Visible = Setup.IsInstalled(ctx);
            remove.Click += (_, __) => RunRemove();

            Controls.AddRange(new Control[] { setup, clips, remove });

            ctx.Progress = Append;
            Shown += (_, __) =>
            {
                if (autoRun == "setup")
                {
                    RunSetup();
                }
                else if (autoRun == "remove")
                {
                    RunRemove();
                }
            };
        }

        private Button NewButton(string text, Point location, Size size)
        {
            var button = new Button
            {
                Text = text,
                Location = location,
                Size = size,
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel,
                ForeColor = Text1,
                UseVisualStyleBackColor = false,
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(63, 63, 70);
            return button;
        }

        private void ShowIntro()
        {
            string nl = Environment.NewLine;
            log.Text = separate.Checked
                ? "Smart Buffer gets its own copy of OBS (about 225 MB download) with completely separate settings. Your OBS, its profiles, scenes, sources and settings are never touched, and both can run at the same time." + nl + nl
                    + "It starts in the tray at login and records with desktop capture. Your mic is recorded in clips."
                    + (Setup.IsInstalled(ctx) ? nl + nl + "To switch modes, click Remove first, then set up again." : "")
                : "This adds a new OBS profile, starts OBS at login in the tray, and turns off Game Bar capture and the OBS exit warning. Your mic is recorded in clips. Your other OBS profiles aren't changed." + nl + nl
                    + "Game capture hooks into fullscreen games, so some anti-cheats may block it or object. You can switch to desktop capture in settings."
                    + (Setup.IsInstalled(ctx) ? nl + nl + "To switch modes, click Remove first, then set up again." : "");
        }

        private void RunSetup()
        {
            if (!Setup.IsInstalled(ctx))
            {
                Setup.ChooseMode(ctx, separate.Checked);
            }
            separate.AutoCheck = false;
            Execute(() => Setup.Install(ctx, Confirm));
        }

        private void RunRemove()
        {
            if (MessageBox.Show(this, "Remove the OBS profile, the startup task and the Windows changes?\nYour clips and other OBS profiles stay.", Context.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            Execute(() => Setup.Remove(ctx, Confirm));
        }

        private async void Execute(Func<string> work)
        {
            setup.Enabled = remove.Enabled = clips.Enabled = false;
            log.Clear();

            string failure = null;
            try
            {
                await Task.Run(work);
            }
            catch (SetupException e)
            {
                failure = e.Message;
            }
            catch (Exception e)
            {
                ctx.Log(e.ToString());
                failure = "Something went wrong: " + e.Message;
            }

            if (failure != null)
            {
                ctx.Log(failure);
                Append(failure);
            }

            bool installed = Setup.IsInstalled(ctx);
            setup.Text = installed ? "Set up again" : "Set up";
            clips.Visible = remove.Visible = installed;
            separate.AutoCheck = !installed;
            separate.Cursor = installed ? Cursors.Default : Cursors.Hand;
            setup.Enabled = remove.Enabled = clips.Enabled = true;
        }

        private bool Confirm(string question)
        {
            return (bool)Invoke(new Func<bool>(() =>
                MessageBox.Show(this, question, Context.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
        }

        private void Append(string line)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Append), line);
                return;
            }
            log.ForeColor = Text1;
            log.AppendText((log.TextLength > 0 ? Environment.NewLine : "") + line);
        }

        private void OpenClips()
        {
            Directory.CreateDirectory(ctx.ClipsDir);
            Process.Start(new ProcessStartInfo(ctx.ClipsDir) { UseShellExecute = true })?.Dispose();
        }
    }
}
