using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LogiG304.Standalone
{
    internal static class Program
    {
        public const string Version = "0.1.0-preview.1";
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length > 0 && args[0] == "--probe") return Diagnostics();
                if (args.Length > 0 && args[0] != "--install" && args[0] != "--demo") throw new ArgumentException("Unknown option.");
                if (Path.GetFileNameWithoutExtension(Application.ExecutablePath).EndsWith("-Setup", StringComparison.OrdinalIgnoreCase) || (args.Length > 0 && args[0] == "--install"))
                { Application.Run(new InstallForm()); return 0; }
                bool created;
                using (var mutex = new Mutex(true, "Local\\LogiG304.Standalone.Controller", out created))
                {
                    if (!created) { MessageBox.Show("LogiG304 已在运行。", "LogiG304"); return 1; }
                    try { Application.Run(new MainForm(args.Length > 0 && args[0] == "--demo")); }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception e) { MessageBox.Show(e.Message, "LogiG304", MessageBoxButtons.OK, MessageBoxIcon.Warning); return 1; }
        }
        static int Diagnostics()
        {
            // Output goes only to stdout when run from a console. No automatic diagnostic files or identifiers.
            try
            {
                var endpoints = HidTransport.Enumerate();
                if (endpoints.Count != 1) throw new IOException("Expected exactly one G304/G305 receiver; found " + endpoints.Count);
                using (var link = new HidTransport(endpoints[0]))
                {
                    var mouse = new G304Device(link, WriteProtection.Check);
                    Console.WriteLine(mouse.Read().Summary());
                    Console.WriteLine("G HUB write protection: " + WriteProtection.IsGHubRunning());
                }
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
    }

    internal sealed class MainForm : Form
    {
        readonly bool demo;
        readonly Label summary = new Label(), status = new Label(), range = new Label();
        readonly NumericUpDown dpi = new NumericUpDown();
        readonly ComboBox rate = new ComboBox(), mode = new ComboBox(), stage = new ComboBox();
        readonly Button refresh = new Button(), dpiApply = new Button(), rateApply = new Button(), modeApply = new Button(), stageApply = new Button();
        HidEndpoint selected;
        MouseState current;
        bool busy;

        public MainForm(bool demo)
        {
            this.demo = demo;
            Text = "LogiG304 · 独立本地控制 " + Program.Version;
            Font = new Font("Microsoft YaHei UI", 10F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(740, 610); MinimumSize = new Size(756, 649);
            BackColor = Color.FromArgb(244, 247, 250); AutoScaleMode = AutoScaleMode.Dpi;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 8 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(panel);
            var title = new Label { Text = "G304，直接本地控制", Font = new Font(Font.FontFamily, 22, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            panel.Controls.Add(title);
            var intro = new Label { Text = "无需 G HUB · 无联网功能 · 不安装驱动或服务", AutoSize = true, ForeColor = Color.FromArgb(65, 88, 111), Margin = new Padding(0, 0, 0, 14) };
            panel.Controls.Add(intro);
            summary.Text = "尚未读取设备。"; summary.AutoSize = true; summary.MaximumSize = new Size(670, 0); summary.Margin = new Padding(0, 0, 0, 14); panel.Controls.Add(summary);
            var controls = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 5 };
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            mode.DropDownStyle = rate.DropDownStyle = stage.DropDownStyle = ComboBoxStyle.DropDownList;
            mode.Items.AddRange(new object[] { "板载模式：使用鼠标已有配置", "软件模式：调整当前 DPI / 回报率" });
            dpi.Minimum = 200; dpi.Maximum = 12000; dpi.Increment = 50; dpi.ThousandsSeparator = true;
            AddRow(controls, 0, "控制模式", mode, modeApply, "切换模式");
            AddRow(controls, 1, "当前 DPI", dpi, dpiApply, "应用 DPI");
            AddRow(controls, 2, "回报率", rate, rateApply, "应用回报率");
            AddRow(controls, 3, "已有板载档位", stage, stageApply, "切换档位");
            panel.Controls.Add(controls);
            range.AutoSize = true; range.Margin = new Padding(0, 12, 0, 12); panel.Controls.Add(range);
            status.AutoSize = true; status.MaximumSize = new Size(670, 0); status.ForeColor = Color.FromArgb(135, 78, 20); status.Margin = new Padding(0, 0, 0, 12); panel.Controls.Add(status);
            var note = new Label { AutoSize = true, MaximumSize = new Size(670, 0), ForeColor = Color.DimGray,
                Text = "预览版：读取已在实机验证；设置写入尚未完成实机验收。\n软件模式设置不保证断电保存。本版不编辑板载配置、不写闪存，不提供按键重映射或宏。安装、刷新和关闭都不会自动改鼠标设置。" };
            panel.Controls.Add(note);
            refresh.Text = "刷新设备"; refresh.AutoSize = true; refresh.Margin = new Padding(0, 16, 0, 0); panel.Controls.Add(refresh);
            refresh.Click += async delegate { await ReadDevice(); };
            dpiApply.Click += async delegate { int value = (int)dpi.Value; await Write("将当前 DPI 改为 " + value + "？", d => d.SetDpi(value)); };
            rateApply.Click += async delegate { var item = rate.SelectedItem as Choice; if (item != null) await Write("将当前回报率改为 " + item.Text + "？", d => d.SetRate(item.Value)); };
            modeApply.Click += async delegate { int value = mode.SelectedIndex + 1; await Write("切换控制模式可能立即改变 DPI 和按键行为。" + Environment.NewLine + "确认切换到" + (value == 1 ? "板载模式" : "软件模式") + "？", d => d.SetMode(value)); };
            stageApply.Click += async delegate { var item = stage.SelectedItem as Choice; if (item != null) await Write("切换到 " + item.Text + "？", d => d.SetStage(item.Value)); };
            Shown += async delegate { if (demo) ShowDemo(); else await ReadDevice(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "正在等待设备回应，请稍候再关闭。"; } };
        }
        static void AddRow(TableLayoutPanel panel, int row, string text, Control input, Button button, string action)
        {
            panel.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 0, 10) }, 0, row);
            input.Dock = DockStyle.Fill; input.Margin = new Padding(0, 7, 12, 7); panel.Controls.Add(input, 1, row);
            button.Text = action; button.Dock = DockStyle.Fill; button.Margin = new Padding(0, 5, 0, 5); panel.Controls.Add(button, 2, row);
        }
        void EnableActions(bool value)
        {
            refresh.Enabled = !busy && !demo;
            bool allowed = value && !busy && !demo && current != null;
            dpiApply.Enabled = allowed && current.DpiSupported && current.Mode == 2;
            rateApply.Enabled = allowed && current.RateSupported && current.Mode == 2;
            modeApply.Enabled = allowed && current.ModeSupported;
            stageApply.Enabled = allowed && current.Mode == 1 && stage.Items.Count > 0;
        }
        async Task ReadDevice()
        {
            busy = true; current = null; EnableActions(false); status.Text = "正在读取接收器，请移动鼠标唤醒…";
            try
            {
                MouseState state = await Task.Run(delegate {
                    var endpoints = HidTransport.Enumerate();
                    if (endpoints.Count == 0) throw new IOException("没有找到 G304 / G305 接收器。请插入原装 USB 接收器后刷新。");
                    if (endpoints.Count > 1) throw new IOException("发现多个 G304 / G305 接收器。请暂时只保留一个，避免控制错误设备。");
                    selected = endpoints[0];
                    using (var link = new HidTransport(selected)) return new G304Device(link, WriteProtection.Check).Read();
                });
                Populate(state);
                bool blocked = WriteProtection.IsGHubRunning();
                status.Text = blocked ? "保护已开启：罗技控制程序正在运行，当前只读。需要调整时，请自行退出 G HUB 托盘程序后刷新。" : "已连接。只有点击应用并确认后，才会向鼠标写入所选设置。";
                busy = false; EnableActions(!blocked);
            }
            catch (Exception e) { busy = false; current = null; summary.Text = "设备暂不可用"; status.Text = e.Message; EnableActions(false); }
        }
        void Populate(MouseState state)
        {
            current = state; summary.Text = state.Summary();
            if (state.DpiSupported)
            {
                dpi.Minimum = 0; dpi.Maximum = state.DpiMax; dpi.Minimum = state.DpiMin;
                dpi.Increment = Math.Max(1, state.DpiStep); dpi.Value = Math.Max(state.DpiMin, Math.Min(state.DpiMax, state.Dpi));
                range.Text = "设备支持：" + state.DpiMin + "–" + state.DpiMax + " DPI" + (state.DpiStep > 0 ? "，步进 " + state.DpiStep : "，仅限设备列出的值");
            }
            else range.Text = "设备未提供可用的 DPI 范围。";
            rate.Items.Clear();
            foreach (int ms in state.RatesMs) { int index = rate.Items.Add(new Choice(ms, (1000 / ms) + " Hz")); if (ms == state.RateMs) rate.SelectedIndex = index; }
            mode.SelectedIndex = state.ModeSupported ? state.Mode - 1 : -1;
            stage.Items.Clear();
            for (int i = 0; i < state.StageDpis.Length; i++)
                if (G304Device.ValidDpi(state, state.StageDpis[i]))
                { int index = stage.Items.Add(new Choice(i, "档位 " + (i + 1) + " · " + state.StageDpis[i] + " DPI")); if (state.Stage == i) stage.SelectedIndex = index; }
        }
        async Task Write(string prompt, Action<G304Device> action)
        {
            if (busy || demo || current == null) return;
            if (MessageBox.Show(this, prompt + Environment.NewLine + "本操作不修改 G HUB 配置文件。", "确认设备设置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            busy = true; EnableActions(false); status.Text = "正在应用并回读验证…";
            try
            {
                await Task.Run(delegate { WriteProtection.Check(); using (var link = new HidTransport(selected)) { var mouse = new G304Device(link, WriteProtection.Check); mouse.Read(); action(mouse); } });
                busy = false; await ReadDevice();
                if (current != null) status.Text = "设备已确认设置。软件模式下不保证断电保存；G HUB 再次接管时可能覆盖此设置。";
            }
            catch (Exception e)
            {
                busy = false; current = null; EnableActions(false); status.Text = "操作未完成：" + e.Message + " 请刷新检查当前状态。";
                MessageBox.Show(this, status.Text, "LogiG304", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        void ShowDemo()
        {
            var state = new MouseState { Name = "G304 · 界面演示（未连接设备）", Protocol = "4.2", Dpi = 800, DpiMin = 200, DpiMax = 12000, DpiStep = 50, DpiSupported = true, RateSupported = true, ModeSupported = true, RateMs = 1, Mode = 2, Battery = 50 };
            state.RatesMs.AddRange(new[] { 1, 2, 4, 8 }); Populate(state); status.Text = "演示模式：不打开 HID，不读取或写入任何鼠标设置。"; EnableActions(false);
        }
        sealed class Choice
        {
            public readonly int Value; public readonly string Text;
            public Choice(int value, string text) { Value = value; Text = text; }
            public override string ToString() { return Text; }
        }
    }
}
