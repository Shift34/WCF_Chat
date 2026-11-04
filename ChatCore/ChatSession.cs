using ChatClient;
using ChatClient.Protocol_Signal;
using ChatClient.ProtocolSignal;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ChatCore
{
    public sealed class ChatLine
    {
        public string Text { get; init; }
        public bool IsOwn { get; init; }
        public bool IsSystem { get; init; }
    }

    public sealed class ChatSession : IAsyncDisposable
    {
        private readonly PortableEcdh _ecdh = new PortableEcdh();
        private readonly GostSignature _signature = new GostSignature();
        private ChatHubClient _hub;
        private DirectPeerLink _direct;
        private CancellationTokenSource _directCts;
        private Kuznechik _cipher;
        private byte[] _sharedSecret;
        private byte[] _aesKey;
        private byte[] _hmacKey;
        private int _userId = -1;
        private int _peerId = -1;
        private string _pendingHost;
        private int _pendingPort;
        private bool _connectInProgress;
        private int _listenPort;
        private string _hubUrl = string.Empty;

        public bool CanSend { get; private set; }
        public string Status { get; private set; } = "Не подключено";

        public event Action<string> StatusChanged;
        public event Action<ChatLine> LineAdded;

        public async Task ConnectAsync(string hubUrl)
        {
            await DisposeHubAsync();
            _hubUrl = hubUrl ?? string.Empty;
            _hub = new ChatHubClient(hubUrl);
            _hub.GetConnectionAndPublicKey += OnPeerKeys;
            _hub.CompareHMAC += OnCompareHmac;
            _hub.GetConnectionProtocol += OnProtocol;
            _hub.LeftChat += OnPeerLeft;
            _hub.MessageNotification += text => Add(new ChatLine { Text = text, IsSystem = true });
            _hub.MessageCallBackSigned += OnSignedMessage;
            _hub.EstablishDirectLink += (host, port) =>
            {
                _pendingHost = host;
                _pendingPort = port;
                _ = ConnectDirectAsync();
            };

            await _hub.StartAsync();
            _userId = await _hub.CreateUserAsync(_ecdh.PublicBlob, _signature.GetPublicKey());
            SetStatus("Подключено к серверу. Можно искать собеседника.");
        }

        public async Task FindAsync()
        {
            if (_hub == null || _userId < 0)
                return;

            CanSend = false;
            SetStatus("Ищем собеседника…");
            await _hub.ConnectAsync(_userId);
        }

        public async Task LeaveAsync()
        {
            CanSend = false;
            try
            {
                if (_hub != null && _userId >= 0)
                    await _hub.DisconnectAsync(_userId);
            }
            catch
            {
            }

            TearDownDirect();
            _cipher = null;
            _peerId = -1;
            SetStatus("Вы вышли из чата.");
        }

        public async Task SendAsync(string text)
        {
            if (!CanSend || _cipher == null || string.IsNullOrWhiteSpace(text))
                return;

            byte[] plain = Encoding.UTF8.GetBytes(text.Trim());
            byte[] signature = _signature.Sign(plain);
            byte[] encrypted = _cipher.KuzEncript(plain, _aesKey);
            byte[] hmac = SignalProtocolExample.ComputeHmac(_hmacKey, encrypted);

            if (_direct?.IsConnected == true)
                _direct.SendSignedMessage(hmac, encrypted, signature);
            else if (_hub != null)
                await _hub.SendSignedMessageAsync(hmac, encrypted, signature, _userId);

            Add(new ChatLine { Text = text.Trim(), IsOwn = true });
        }

        public async ValueTask DisposeAsync()
        {
            await LeaveAsync();
            await DisposeHubAsync();
        }

        private void OnPeerKeys(byte[] publicKey, byte[] signPublicKey)
        {
            if (publicKey == null || signPublicKey == null)
            {
                SetStatus("Ищем собеседника…");
                return;
            }

            _signature.SetPeerKey(signPublicKey);
            _sharedSecret = _ecdh.DeriveKeyMaterial(publicKey);
            _ = SendNonceAsync();
        }

        private async Task SendNonceAsync()
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(32);
            byte[] hmac = SignalProtocolExample.ComputeHmac(nonce, _sharedSecret);
            await _hub.SendHashProtocolAsync(nonce, hmac, _userId);
        }

        private async void OnCompareHmac(byte[] key, byte[] hmac)
        {
            byte[] test = SignalProtocolExample.ComputeHmac(key, _sharedSecret);
            bool matches = test.AsSpan().SequenceEqual(hmac);
            await _hub.SendHashEqualsAsync(matches, _userId);
        }

        private void OnProtocol(bool ok, int peerId)
        {
            if (!ok)
            {
                CanSend = false;
                SetStatus("Не удалось установить защищённый канал.");
                return;
            }

            _peerId = peerId;
            SignalProtocolExample.DeriveKeys(_sharedSecret, out _aesKey, out _hmacKey);
            _cipher = new Kuznechik();
            CanSend = true;
            SetStatus("Собеседник на связи.");
            Add(new ChatLine { Text = "Защищённый канал установлен.", IsSystem = true });
            _ = BeginDirectAsync();
        }

        private void OnSignedMessage(byte[] hmac, string caption, byte[] bytes, byte[] signature)
        {
            if (_cipher == null || hmac == null || bytes == null)
                return;

            if (!SignalProtocolExample.ComputeHmac(_hmacKey, bytes).AsSpan().SequenceEqual(hmac))
            {
                Add(new ChatLine { Text = "Поддельное сообщение", IsSystem = true });
                return;
            }

            byte[] plain = _cipher.KuzDecript(bytes, _aesKey);
            bool valid = _signature.Verify(plain, signature);
            string text = Encoding.UTF8.GetString(plain);
            if (!valid)
                text = "Подпись не сошлась: " + text;

            Add(new ChatLine { Text = text, IsOwn = false });
        }

        private void OnPeerLeft()
        {
            CanSend = false;
            TearDownDirect();
            SetStatus("Собеседник отключился.");
            Add(new ChatLine { Text = "Собеседник покинул чат.", IsSystem = true });
        }

        private async Task BeginDirectAsync()
        {
            TearDownDirect();
            _direct = new DirectPeerLink();
            _direct.SignedMessageReceived += (hmac, bytes, signature) => OnSignedMessage(hmac, string.Empty, bytes, signature);
            _directCts = new CancellationTokenSource();
            string advertise = AdvertiseAddress();

            if (_userId < _peerId)
            {
                _listenPort = FreePort();
                _direct.StartListening(_listenPort, advertise == "127.0.0.1");
                await _hub.RegisterDirectEndpointAsync(_userId, advertise, _listenPort);
                await _hub.RefreshDirectLinkAsync(_userId);
                try
                {
                    await _direct.AcceptIncomingAsync(_directCts.Token);
                    await _hub.NotifyDirectLinkReadyAsync(_userId);
                    Add(new ChatLine { Text = "Прямой канал активен.", IsSystem = true });
                }
                catch (OperationCanceledException)
                {
                }
            }
            else
            {
                await _hub.RegisterDirectEndpointAsync(_userId, advertise, 0);
                await _hub.RefreshDirectLinkAsync(_userId);
                await ConnectDirectAsync();
            }
        }

        private async Task ConnectDirectAsync()
        {
            if (_direct == null || _connectInProgress || _pendingPort <= 0 || _userId < _peerId)
                return;

            _connectInProgress = true;
            try
            {
                for (int i = 0; i < 8 && _direct?.IsConnected != true; i++)
                {
                    if (i > 0)
                        await Task.Delay(300);

                    if (!await TryConnect(_pendingHost, _pendingPort) && !await TryConnect("127.0.0.1", _pendingPort))
                        continue;
                }

                if (_direct?.IsConnected == true)
                {
                    await _hub.NotifyDirectLinkReadyAsync(_userId);
                    Add(new ChatLine { Text = "Прямой канал активен.", IsSystem = true });
                }
            }
            finally
            {
                _connectInProgress = false;
            }
        }

        private void TearDownDirect()
        {
            try { _directCts?.Cancel(); } catch { }
            _directCts?.Dispose();
            _directCts = null;
            _direct?.Dispose();
            _direct = null;
        }

        private async Task<bool> TryConnect(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host) || _direct == null)
                return false;

            try
            {
                await _direct.ConnectAsync(host, port, _directCts.Token);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string AdvertiseAddress()
        {
            if (_hubUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || _hubUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return "127.0.0.1";
            }

            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint)
                    return endPoint.Address.ToString();
            }
            catch
            {
            }

            return "127.0.0.1";
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private async Task DisposeHubAsync()
        {
            if (_hub == null)
                return;

            var hub = _hub;
            _hub = null;
            await Task.Run(() => hub.Dispose());
        }

        private void SetStatus(string text)
        {
            Status = text;
            StatusChanged?.Invoke(text);
        }

        private void Add(ChatLine line) => LineAdded?.Invoke(line);
    }
}
