using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteScreen
{
    internal static class ShareProgram
    {
        [STAThread]
        internal static void Main()
        {
            Desktop.UsePhysicalPixels();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ShareForm());
        }
    }

    internal sealed class ShareForm : Form
    {
        private readonly TextBox address = new TextBox { Dock = DockStyle.Fill };
        private readonly ComboBox permission = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        private readonly Button start = new Button { Text = "连接并同意共享", AutoSize = true };
        private readonly Button stop = new Button { Text = "停止 / 修改地址", AutoSize = true, Enabled = false };
        private readonly Label status = new Label { Text = "尚未共享屏幕。", Dock = DockStyle.Fill, AutoEllipsis = true };
        private CancellationTokenSource session;

        internal ShareForm()
        {
            Text = "远程屏幕 · 被控端";
            Font = new Font("Microsoft YaHei UI", 10);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(550, 245);
            MinimumSize = new Size(515, 280);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 7
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var title = new Label { Text = "共享当前主屏幕", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            var addressLabel = new Label { Text = "接收方地址", AutoSize = true, Margin = new Padding(3, 14, 3, 3) };
            var hint = new Label
            {
                Text = "填写接收方发来的 IP:端口 或 tls://主机名:端口。",
                AutoSize = true, ForeColor = Color.DimGray
            };
            permission.Items.AddRange(new object[] { "仅查看", "允许鼠标键盘操作" });
            permission.SelectedIndex = 0;
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 4) };
            actions.Controls.AddRange(new Control[] { permission, start, stop });
            layout.Controls.Add(title);
            layout.Controls.Add(addressLabel);
            layout.Controls.Add(address);
            layout.Controls.Add(hint);
            layout.Controls.Add(actions);
            layout.Controls.Add(new Label { Text = "停止或关闭此窗口即可结束共享。", AutoSize = true, ForeColor = Color.DimGray });
            layout.Controls.Add(status);
            Controls.Add(layout);

            start.Click += StartSharing;
            stop.Click += (sender, args) => Cancel();
            FormClosing += (sender, args) => Cancel();
        }

        private void Cancel()
        {
            if (session == null) return;
            stop.Enabled = false;
            status.Text = "正在停止……";
            session.Cancel();
        }

        private async void StartSharing(object sender, EventArgs args)
        {
            Address target;
            try { target = Address.Parse(address.Text); }
            catch (FormatException error)
            {
                status.Text = error.Message;
                address.Focus();
                return;
            }
            byte mode = permission.SelectedIndex == 0 ? Wire.View : Wire.Control;
            var lifetime = new CancellationTokenSource();
            session = lifetime;
            start.Enabled = address.Enabled = permission.Enabled = false;
            stop.Enabled = true;
            var updates = new Progress<string>(message =>
            {
                if (session == lifetime && !lifetime.IsCancellationRequested && !IsDisposed)
                    status.Text = message;
            });
            string result = "已停止共享。";
            try
            {
                await Task.Run(() => ShareSessionAsync(target, mode, updates, lifetime.Token));
            }
            catch (Exception error)
            {
                if (!lifetime.IsCancellationRequested)
                    result = error.Message + "。再次连接需重新点同意。";
            }
            finally
            {
                session = null;
                lifetime.Dispose();
                if (!IsDisposed)
                {
                    status.Text = result;
                    start.Enabled = address.Enabled = permission.Enabled = true;
                    stop.Enabled = false;
                }
            }
        }

        private static async Task ShareSessionAsync(Address address, byte mode, IProgress<string> updates,
            CancellationToken token)
        {
            for (int attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                updates.Report("正在连接（第 " + attempt + " 次）……");
                bool approvedConnection = false;
                try
                {
                    using (ClientConnection connection = await Wire.ConnectAsync(address, mode, token))
                    using (token.Register(connection.Socket.Close))
                    using (var session = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        token.ThrowIfCancellationRequested();
                        approvedConnection = true;
                        updates.Report(mode == Wire.Control
                            ? "正在共享屏幕 · 对方可操作鼠标键盘。"
                            : "正在共享屏幕 · 对方仅可查看。");
                        await Wire.RunDuplexAsync(connection.Socket, session,
                            ReceiveCommandsAsync(connection.Socket, connection.Stream, mode, session.Token),
                            SendFramesAsync(connection.Stream, session.Token));
                        return;
                    }
                }
                catch (Exception error)
                {
                    token.ThrowIfCancellationRequested();
                    if (approvedConnection)
                        throw new IOException("共享已断开（" + error.Message.TrimEnd('。', '.') + "）", error);
                    if (!(error is IOException || error is SocketException ||
                          error is System.Security.Authentication.AuthenticationException ||
                          error is ObjectDisposedException || error is OperationCanceledException))
                        throw;
                    updates.Report("暂未连上。3 秒后重试，可点停止修改地址。");
                }
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
        }

        private static async Task SendFramesAsync(Stream stream, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var interval = Stopwatch.StartNew();
                byte[] frame = Desktop.Capture();
                if (frame.Length > Wire.MaxFrame) throw new IOException("屏幕帧过大");
                await Wire.SendAsync(stream, frame, token);
                int delay = (int)(Math.Max(100, (frame.Length + 124) / 125) - interval.ElapsedMilliseconds);
                if (delay > 0) await Task.Delay(delay, token);
            }
        }

        private static async Task ReceiveCommandsAsync(TcpClient socket, Stream stream, byte mode, CancellationToken token)
        {
            using (var input = new RemoteInput())
            using (var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (watchdog.Token.Register(socket.Close))
            {
                while (true)
                {
                    watchdog.CancelAfter(TimeSpan.FromSeconds(12));
                    byte[] message = await Wire.ReceiveAsync(stream, Wire.MaxCommand, watchdog.Token);
                    if (mode == Wire.Control && !token.IsCancellationRequested) input.Apply(message);
                }
            }
        }
    }
}
