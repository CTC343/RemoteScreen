using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteScreen
{
    internal sealed class Address
    {
        internal readonly string Host;
        internal readonly int Port;
        internal readonly bool Tls;

        private Address(string host, int port, bool tls)
        {
            Host = host;
            Port = port;
            Tls = tls;
        }

        internal static Address Parse(string text)
        {
            string value = text.Trim();
            if (!value.Contains("://")) value = "tcp://" + value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Host.Length == 0 ||
                (uri.Scheme != "tcp" && uri.Scheme != "tls") || uri.UserInfo.Length != 0 ||
                (uri.AbsolutePath != "" && uri.AbsolutePath != "/") ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port == 0)
                throw new FormatException("请输入 IP:端口、主机名:端口，或 tls://主机名:端口。IPv6 地址请加方括号。");
            bool tls = uri.Scheme == "tls";
            return new Address(uri.IdnHost.Trim('[', ']'), uri.Port < 0 ? (tls ? 443 : Wire.DefaultPort) : uri.Port, tls);
        }
    }

    internal sealed class ClientConnection : IDisposable
    {
        internal readonly TcpClient Socket;
        internal readonly Stream Stream;

        internal ClientConnection(TcpClient socket, Stream stream)
        {
            Socket = socket;
            Stream = stream;
        }

        public void Dispose()
        {
            Socket.Close();
            Stream.Dispose();
        }
    }

    internal static class Wire
    {
        internal const int DefaultPort = 8765;
        internal const int MaxFrame = 512 * 1024;
        internal const int MaxCommand = 7;
        internal const byte View = 1, Control = 2, Frame = 3, Move = 4, Button = 5,
            Wheel = 6, Key = 7, Release = 8, Ping = 9;

        internal static async Task<ClientConnection> ConnectAsync(Address address, byte mode, CancellationToken token)
        {
            IPAddress literal;
            IPAddress[] targets;
            if (IPAddress.TryParse(address.Host, out literal))
                targets = new[] { literal };
            else
                targets = await ResolveAsync(address.Host, token).ConfigureAwait(false);

            Exception last = null;
            foreach (IPAddress target in targets)
            {
                token.ThrowIfCancellationRequested();
                var client = new TcpClient(target.AddressFamily);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (deadline.Token.Register(client.Close))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(4));
                    Stream stream = null;
                    try
                    {
                        await client.ConnectAsync(target, address.Port).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        client.NoDelay = true;
                        deadline.CancelAfter(TimeSpan.FromSeconds(8));
                        stream = await OpenClientStreamAsync(client, address).ConfigureAwait(false);
                        await RequestAsync(stream, mode, deadline.Token).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        return new ClientConnection(client, stream);
                    }
                    catch (Exception error)
                    {
                        client.Close();
                        if (stream != null) stream.Dispose();
                        token.ThrowIfCancellationRequested();
                        if (!(error is IOException || error is SocketException || error is AuthenticationException ||
                              error is ObjectDisposedException || error is OperationCanceledException))
                            throw;
                        last = error;
                    }
                }
            }
            throw new IOException("该地址尚未建立可用的共享连接。", last);
        }

        private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(6));
                var interruption = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (deadline.Token.Register(() => interruption.TrySetResult(true)))
                {
                    if (await Task.WhenAny(lookup, interruption.Task).ConfigureAwait(false) == lookup)
                        return await lookup.ConfigureAwait(false);
                }
            }
            ObserveLateLookupFailure(lookup);
            token.ThrowIfCancellationRequested();
            throw new IOException("域名解析超时。");
        }

        private static void ObserveLateLookupFailure(Task lookup)
        {
            lookup.ContinueWith(failed => failed.Exception.Handle(error => true), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static async Task<Stream> OpenClientStreamAsync(TcpClient client, Address address)
        {
            if (!address.Tls) return client.GetStream();
            var tls = new SslStream(client.GetStream(), false);
            try
            {
                await tls.AuthenticateAsClientAsync(address.Host, new X509CertificateCollection(),
                    SslProtocols.Tls12, true).ConfigureAwait(false);
                return tls;
            }
            catch
            {
                tls.Dispose();
                throw;
            }
        }

        internal static async Task RequestAsync(Stream stream, byte permission, CancellationToken token)
        {
            var hello = new[] { (byte)'R', (byte)'S', (byte)'C', (byte)'1', permission };
            await stream.WriteAsync(hello, 0, hello.Length, token).ConfigureAwait(false);
            byte[] reply = await ReadAsync(stream, 1, token).ConfigureAwait(false);
            if (reply[0] != 0xac) throw new IOException("接收端未接受连接。");
        }

        internal static async Task<byte> AcceptAsync(Stream stream, CancellationToken token)
        {
            byte[] hello = await ReadAsync(stream, 5, token).ConfigureAwait(false);
            if (hello[0] != 'R' || hello[1] != 'S' || hello[2] != 'C' || hello[3] != '1' ||
                (hello[4] != View && hello[4] != Control))
                throw new IOException("连接协议不匹配。");
            await stream.WriteAsync(new byte[] { 0xac }, 0, 1, token).ConfigureAwait(false);
            return hello[4];
        }

        internal static async Task<byte[]> ReadAsync(Stream stream, int size, CancellationToken token)
        {
            var data = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                int length = await stream.ReadAsync(data, offset, size - offset, token).ConfigureAwait(false);
                if (length == 0) throw new EndOfStreamException("对端已断开。");
                offset += length;
            }
            return data;
        }

        internal static async Task<byte[]> ReceiveAsync(Stream stream, int limit, CancellationToken token)
        {
            byte[] header = await ReadAsync(stream, 4, token).ConfigureAwait(false);
            uint size = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
            if (size == 0 || size > limit) throw new IOException("收到无效的消息长度。");
            return await ReadAsync(stream, (int)size, token).ConfigureAwait(false);
        }

        internal static async Task SendAsync(Stream stream, byte[] data, CancellationToken token)
        {
            int size = data.Length;
            var header = new[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size };
            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(data, 0, size, token).ConfigureAwait(false);
        }

        internal static async Task RunDuplexAsync(TcpClient socket, CancellationTokenSource lifetime,
            Task receive, Task send)
        {
            Task first = await Task.WhenAny(receive, send).ConfigureAwait(false);
            lifetime.Cancel();
            socket.Close();
            try { await Task.WhenAll(receive, send).ConfigureAwait(false); }
            catch (Exception) { } // Report the task that ended the session.
            await first.ConfigureAwait(false);
        }

        internal static int ReadWord(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }
    }
}
