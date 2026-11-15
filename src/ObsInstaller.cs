using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Web.Script.Serialization;

namespace SmartBuffer
{
    // only runs inside the elevated step
    public static class ObsInstaller
    {
        private const string WingetId = "OBSProject.OBSStudio";
        private const string ReleaseApi = "https://api.github.com/repos/obsproject/obs-studio/releases/latest";
        private const string Publisher = "OBS Project, LLC";

        public static void EnsureInstalled()
        {
            if (IsReady())
            {
                return;
            }
            if (!TryWinget() || !IsReady())
            {
                InstallFromRelease();
            }
            if (!IsReady())
            {
                throw new SetupException("OBS didn't install. You can get it from obsproject.com and run this again.");
            }
        }

        /// Downloads the official portable OBS into root (no admin needed) and checks it's signed by OBS.
        public static void EnsurePortable(string root, Action<string> report)
        {
            if (File.Exists(Obs.ExePath(root)) && Obs.IsOwnCopy(root))
            {
                return;
            }
            string work = root + ".download";
            if (Directory.Exists(work))
            {
                Directory.Delete(work, true);
            }
            Directory.CreateDirectory(work);
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                Dictionary<string, object> asset;
                string zip = Path.Combine(work, "obs.zip");
                using (var web = new WebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent] = "SmartBuffer-setup";
                    var release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(web.DownloadString(ReleaseApi));
                    asset = ((IEnumerable)release["assets"]).Cast<Dictionary<string, object>>().FirstOrDefault(a => IsPortableZip((string)a["name"]));
                    if (asset == null)
                    {
                        throw new SetupException("Couldn't find the portable OBS in its latest release.");
                    }
                    report("Downloading a separate copy of OBS (about 225 MB)");
                    web.Headers[HttpRequestHeader.UserAgent] = "SmartBuffer-setup";
                    web.DownloadFile((string)asset["browser_download_url"], zip);
                }
                // github publishes a sha256 for each release file
                if (asset.TryGetValue("digest", out object digest) && digest is string text && text.StartsWith("sha256:"))
                {
                    string actual;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    using (FileStream stream = File.OpenRead(zip))
                    {
                        actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                    }
                    if (!string.Equals(actual, text.Substring(7), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SetupException("The OBS download didn't match its checksum, so it wasn't used.");
                    }
                }
                report("Unpacking OBS");
                string staging = Path.Combine(work, "obs");
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, staging);
                if (!IsSignedByObs(Obs.ExePath(staging)))
                {
                    throw new SetupException("The downloaded OBS wasn't signed by OBS, so it wasn't used.");
                }
                File.WriteAllText(Path.Combine(staging, Obs.OwnMarker), "This copy of OBS belongs to Smart Buffer. Removing Smart Buffer deletes it.");
                // a previous broken copy keeps its settings
                string oldConfig = Path.Combine(root, "config");
                if (Directory.Exists(oldConfig))
                {
                    Directory.Move(oldConfig, Path.Combine(staging, "config"));
                }
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
                Directory.Move(staging, root);
            }
            finally
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (IOException)
                {
                }
            }
        }

        public static bool IsPortableZip(string name)
        {
            return name.EndsWith("-Windows-x64.zip", StringComparison.OrdinalIgnoreCase);
        }

        public static string DownloadInstaller(string folder)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            using (var web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = "SmartBuffer-setup";
                string json = web.DownloadString(ReleaseApi);
                var release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                var assets = ((IEnumerable)release["assets"]).Cast<Dictionary<string, object>>();
                var asset = assets.FirstOrDefault(a => IsWindowsInstaller((string)a["name"]));
                if (asset == null)
                {
                    throw new SetupException("Couldn't find the OBS installer in its latest release.");
                }

                string target = Path.Combine(folder, (string)asset["name"]);
                web.Headers[HttpRequestHeader.UserAgent] = "SmartBuffer-setup";
                web.DownloadFile((string)asset["browser_download_url"], target);
                return target;
            }
        }

        public static bool IsWindowsInstaller(string name)
        {
            return name.EndsWith("Installer.exe", StringComparison.OrdinalIgnoreCase)
                && name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0
                && name.IndexOf("arm64", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // i won't run a downloaded installer as admin unless obs really signed it
        public static bool IsSignedByObs(string file)
        {
            if (!HasTrustedSignature(file))
            {
                return false;
            }
            try
            {
                var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
                return certificate.GetNameInfo(X509NameType.SimpleName, false) == Publisher;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsReady()
        {
            string root = Obs.FindInstalledRoot();
            return root != null && Obs.IsNewEnough(root);
        }

        private static bool TryWinget()
        {
            string args = $"install --id {WingetId} -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
            try
            {
                return Shell.Run("winget", args, 600000, out _) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void InstallFromRelease()
        {
            string folder = Path.Combine(Path.GetTempPath(), "smartbuffer-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                string installer = DownloadInstaller(folder);
                if (!IsSignedByObs(installer))
                {
                    throw new SetupException("The downloaded OBS installer wasn't signed by OBS, so it wasn't run.");
                }
                Shell.Run(installer, "/S", 600000, out _);
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (IOException)
                {
                }
            }
        }

        private static bool HasTrustedSignature(string file)
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                FilePath = file,
            };
            IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            try
            {
                Marshal.StructureToPtr(fileInfo, filePointer, false);
                var data = new WinTrustData
                {
                    StructSize = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    UiChoice = 2,
                    RevocationChecks = 0,
                    UnionChoice = 1,
                    FileInfo = filePointer,
                    StateAction = 0,
                    ProviderFlags = 0x00000080,
                };
                var verifyAction = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                return WinVerifyTrust(new IntPtr(-1), ref verifyAction, ref data) == 0;
            }
            finally
            {
                Marshal.DestroyStructure(filePointer, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(filePointer);
            }
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallback;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
        }
    }
}
