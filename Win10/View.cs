using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteScreen
{
    internal static class ViewProgram
    {
        [STAThread]
        internal static void Main()
        {
            Desktop.UsePhysicalPixels();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ViewForm());
        }
    }

    internal sealed class ViewForm : Form
    {
        private readonly NumericUpDown port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Wire.DefaultPort, Width = 80 };
        private readonly CheckBox direct = new CheckBox { Text = "允许其他网卡直连", AutoSize = true };
        private readonly Button start = new Button { Text = "开始接收", AutoSize = true };
        private readonly Button stop = new Button { Text = "停止接收", AutoSize = true, Enabled = false };
        private readonly Label status = new Label { Text = "尚未开始接收。", AutoSize = true, Margin = new Padding(10, 5, 5, 10) };
        private readonly ScreenView screen;
        private readonly System.Windows.Forms.Timer displayTimer = new System.Windows.Forms.Timer { Interval = 35 };
        private CancellationTokenSource listener;
        private volatile Peer active;
        private Peer displayed;

        internal ViewForm()
        {
            Text = "远程屏幕 · 控制端";
            Font = new Font("Microsoft YaHei UI", 10);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(985, 650);
            MinimumSize = new Size(625, 350);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 7, 8, 0) };
            toolbar.Controls.AddRange(new Control[]
            {
                new Label { Text = "接收端口", AutoSize = true, Margin = new Padding(3, 7, 3, 0) },
                port, direct, start, stop
            });
            screen = new ScreenView(Send) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            layout.Controls.Add(toolbar);
            layout.Controls.Add(status);
            layout.Controls.Add(screen);
            Controls.Add(layout);

            start.Click += StartListening;
            stop.Click += (sender, args) => StopListening();
            Deactivate += (sender, args) => screen.Release();
            FormClosing += (sender, args) => StopListening();
            FormClosed += (sender, args) => displayTimer.Dispose();
            displayTimer.Tick += (sender, args) => ShowLatestFrame();
            displayTimer.Start();
        }

        private void StopListening()
        {
            if (listener == null) return;
            stop.Enabled = false;
            status.Text = "正在停止……";
            listener.Cancel();
        }

        private async void StartListening(object sender, EventArgs args)
        {
            var lifetime = new CancellationTokenSource();
            listener = lifetime;
            int selectedPort = (int)port.Value;
            bool allowDirect = direct.Checked;
            start.Enabled = port.Enabled = direct.Enabled = false;
            stop.Enabled = true;
            var updates = new Progress<string>(message =>
            {
                if (listener == lifetime && !lifetime.IsCancellationRequested && !IsDisposed)
                    status.Text = message;
            });
            string result = "已停止接收。";
            try
            {
                await Task.Run(() => ListenAsync(selectedPort, allowDirect, updates, lifetime.Token));
            }
            catch (Exception error)
            {
                if (!lifetime.IsCancellationRequested) result = "无法开始接收：" + error.Message;
            }
            finally
            {
                listener = null;
                lifetime.Dispose();
                if (!IsDisposed)
                {
                    status.Text = result;
                    start.Enabled = port.Enabled = direct.Enabled = true;
                    stop.Enabled = false;
                }
            }
        }

        private async Task ListenAsync(int number, bool allowDirect, IProgress<string> updates, CancellationToken token)
        {
            bool dualStack = allowDirect && Socket.OSSupportsIPv6;
            IPAddress bind = dualStack ? IPAddress.IPv6Any : (allowDirect ? IPAddress.Any : IPAddress.Loopback);
            var server = new TcpListener(bind, number);
            if (dualStack) server.Server.DualMode = true;
            try
            {
                server.Start(2);
                using (token.Register(server.Stop))
                {
                    string listening = allowDirect ? "正在等待直连 · 端口 " + number
                        : "正在等待转发 · 本机 127.0.0.1:" + number;
                    updates.Report(listening);
                    while (!token.IsCancellationRequested)
                    {
                        using (TcpClient socket = await server.AcceptTcpClientAsync())
                        using (token.Register(socket.Close))
                        {
                            socket.NoDelay = true;
                            try { await ViewSessionAsync(socket, updates, token); }
                            catch (Exception error)
                            {
                                updates.Report("连接已结束 · " + error.Message.TrimEnd('。', '.') + "。 " + listening);
                            }
                        }
                    }
                }
            }
            finally { server.Stop(); }
        }

        private async Task ViewSessionAsync(TcpClient socket, IProgress<string> updates, CancellationToken token)
        {
            Stream stream = socket.GetStream();
            byte mode;
            using (var negotiation = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (negotiation.Token.Register(socket.Close))
            {
                negotiation.CancelAfter(TimeSpan.FromSeconds(8));
                mode = await Wire.AcceptAsync(stream, negotiation.Token);
            }
            using (var session = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var peer = new Peer(socket, mode == Wire.Control);
                active = peer;
                updates.Report(peer.CanControl ? "已连接 · 点击画面后可操作鼠标键盘。"
                    : "已连接 · 对方授权仅查看。");
                try
                {
                    await Wire.RunDuplexAsync(socket, session,
                        ReceiveFramesAsync(socket, stream, peer, session.Token),
                        Task.Run(() => SendCommandsAsync(stream, peer, session.Token)));
                }
                finally
                {
                    active = null;
                    Bitmap last = peer.TakeFrame();
                    if (last != null) last.Dispose();
                }
            }
        }

        private static async Task ReceiveFramesAsync(TcpClient socket, Stream stream, Peer peer, CancellationToken token)
        {
            using (var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (watchdog.Token.Register(socket.Close))
            {
                while (true)
                {
                    watchdog.CancelAfter(TimeSpan.FromSeconds(12));
                    byte[] packet = await Wire.ReceiveAsync(stream, Wire.MaxFrame, watchdog.Token);
                    if (packet.Length < 4 || packet[0] != Wire.Frame || packet[1] != 255 || packet[2] != 216)
                        throw new IOException("收到无效的画面");
                    using (var bytes = new MemoryStream(packet, 1, packet.Length - 1, false))
                    using (Image decoded = Image.FromStream(bytes, false, true))
                    {
                        if (decoded.Width > 1024 || decoded.Height > 768)
                            throw new IOException("画面尺寸超出范围");
                        peer.SetFrame(new Bitmap(decoded));
                    }
                }
            }
        }

        private static async Task SendCommandsAsync(Stream stream, Peer peer, CancellationToken token)
        {
            var ping = new byte[] { Wire.Ping };
            while (true)
            {
                byte[] command;
                if (!peer.Commands.TryTake(out command, 1000, token)) command = ping;
                await Wire.SendAsync(stream, command, token);
            }
        }

        private void Send(byte[] command)
        {
            Peer peer = active;
            if (peer != null && peer == displayed && peer.CanControl && !peer.Commands.TryAdd(command))
                peer.Socket.Close();
        }

        private void ShowLatestFrame()
        {
            Peer peer = active;
            if (peer != displayed)
            {
                screen.Interactive = false;
                screen.SetFrame(null);
                displayed = peer;
            }
            screen.Interactive = peer != null && peer.CanControl;
            if (peer == null) return;
            Bitmap latest = peer.TakeFrame();
            if (latest != null) screen.SetFrame(latest);
            screen.FlushMove();
        }

        private sealed class Peer
        {
            internal readonly TcpClient Socket;
            internal readonly bool CanControl;
            internal readonly BlockingCollection<byte[]> Commands = new BlockingCollection<byte[]>(96);
            private Bitmap pending;

            internal Peer(TcpClient socket, bool canControl)
            {
                Socket = socket;
                CanControl = canControl;
            }

            internal void SetFrame(Bitmap bitmap)
            {
                Bitmap old = Interlocked.Exchange(ref pending, bitmap);
                if (old != null) old.Dispose();
            }

            internal Bitmap TakeFrame()
            {
                return Interlocked.Exchange(ref pending, null);
            }
        }
    }

    internal sealed class ScreenView : Control
    {
        private readonly Action<byte[]> send;
        private Bitmap frame;
        private byte[] pendingMove;
        private int heldButtons;
        private bool interactive;

        internal ScreenView(Action<byte[]> send)
        {
            this.send = send;
            DoubleBuffered = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(24, 27, 32);
            ForeColor = Color.Gainsboro;
        }

        internal bool Interactive
        {
            set
            {
                if (interactive && !value) Release();
                interactive = value;
            }
        }

        internal void SetFrame(Bitmap bitmap)
        {
            if (frame == bitmap) return;
            if (frame != null) frame.Dispose();
            frame = bitmap;
            Invalidate();
        }

        private Rectangle FrameBounds()
        {
            if (frame == null || Width <= 0 || Height <= 0) return Rectangle.Empty;
            double scale = Math.Min((double)Width / frame.Width, (double)Height / frame.Height);
            int width = Math.Max(1, (int)(scale * frame.Width));
            int height = Math.Max(1, (int)(scale * frame.Height));
            return new Rectangle((Width - width) / 2, (Height - height) / 2, width, height);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            if (frame == null)
            {
                TextRenderer.DrawText(args.Graphics, "等待对方同意共享屏幕", Font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            args.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            args.Graphics.DrawImage(frame, FrameBounds());
        }

        private byte[] MousePacket(byte type, Point point, bool clamp, params byte[] tail)
        {
            Rectangle bounds = FrameBounds();
            if (!interactive || bounds.IsEmpty || (!clamp && !bounds.Contains(point))) return null;
            int x = 65535 * Math.Max(0, Math.Min(bounds.Width - 1, point.X - bounds.Left)) / Math.Max(1, bounds.Width - 1);
            int y = 65535 * Math.Max(0, Math.Min(bounds.Height - 1, point.Y - bounds.Top)) / Math.Max(1, bounds.Height - 1);
            var packet = new byte[5 + tail.Length];
            packet[0] = type;
            packet[1] = (byte)(x >> 8);
            packet[2] = (byte)x;
            packet[3] = (byte)(y >> 8);
            packet[4] = (byte)y;
            Array.Copy(tail, 0, packet, 5, tail.Length);
            return packet;
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            pendingMove = MousePacket(Wire.Move, args.Location, heldButtons != 0);
        }

        internal void FlushMove()
        {
            if (pendingMove == null) return;
            send(pendingMove);
            pendingMove = null;
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            MouseButton(args, true);
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            MouseButton(args, false);
        }

        private void MouseButton(MouseEventArgs args, bool down)
        {
            int button;
            switch (args.Button)
            {
                case MouseButtons.Left: button = 0; break;
                case MouseButtons.Right: button = 1; break;
                case MouseButtons.Middle: button = 2; break;
                default: return;
            }
            byte[] message = MousePacket(Wire.Button, args.Location, !down, (byte)button, down ? (byte)1 : (byte)0);
            if (message == null) return;
            if (down)
            {
                Focus();
                heldButtons |= 1 << button;
                Capture = true;
            }
            else heldButtons &= ~(1 << button);
            pendingMove = null;
            send(message);
            if (heldButtons == 0) Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs args)
        {
            base.OnMouseWheel(args);
            byte[] message = MousePacket(Wire.Wheel, args.Location, false, (byte)(args.Delta >> 8), (byte)args.Delta);
            if (message == null) return;
            pendingMove = null;
            send(message);
        }

        private bool SendKey(Message message)
        {
            if (!interactive || !Focused || frame == null) return false;
            int kind = message.Msg;
            if (kind != 0x100 && kind != 0x104 && kind != 0x101 && kind != 0x105) return false;
            byte extended = (byte)((message.LParam.ToInt64() >> 24) & 1);
            send(new[] { Wire.Key, (byte)message.WParam.ToInt64(), extended,
                kind == 0x100 || kind == 0x104 ? (byte)1 : (byte)0 });
            return true;
        }

        public override bool PreProcessMessage(ref Message message)
        {
            return SendKey(message) || base.PreProcessMessage(ref message);
        }

        protected override void WndProc(ref Message message)
        {
            if (!SendKey(message)) base.WndProc(ref message);
        }

        protected override void OnLostFocus(EventArgs args)
        {
            Release();
            base.OnLostFocus(args);
        }

        protected override void OnMouseCaptureChanged(EventArgs args)
        {
            if (!Capture && heldButtons != 0) Release();
            base.OnMouseCaptureChanged(args);
        }

        internal void Release()
        {
            pendingMove = null;
            heldButtons = 0;
            if (Capture) Capture = false;
            if (interactive) send(new[] { Wire.Release });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && frame != null)
            {
                frame.Dispose();
                frame = null;
            }
            base.Dispose(disposing);
        }
    }
}
