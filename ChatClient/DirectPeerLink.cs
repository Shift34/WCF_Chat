using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ChatClient
{
    internal enum DirectLinkMessageType : byte
    {
        SignedMessage = 1,
        CallRequest = 2,
        CallAnswer = 3,
        CallEnd = 4,
        VoiceKeys = 5,
        PeerLeft = 6,
        VideoInvite = 7,
        VideoAccept = 8,
        VideoKeys = 9
    }

    internal sealed class DirectPeerLink : IDisposable
    {
        private readonly object _sendLock = new object();
        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;
        private volatile bool _disposed;
        private bool _sessionEstablished;

        public bool IsConnected => _stream != null && _client != null && _client.Connected;

        public event Action<byte[], byte[], byte[]> SignedMessageReceived;
        public event Action<string, int> CallRequestReceived;
        public event Action<bool, string, int> CallAnswerReceived;
        public event Action CallEndReceived;
        public event Action<byte[], byte[]> VoiceKeysReceived;
        public event Action<string, int> VideoInviteReceived;
        public event Action<bool, string, int> VideoAcceptReceived;
        public event Action<byte[]> VideoKeysReceived;
        public event Action PeerLeftReceived;
        public event Action<string> LinkStatusChanged;

        public void StartListening(int port, bool loopbackOnly = false)
        {
            Close();
            var address = loopbackOnly ? IPAddress.Loopback : IPAddress.Any;
            _listener = new TcpListener(address, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            _cts = new CancellationTokenSource();
            LinkStatusChanged?.Invoke("Ожидание прямого подключения…");
        }

        public async Task AcceptIncomingAsync(CancellationToken cancellationToken)
        {
            if (_listener == null)
                throw new InvalidOperationException("Слушатель прямого канала не запущен.");

            TcpClient accepted = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try { _listener.Stop(); } catch { }
            _listener = null;
            AttachClient(accepted);
            LinkStatusChanged?.Invoke("Прямой канал установлен");
            _ = Task.Run(() => ReadLoopAsync(_cts.Token));
        }

        public async Task StartHostAsync(int port, CancellationToken cancellationToken)
        {
            StartListening(port);
            await AcceptIncomingAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Close();
            var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AttachClient(tcp);
            LinkStatusChanged?.Invoke("Прямой канал установлен");
            _ = Task.Run(() => ReadLoopAsync(_cts.Token));
        }

        public void SendSignedMessage(byte[] hmac, byte[] message, byte[] signature) =>
            Send(DirectLinkMessageType.SignedMessage, w =>
            {
                WriteBytes(w, hmac);
                WriteBytes(w, message);
                WriteBytes(w, signature);
            });

        public void SendCallRequest(string callerIp, int callerPort) =>
            Send(DirectLinkMessageType.CallRequest, w =>
            {
                w.Write(callerIp ?? string.Empty);
                w.Write(callerPort);
            });

        public void SendCallAnswer(bool accept, string answererIp, int answererPort) =>
            Send(DirectLinkMessageType.CallAnswer, w =>
            {
                w.Write(accept);
                w.Write(answererIp ?? string.Empty);
                w.Write(answererPort);
            });

        public void SendCallEnd() => Send(DirectLinkMessageType.CallEnd, _ => { });

        public void SendVoiceKeys(byte[] sessionKey, byte[] iv) =>
            Send(DirectLinkMessageType.VoiceKeys, w =>
            {
                WriteBytes(w, sessionKey);
                WriteBytes(w, iv);
            });

        public void SendVideoInvite(string host, int port) =>
            Send(DirectLinkMessageType.VideoInvite, w =>
            {
                w.Write(host ?? string.Empty);
                w.Write(port);
            });

        public void SendVideoAccept(bool accept, string host, int port) =>
            Send(DirectLinkMessageType.VideoAccept, w =>
            {
                w.Write(accept);
                w.Write(host ?? string.Empty);
                w.Write(port);
            });

        public void SendVideoKeys(byte[] protectedKeys) =>
            Send(DirectLinkMessageType.VideoKeys, w => WriteBytes(w, protectedKeys));

        public void SendPeerLeft() => Send(DirectLinkMessageType.PeerLeft, _ => { });

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Close();
        }

        private void AttachClient(TcpClient tcp)
        {
            _client = tcp;
            _client.NoDelay = true;
            _stream = _client.GetStream();
            if (_cts == null)
                _cts = new CancellationTokenSource();
            _sessionEstablished = true;
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _stream != null)
                {
                    byte[] frame = await ReadFrameAsync(_stream, token).ConfigureAwait(false);
                    if (frame == null)
                        break;

                    using (var ms = new MemoryStream(frame))
                    using (var reader = new BinaryReader(ms))
                    {
                        var type = (DirectLinkMessageType)reader.ReadByte();
                        Dispatch(type, reader);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
            }
        }

        private void Dispatch(DirectLinkMessageType type, BinaryReader reader)
        {
            switch (type)
            {
                case DirectLinkMessageType.SignedMessage:
                    SignedMessageReceived?.Invoke(ReadBytes(reader), ReadBytes(reader), ReadBytes(reader));
                    break;
                case DirectLinkMessageType.CallRequest:
                    CallRequestReceived?.Invoke(reader.ReadString(), reader.ReadInt32());
                    break;
                case DirectLinkMessageType.CallAnswer:
                    CallAnswerReceived?.Invoke(reader.ReadBoolean(), reader.ReadString(), reader.ReadInt32());
                    break;
                case DirectLinkMessageType.CallEnd:
                    CallEndReceived?.Invoke();
                    break;
                case DirectLinkMessageType.VoiceKeys:
                    VoiceKeysReceived?.Invoke(ReadBytes(reader), ReadBytes(reader));
                    break;
                case DirectLinkMessageType.VideoInvite:
                    VideoInviteReceived?.Invoke(reader.ReadString(), reader.ReadInt32());
                    break;
                case DirectLinkMessageType.VideoAccept:
                    VideoAcceptReceived?.Invoke(reader.ReadBoolean(), reader.ReadString(), reader.ReadInt32());
                    break;
                case DirectLinkMessageType.VideoKeys:
                    VideoKeysReceived?.Invoke(ReadBytes(reader));
                    break;
                case DirectLinkMessageType.PeerLeft:
                    PeerLeftReceived?.Invoke();
                    break;
            }
        }

        private void Send(DirectLinkMessageType type, Action<BinaryWriter> writePayload)
        {
            if (!IsConnected || _stream == null)
                return;

            using (var payload = new MemoryStream())
            {
                using (var writer = new BinaryWriter(payload))
                {
                    writer.Write((byte)type);
                    writePayload(writer);
                }

                byte[] body = payload.ToArray();
                var header = BitConverter.GetBytes(body.Length);
                lock (_sendLock)
                {
                    _stream.Write(header, 0, 4);
                    _stream.Write(body, 0, body.Length);
                    _stream.Flush();
                }
            }
        }

        private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] lenBuf = await ReadExactAsync(stream, 4, token).ConfigureAwait(false);
            if (lenBuf == null)
                return null;

            int length = BitConverter.ToInt32(lenBuf, 0);
            if (length <= 0 || length > 1024 * 1024)
                return null;

            return await ReadExactAsync(stream, length, token).ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken token)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, token).ConfigureAwait(false);
                if (read == 0)
                    return null;
                offset += read;
            }

            return buffer;
        }

        private static void WriteBytes(BinaryWriter writer, byte[] data)
        {
            data = data ?? Array.Empty<byte>();
            writer.Write(data.Length);
            if (data.Length > 0)
                writer.Write(data);
        }

        private static byte[] ReadBytes(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length <= 0)
                return Array.Empty<byte>();
            return reader.ReadBytes(length);
        }

        private void Close()
        {
            try { _cts?.Cancel(); } catch { }
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            try { _listener?.Stop(); } catch { }
            _stream = null;
            _client = null;
            _listener = null;
            _cts?.Dispose();
            _cts = null;
            _sessionEstablished = false;
        }
    }
}
