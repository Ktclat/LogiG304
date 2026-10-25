using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace LogiG304.Standalone
{
    internal static class ControllerTests
    {
        static int checks;
        static void Check(bool ok, string reason) { checks++; if (!ok) throw new Exception(reason); }
        static void Throws(Action action, string reason)
        { bool threw = false; try { action(); } catch { threw = true; } Check(threw, reason); }
        [STAThread]
        public static int Main()
        {
            try
            {
                TestProtocol(); TestWrites(); TestInstall(); TestView();
                Console.WriteLine("PASS: " + checks + " independent controller checks; no real HID writes."); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        static void TestProtocol()
        {
            var wire = new Fake(); var device = new G304Device(wire, delegate { });
            MouseState state = device.Read();
            Check(state.Name.Contains("G305") && state.Protocol == "4.2", "identity");
            Check(state.Dpi == 800 && state.DpiMin == 200 && state.DpiMax == 12000 && state.DpiStep == 50, "DPI decoding");
            Check(state.RateMs == 1 && state.RatesMs.Count == 4 && state.Battery == 50, "status decoding");
            Check(wire.Writes == 0, "Read must not change mode, DPI, rate, notification state or flash");
            Check(G304Device.ValidDpi(state, 200) && G304Device.ValidDpi(state, 12000), "DPI boundaries");
            foreach (int invalid in new[] { -1, 0, 199, 201, 11999, 12001, 65535 }) Check(!G304Device.ValidDpi(state, invalid), "invalid DPI " + invalid);
            var discrete = new MouseState { DpiSupported = true };
            G304Device.ParseDpiList(new byte[] { 0, 1, 144, 3, 32, 6, 64, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, discrete);
            Check(G304Device.ValidDpi(discrete, 800) && !G304Device.ValidDpi(discrete, 600), "discrete DPI capabilities");
            Throws(delegate { G304Device.ParseDpiList(new byte[3], new MouseState()); }, "truncated range");
            Throws(delegate { new G304Device(new Fake { Name = "Unrelated Mouse" }, delegate { }).Read(); }, "wrong model");
            Throws(delegate { new G304Device(new Fake { BadPing = true }, delegate { }).Read(); }, "bad ping");
            byte[] response = { 0x11, 1, 0x1A, 0x2A, 0, 0, 0 };
            Check(HidTransport.IsReply(response, 1, 0x1A, 0x2A), "matching reply");
            Check(!HidTransport.IsReply(response, 2, 0x1A, 0x2A), "wrong paired device");
            Check(!HidTransport.IsReply(response, 1, 0x1A, 0x2B), "other application reply");
            Check(!HidTransport.IsReply(new byte[3], 1, 0, 0), "truncated reply");
            Check(HidTransport.IsReply(new byte[] { 0x11, 1, 0xFF, 0x1A, 0x3A, 2, 0 }, 1, 0x1A, 0x3A), "matching error");
            Check(!HidTransport.IsReply(new byte[] { 0x11, 1, 0xFF, 0x1A, 0x3B, 2, 0 }, 1, 0x1A, 0x3A), "other application error");
        }
        static void TestWrites()
        {
            var wire = new Fake(); var device = new G304Device(wire, delegate { });
            Throws(delegate { device.SetDpi(800); }, "unidentified write");
            device.Read(); device.SetDpi(1600); Check(wire.Dpi == 1600 && wire.Writes == 1, "DPI request + readback");
            device.SetRate(8); Check(wire.Rate == 8 && wire.Writes == 2, "rate request + readback");
            Throws(delegate { device.SetDpi(825); }, "off-step DPI");
            Throws(delegate { device.SetRate(3); }, "unsupported rate");
            Check(wire.Writes == 2, "invalid inputs sent no settings command");
            device.SetMode(1); Check(wire.Mode == 1 && wire.Writes == 3, "mode change");
            Throws(delegate { device.SetDpi(800); }, "runtime DPI in onboard mode");
            Throws(delegate { device.SetRate(1); }, "runtime rate in onboard mode");
            MouseState onboard = device.Read(); Check(onboard.StageDpis.Length == 5 && onboard.StageDpis[0] == 400, "existing onboard header");
            device.SetStage(1); Check(wire.Stage == 1 && wire.Writes == 4, "existing stage change");
            Throws(delegate { device.SetStage(4); }, "disabled stage");
            Throws(delegate { device.SetStage(-1); }, "negative stage");
            Throws(delegate { device.SetMode(3); }, "invalid mode");
            Check(wire.Writes == 4, "invalid mode/stages sent no commands");
            var blocked = new Fake(); var protectedDevice = new G304Device(blocked, delegate { throw new InvalidOperationException("G HUB active"); });
            protectedDevice.Read(); int reads = blocked.Reads;
            Throws(delegate { protectedDevice.SetDpi(1600); }, "G HUB DPI protection");
            Throws(delegate { protectedDevice.SetRate(2); }, "G HUB rate protection");
            Throws(delegate { protectedDevice.SetMode(1); }, "G HUB mode protection");
            Throws(delegate { protectedDevice.SetStage(0); }, "G HUB stage protection");
            Check(blocked.Writes == 0 && blocked.Reads == reads, "guard blocks before I/O");
            int guards = 0; var race = new Fake();
            var raceDevice = new G304Device(race, delegate { if (++guards == 2) throw new InvalidOperationException("G HUB started"); });
            raceDevice.Read(); Throws(delegate { raceDevice.SetDpi(1600); }, "recheck before settings command");
            Check(race.Writes == 0, "race guard no writes");
            var stale = new Fake { IgnoreWrite = true }; var mismatch = new G304Device(stale, delegate { }); mismatch.Read();
            Throws(delegate { mismatch.SetDpi(1600); }, "readback mismatch must fail"); Check(stale.Writes == 1, "no automatic write retry");
        }
        static void TestInstall()
        {
            var embedded = Installer.Package();
            Check(embedded.Count == 4 && embedded.ContainsKey("LogiG304.exe") && embedded.ContainsKey("LICENSE.txt") && embedded.ContainsKey("THIRD-PARTY.txt") && embedded.ContainsKey("使用说明.txt"), "complete self-contained installation payload");
            Check(Encoding.UTF8.GetString(embedded["THIRD-PARTY.txt"]).Contains("Copyright 2015"), "protocol attribution included");
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, "中文 path with spaces", "LogiG304");
            var payload = new Dictionary<string, byte[]> { { "LogiG304.exe", new byte[] { 1, 2, 3 } }, { "LICENSE.txt", Encoding.UTF8.GetBytes("test license") } };
            Installer.Install(destination, payload);
            Check(File.ReadAllBytes(Path.Combine(destination, "LogiG304.exe"))[2] == 3, "copy + verify");
            Check((File.GetAttributes(destination) & FileAttributes.ReparsePoint) == 0, "ordinary directory");
            Throws(delegate { Installer.Install(destination, payload); }, "reject nonempty destination");
            Check(File.ReadAllBytes(Path.Combine(destination, "LogiG304.exe"))[2] == 3, "existing file unchanged");
            Throws(delegate { Installer.ValidateTarget(Path.GetPathRoot(folder)); }, "reject drive root");
            Throws(delegate { Installer.ValidateTarget(@"\\server\share\App"); }, "reject UNC");
            Throws(delegate { Installer.ValidateTarget("relative-path"); }, "reject relative path");
            Throws(delegate { Installer.ValidateTarget(Path.Combine(folder, "folder:stream")); }, "reject alternate stream");
            string ghub = Path.Combine(folder, "ExistingGHub"); Directory.CreateDirectory(Path.Combine(ghub, "App"));
            File.WriteAllText(Path.Combine(ghub, "App", "lghub.exe"), "existing-program");
            Throws(delegate { Installer.ValidateTarget(Path.Combine(ghub, "Independent")); }, "reject existing G HUB ancestor");
            Check(File.ReadAllText(Path.Combine(ghub, "App", "lghub.exe")) == "existing-program", "existing G HUB untouched");
            string rollback = Path.Combine(folder, "Rollback");
            Throws(delegate { Installer.Install(rollback, new Dictionary<string, byte[]> { { "Good.txt", new byte[] { 1 } }, { "../escape.txt", new byte[] { 2 } } }); }, "invalid package path");
            Check(!File.Exists(Path.Combine(rollback, "Good.txt")) && !File.Exists(Path.Combine(folder, "escape.txt")), "rollback owns only newly created files");
        }
        static void TestView()
        {
            Application.EnableVisualStyles();
            using (var form = new MainForm(true))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "controller-preview.png")); }
                form.Close();
            }
            Check(true, "UI demo render without HID");
            using (var form = new InstallForm())
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer-preview.png")); }
                form.Close();
            }
            Check(true, "installer UI render without installation");
        }
        sealed class Fake : ITransport
        {
            public string Name = "G305 LIGHTSPEED Wireless Gaming Mouse";
            public int Dpi = 800, Rate = 1, Mode = 2, Stage, Writes, Reads;
            public bool BadPing, IgnoreWrite;
            public void Dispose() { }
            public byte[] Exchange(byte device, byte feature, byte function, byte[] p)
            {
                if (device != 1) throw new Exception("Unexpected device index");
                bool write = (feature == 26 && function == 3) || (feature == 9 && function == 2) || (feature == 7 && (function == 1 || function == 12));
                if (write) Writes++; else Reads++;
                var r = new byte[16];
                if (feature == 0 && function == 1) { r[0] = 4; r[1] = 2; r[2] = BadPing ? (byte)0 : (byte)0xA5; }
                else if (feature == 0 && function == 0)
                {
                    int id = (p[0] << 8) | p[1];
                    r[0] = (byte)(id == 5 ? 3 : id == 0x1000 ? 5 : id == 0x2201 ? 26 : id == 0x8060 ? 9 : id == 0x8100 ? 7 : 0);
                }
                else if (feature == 3 && function == 0) r[0] = (byte)Encoding.UTF8.GetByteCount(Name);
                else if (feature == 3 && function == 1) { byte[] bytes = Encoding.UTF8.GetBytes(Name); Array.Copy(bytes, p[0], r, 0, Math.Min(16, bytes.Length - p[0])); }
                else if (feature == 5 && function == 0) r[0] = 50;
                else if (feature == 26 && function == 0) r[0] = 1;
                else if (feature == 26 && function == 1) { r[2] = 200; r[3] = 0xE0; r[4] = 50; r[5] = 0x2E; r[6] = 0xE0; }
                else if (feature == 26 && function == 2) { r[1] = (byte)(Dpi >> 8); r[2] = (byte)Dpi; r[3] = 3; r[4] = 32; }
                else if (feature == 26 && function == 3) { if (!IgnoreWrite) Dpi = (p[1] << 8) | p[2]; }
                else if (feature == 9 && function == 0) r[0] = 0x8B;
                else if (feature == 9 && function == 1) r[0] = (byte)Rate;
                else if (feature == 9 && function == 2) { if (!IgnoreWrite) Rate = p[0]; }
                else if (feature == 7 && function == 0) { r[0] = 1; r[1] = 3; r[6] = 6; r[8] = 255; }
                else if (feature == 7 && function == 1) { if (!IgnoreWrite) Mode = p[0]; }
                else if (feature == 7 && function == 2) r[0] = (byte)Mode;
                else if (feature == 7 && function == 4) r[1] = 1;
                else if (feature == 7 && function == 5) { r[3] = 0x90; r[4] = 1; r[5] = 0x20; r[6] = 3; }
                else if (feature == 7 && function == 11) r[0] = (byte)Stage;
                else if (feature == 7 && function == 12) { if (!IgnoreWrite) Stage = p[0]; }
                else throw new Exception("Unexpected command (including any flash/firmware/notification command): " + feature + "/" + function);
                return r;
            }
        }
    }
}
