using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace LogiG304.Standalone
{
    internal static class Installer
    {
        public static string ValidateTarget(string input)
        {
            if (string.IsNullOrWhiteSpace(input) || input.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathRooted(input))
                throw new IOException("请选择本机磁盘上的完整文件夹路径。");
            string target = Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.GetPathRoot(target);
            if (target.Length <= root.Length || target.IndexOf(':', 2) >= 0) throw new IOException("不能安装到磁盘根目录或数据流路径。");
            if (File.Exists(target)) throw new IOException("目标是文件，不是文件夹。");
            if (Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length != 0)
                throw new IOException("请选择新的空文件夹。安装器不会覆盖现有软件或配置。");
            var part = new DirectoryInfo(target);
            while (part != null)
            {
                if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("路径包含目录联接或符号链接，请选普通文件夹。");
                if (File.Exists(Path.Combine(part.FullName, "lghub.exe")) || File.Exists(Path.Combine(part.FullName, "App", "lghub.exe")))
                    throw new IOException("不能安装在现有 G HUB 目录内。");
                part = part.Parent;
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
            if (target.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) throw new IOException("不能安装到 Windows 系统目录。");
            return target;
        }

        public static void Install(string input, Dictionary<string, byte[]> payload)
        {
            string target = ValidateTarget(input);
            bool createdDirectory = !Directory.Exists(target);
            var createdFiles = new List<string>();
            try
            {
                Directory.CreateDirectory(target);
                foreach (var item in payload)
                {
                    if (Path.GetFileName(item.Key) != item.Key || item.Key.IndexOf(':') >= 0 || string.IsNullOrWhiteSpace(item.Key)) throw new IOException("Invalid package filename.");
                    string path = Path.Combine(target, item.Key);
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { createdFiles.Add(path); output.Write(item.Value, 0, item.Value.Length); output.Flush(true); }
                    if (!Equal(File.ReadAllBytes(path), item.Value)) throw new IOException("文件回读校验失败。");
                }
            }
            catch
            {
                // Only files created by this invocation; no recursive deletion or permission changes.
                foreach (string path in createdFiles) try { File.Delete(path); } catch { }
                if (createdDirectory) try { Directory.Delete(target, false); } catch { }
                throw;
            }
        }
        static bool Equal(byte[] left, byte[] right)
        { if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false; return true; }
        public static Dictionary<string, byte[]> Package()
        {
            var result = new Dictionary<string, byte[]>();
            result.Add("LogiG304.exe", File.ReadAllBytes(Application.ExecutablePath));
            foreach (string name in new[] { "LICENSE.txt", "THIRD-PARTY.txt", "使用说明.txt" })
                using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (source == null) throw new IOException("安装资源不完整：" + name);
                    using (var copy = new MemoryStream()) { source.CopyTo(copy); result.Add(name, copy.ToArray()); }
                }
            return result;
        }
    }

    internal sealed class InstallForm : Form
    {
        readonly TextBox path = new TextBox();
        readonly Label status = new Label();
        readonly Button install = new Button(), browse = new Button();
        public InstallForm()
        {
            Text = "安装 LogiG304 独立版 " + Program.Version; ClientSize = new Size(720, 380); MinimumSize = new Size(736, 419);
            Font = new Font("Microsoft YaHei UI", 10F); StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 6 };
            Controls.Add(panel);
            panel.Controls.Add(new Label { Text = "G304 独立控制，放到你选择的文件夹", Font = new Font(Font.FontFamily, 18, FontStyle.Bold), AutoSize = true });
            panel.Controls.Add(new Label { Text = "无需预装 G HUB，无需联网。不会改动 G HUB、鼠标设置、服务或目录权限。\n安装后双击目录中的 LogiG304.exe。也可以使用便携 ZIP 直接解压运行。", AutoSize = true, MaximumSize = new Size(660, 0), Margin = new Padding(0, 15, 0, 15) });
            var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            path.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LogiG304"); path.Dock = DockStyle.Fill;
            browse.Text = "选择…"; browse.Dock = DockStyle.Fill; row.Controls.Add(path, 0, 0); row.Controls.Add(browse, 1, 0); panel.Controls.Add(row);
            panel.Controls.Add(new Label { Text = "选择空文件夹，例如 D:\\Apps\\LogiG304。若上级目录不允许普通用户写入，请改选有写入权限的位置。卸载时关闭程序后删除本文件夹即可。", AutoSize = true, MaximumSize = new Size(660, 0), Margin = new Padding(0, 12, 0, 12) });
            status.AutoSize = true; status.MaximumSize = new Size(660, 0); panel.Controls.Add(status);
            install.Text = "安装"; install.AutoSize = true; panel.Controls.Add(install);
            browse.Click += delegate {
                using (var dialog = new FolderBrowserDialog { Description = "选择存放位置，将在里面新建 LogiG304 文件夹" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = Path.Combine(dialog.SelectedPath, "LogiG304");
            };
            install.Click += delegate {
                try
                {
                    string target = Installer.ValidateTarget(path.Text);
                    install.Enabled = browse.Enabled = path.Enabled = false;
                    Installer.Install(target, Installer.Package());
                    status.Text = "安装完成。请双击 " + Path.Combine(target, "LogiG304.exe") + "。安装器没有打开设备或更改设置。";
                    install.Text = "已完成";
                }
                catch (Exception e) { status.Text = "未完成：" + e.Message; install.Enabled = browse.Enabled = path.Enabled = true; }
            };
        }
    }
}
