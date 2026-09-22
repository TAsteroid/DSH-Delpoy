using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DSHDeploy.Core;
using CoreLog = DSHDeploy.Core.Log;

namespace DSHDeploy.Ui
{
    /// <summary>
    /// The deployment wizard. All deployment work happens on a background thread; this class
    /// marshals every UI touch back to the message loop through <see cref="IUi"/>.
    /// </summary>
    public sealed class MainForm : Form, IUi
    {
        private const int LogLimit = 400 * 1024;

        private readonly Options _options;
        private readonly List<LogEntry> _pending = new List<LogEntry>();
        private readonly object _logGate = new object();
        private readonly SynchronizationContext _context;
        private readonly System.Windows.Forms.Timer _flushTimer;

        private PictureBox _iconBox;
        private Label _titleLabel;
        private Label _subtitleLabel;
        private Label _statusLabel;
        private ProgressBar _progress;
        private TextBox _logBox;
        private Button _closeButton;
        private Button _logButton;

        private int _loggedChars;
        private bool _finished;

        public Outcome Outcome { get; private set; }

        public MainForm(Options options)
        {
            _options = options;
            _context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            BuildLayout();
            ApplyIcon();

            _flushTimer = new System.Windows.Forms.Timer { Interval = 120 };
            _flushTimer.Tick += (s, e) => FlushLog();
            _flushTimer.Start();

            Shown += OnShown;
            FormClosing += OnFormClosing;
        }

        private void BuildLayout()
        {
            Text = "DSH 一键部署器 " + AppInfo.Version;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimumSize = new Size(640, 420);
            StartPosition = FormStartPosition.CenterScreen;
            // Size from the real working area rather than a fixed constant: a hard-coded client
            // size is unreliable across scaled displays, and a window larger than the screen
            // would put the log pane off-screen.
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(
                Math.Min(900, Math.Max(640, work.Width - 80)),
                Math.Min(700, Math.Max(440, work.Height - 80)));
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(250, 250, 252);
            // Every control here is positioned explicitly, so font-driven auto-scaling adds
            // nothing and actively hurts: AutoScaleMode.Font (the default) rescales the whole
            // layout whenever the form's font differs from the inherited default, which shrank
            // this window to ~67% and left the log pane too small to read. DPI scaling is
            // handled by the application manifest and AutoScaleMode.Dpi is opted into there.
            AutoScaleMode = AutoScaleMode.None;

            var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.White };
            _iconBox = new PictureBox
            {
                Location = new Point(18, 14),
                Size = new Size(48, 48),
                SizeMode = PictureBoxSizeMode.Zoom
            };
            _titleLabel = new Label
            {
                Text = "DeepSeek Harness 一键部署",
                Location = new Point(80, 14),
                Size = new Size(650, 26),
                Font = new Font(Font.FontFamily, 13f, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 30, 40)
            };
            _subtitleLabel = new Label
            {
                Text = "检测前置环境 → 安装缺失组件 → 安装插件市场 → 启动 DSH",
                Location = new Point(80, 42),
                Size = new Size(650, 22),
                ForeColor = Color.FromArgb(110, 110, 125)
            };
            header.Controls.Add(_iconBox);
            header.Controls.Add(_titleLabel);
            header.Controls.Add(_subtitleLabel);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 8) };
            _statusLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                Text = "准备中 ...",
                Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 40, 55)
            };
            _progress = new ProgressBar
            {
                Dock = DockStyle.Top,
                Height = 18,
                Style = ProgressBarStyle.Continuous,
                Maximum = 100,
                Minimum = 0,
                Value = 0
            };
            var spacer = new Panel { Dock = DockStyle.Top, Height = 10 };
            _logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false,
                BackColor = Color.FromArgb(24, 26, 33),
                ForeColor = Color.FromArgb(220, 220, 230),
                Font = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point),
                BorderStyle = BorderStyle.FixedSingle
            };
            body.Controls.Add(_logBox);
            body.Controls.Add(spacer);
            body.Controls.Add(_progress);
            body.Controls.Add(_statusLabel);
            // WinForms docks from the highest z-order index down, so the frontmost control
            // (index 0) claims the top edge first. Visual order: status, progress, spacer, log.
            body.Controls.SetChildIndex(_statusLabel, 0);
            body.Controls.SetChildIndex(_progress, 1);
            body.Controls.SetChildIndex(spacer, 2);
            body.Controls.SetChildIndex(_logBox, 3);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.White };
            _logButton = new Button
            {
                Text = "打开日志",
                Location = new Point(18, 14),
                Size = new Size(96, 30),
                FlatStyle = FlatStyle.System
            };
            _logButton.Click += (s, e) => OpenLogFolder();
            _closeButton = new Button
            {
                Text = "关闭",
                Size = new Size(96, 30),
                Enabled = false,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _closeButton.Location = new Point(footer.ClientSize.Width - _closeButton.Width - 18, 14);
            _closeButton.Click += (s, e) => Close();
            footer.Controls.Add(_logButton);
            footer.Controls.Add(_closeButton);

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(header);
        }

        private void ApplyIcon()
        {
            // The exe carries its icon group from /win32icon, so the shell-selected associated
            // icon is already the right size for the title bar and taskbar.
            Icon shellIcon = null;
            try { shellIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            if (shellIcon != null)
            {
                try { Icon = shellIcon; } catch { }
            }
            else
            {
                try { Icon = SystemIcons.Application; } catch { }
            }

            System.Drawing.Image image = LoadHeaderImage(shellIcon);
            _iconBox.Image = image;
        }

        /// <summary>Header image from the embedded ICO, scaled to the box; falls back to the shell icon.</summary>
        private static System.Drawing.Image LoadHeaderImage(Icon fallback)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("deepseek-bowl.ico"))
                {
                    if (stream != null)
                    {
                        using (var source = new Icon(stream))
                        using (var scaled = new Icon(source, new Size(48, 48)))
                        {
                            return scaled.ToBitmap();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CoreLog.Debug("加载内嵌图标失败: {0}", ex.Message);
            }

            if (fallback != null)
            {
                try { return fallback.ToBitmap(); } catch { }
            }
            return null;
        }

        private void OnShown(object sender, EventArgs e)
        {
            // The logger is initialized during startup, before this window exists, with a sink
            // that discards everything. Wire it to this form now so the log pane is live.
            CoreLog.Attach(this);
            CoreLog.Info("DSH 一键部署器 {0} 启动", AppInfo.Version);
            CoreLog.Info("日志文件: {0}", CoreLog.Path);

            var thread = new Thread(RunDeployment) { IsBackground = true, Name = "deploy" };
            thread.Start();
        }

        private void RunDeployment()
        {
            try
            {
                var deployment = new Deployment(_options, this);
                Outcome outcome = deployment.Run();
                Outcome = outcome;
                Post(() =>
                {
                    _finished = true;
                    _closeButton.Enabled = true;
                    _progress.Style = ProgressBarStyle.Continuous;
                    _statusLabel.Text = outcome.Success ? "部署完成" : "部署未完全成功";
                    ShowSummary(outcome);
                });
            }
            catch (Exception ex)
            {
                CoreLog.Error("部署失败: {0}", ex.ToString());
                Post(() =>
                {
                    _finished = true;
                    _closeButton.Enabled = true;
                    _statusLabel.Text = "部署失败";
                    MessageBox.Show(this,
                        "部署过程中出现错误:\r\n\r\n" + ex.Message + "\r\n\r\n日志: " + CoreLog.Path,
                        "DSH 一键部署器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            }
        }

        private void ShowSummary(Outcome outcome)
        {
            var sb = new StringBuilder();
            sb.AppendLine(outcome.Success ? "部署完成。" : "部署未完全成功。");
            sb.AppendLine();
            foreach (string action in outcome.Actions) sb.AppendLine("• " + action);
            if (!string.IsNullOrEmpty(outcome.Url) && outcome.Success)
            {
                sb.AppendLine();
                sb.AppendLine("地址: " + outcome.Url);
            }
            sb.AppendLine();
            sb.AppendLine("日志: " + outcome.LogPath);

            MessageBoxIcon icon = outcome.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning;
            MessageBox.Show(this, sb.ToString(), "DSH 一键部署器", MessageBoxButtons.OK, icon);
        }

        // ------------------------------------------------------------------ IUi

        public void Log(LogEntry entry)
        {
            lock (_logGate) _pending.Add(entry);
        }

        public void SetStatus(string text)
        {
            Post(() => _statusLabel.Text = text);
        }

        public void SetProgress(int percent)
        {
            Post(() =>
            {
                if (percent < 0)
                {
                    _progress.Style = ProgressBarStyle.Marquee;
                    return;
                }
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = Math.Max(0, Math.Min(100, percent));
            });
        }

        public bool Consent(string title, string message)
        {
            bool result = false;
            Post(() =>
            {
                result = MessageBox.Show(this, message, title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) == DialogResult.Yes;
            }, true);
            return result;
        }

        public string AskString(string title, string message, bool secret)
        {
            string result = null;
            Post(() =>
            {
                using (var dialog = new InputDialog(title, message, secret))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) result = dialog.Value;
                }
            }, true);
            return result;
        }

        public void Finish(Outcome outcome)
        {
            Outcome = outcome;
        }

        // ------------------------------------------------------------------ helpers

        private void Post(Action action, bool synchronous = false)
        {
            if (_context == null)
            {
                action();
                return;
            }
            if (synchronous)
            {
                _context.Send(_ => action(), null);
            }
            else
            {
                _context.Post(_ => action(), null);
            }
        }

        private void FlushLog()
        {
            List<LogEntry> batch = null;
            lock (_logGate)
            {
                if (_pending.Count == 0) return;
                batch = new List<LogEntry>(_pending);
                _pending.Clear();
            }

            var builder = new StringBuilder();
            foreach (LogEntry entry in batch) builder.AppendLine(entry.ToString());
            if (_loggedChars + builder.Length > LogLimit)
            {
                _logBox.Clear();
                _loggedChars = 0;
            }
            _loggedChars += builder.Length;
            _logBox.AppendText(builder.ToString());
        }

        private void OpenLogFolder()
        {
            try
            {
                string path = CoreLog.Path;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                else
                {
                    string dir = Path.GetDirectoryName(path ?? string.Empty);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开日志目录: " + ex.Message, "DSH 一键部署器",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_finished) return;
            DialogResult answer = MessageBox.Show(this,
                "部署仍在进行中。确定要退出吗? 已完成的步骤会被保留。",
                "DSH 一键部署器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) e.Cancel = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _flushTimer != null)
            {
                _flushTimer.Stop();
                _flushTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Small modal prompt for the API key (and any future free-text answer).</summary>
    internal sealed class InputDialog : Form
    {
        private readonly TextBox _input;

        public string Value
        {
            get { return _input.Text.Trim(); }
        }

        public InputDialog(string title, string message, bool secret)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 260);
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            var label = new Label
            {
                Text = message,
                Location = new Point(16, 14),
                Size = new Size(488, 150)
            };
            _input = new TextBox
            {
                Location = new Point(16, 172),
                Size = new Size(488, 26),
                UseSystemPasswordChar = secret
            };
            var ok = new Button
            {
                Text = "保存",
                Location = new Point(330, 212),
                Size = new Size(84, 30),
                DialogResult = DialogResult.OK,
                FlatStyle = FlatStyle.System
            };
            var cancel = new Button
            {
                Text = "稍后再说",
                Location = new Point(420, 212),
                Size = new Size(84, 30),
                DialogResult = DialogResult.Cancel,
                FlatStyle = FlatStyle.System
            };

            Controls.Add(label);
            Controls.Add(_input);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
