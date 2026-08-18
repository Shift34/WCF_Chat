using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChatClient
{
    public sealed class ChatHubClient : IDisposable
    {
        private readonly HubConnection _connection;
        private readonly SynchronizationContext _ui;

        public ChatHubClient(string url)
        {
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            _connection = new HubConnectionBuilder()
                .WithUrl(url, options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                })
                .AddMessagePackProtocol()
                .Build();

            _connection.On<byte[], byte[]>("GetConnectionAndPublicKey",
                (publicKey, signPublicKey) => Post(() => GetConnectionAndPublicKey?.Invoke(publicKey, signPublicKey)));
            _connection.On<bool>("GetConnectionProtocol",
                state => Post(() => GetConnectionProtocol?.Invoke(state)));
            _connection.On<byte[], byte[]>("CompareHMAC",
                (key, hmac) => Post(() => CompareHMAC?.Invoke(key, hmac)));
            _connection.On("LeftChat",
                () => Post(() => LeftChat?.Invoke()));
            _connection.On<string>("MessageNotification",
                message => Post(() => MessageNotification?.Invoke(message)));
            _connection.On<byte[], string, byte[]>("MessageCallBack",
                (hmac, message, bytes) => Post(() => MessageCallBack?.Invoke(hmac, message, bytes)));
            _connection.On<byte[], string, byte[], byte[]>("MessageCallBackSigned",
                (hmac, message, bytes, signature) => Post(() => MessageCallBackSigned?.Invoke(hmac, message, bytes, signature)));
            _connection.On<int, string, int>("IncomingCall",
                (fromUserId, callerIP, callerPort) => Post(() => IncomingCall?.Invoke(fromUserId, callerIP, callerPort)));
            _connection.On<int, bool, string, int>("CallAnswered",
                (fromUserId, accept, answererIP, answererPort) => Post(() => CallAnswered?.Invoke(fromUserId, accept, answererIP, answererPort)));
            _connection.On<int>("CallEnded",
                fromUserId => Post(() => CallEnded?.Invoke(fromUserId)));
            _connection.On<int, byte[]>("ReceiveVoice",
                (fromUserId, voiceData) => ReceiveVoice?.Invoke(fromUserId, voiceData));
            _connection.On<int, byte[], byte[]>("ReceiveVoiceKeys",
                (fromUserId, sessionKey, iv) => Post(() => ReceiveVoiceKeys?.Invoke(fromUserId, sessionKey, iv)));
        }

        public bool IsConnected => _connection.State == HubConnectionState.Connected;

        public event Action<byte[], byte[]> GetConnectionAndPublicKey;
        public event Action<bool> GetConnectionProtocol;
        public event Action<byte[], byte[]> CompareHMAC;
        public event Action LeftChat;
        public event Action<string> MessageNotification;
        public event Action<byte[], string, byte[]> MessageCallBack;
        public event Action<byte[], string, byte[], byte[]> MessageCallBackSigned;
        public event Action<int, string, int> IncomingCall;
        public event Action<int, bool, string, int> CallAnswered;
        public event Action<int> CallEnded;
        public event Action<int, byte[]> ReceiveVoice;
        public event Action<int, byte[], byte[]> ReceiveVoiceKeys;

        public Task StartAsync() => _connection.StartAsync();

        public Task<int> CreateUserAsync(byte[] publicKey, byte[] signPublicKey) =>
            Invoke<int>("CreateUser", publicKey, signPublicKey);

        public Task ConnectAsync(int myId) => Invoke("Connect", myId);

        public Task DisconnectAsync(int identificator) => Invoke("Disconnect", identificator);

        public Task RemoveUserSearchAsync(int identificator) => Invoke("RemoveUserSearch", identificator);

        public Task SendSignedMessageAsync(byte[] hmac, byte[] message, byte[] signature, int identificator) =>
            Invoke("SendSignedMessage", hmac, message, signature, identificator);

        public Task SendHashProtocolAsync(byte[] key, byte[] hmac, int id) =>
            Invoke("SendHashProtocol", key, hmac, id);

        public Task SendHashEqualsAsync(bool state, int id) => Invoke("SendHashEquals", state, id);

        public Task SendCallRequestAsync(int userId, string callerIP, int callerPort) =>
            Invoke("SendCallRequest", userId, callerIP, callerPort);

        public Task SendCallAnswerAsync(int userId, bool accept, string answererIP, int answererPort) =>
            Invoke("SendCallAnswer", userId, accept, answererIP, answererPort);

        public Task SendCallEndAsync(int userId) => Invoke("SendCallEnd", userId);

        public void RelayVoice(int fromUserId, byte[] voiceData)
        {
            _ = _connection.SendAsync("RelayVoice", fromUserId, voiceData);
        }

        public Task SendVoiceKeysAsync(int fromUserId, byte[] sessionKey, byte[] iv) =>
            Invoke("SendVoiceKeys", fromUserId, sessionKey, iv);

        public void Dispose()
        {
            try
            {
                if (_connection.State != HubConnectionState.Disconnected)
                    _connection.StopAsync().GetAwaiter().GetResult();

                _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        private void Post(Action action)
        {
            _ui.Post(_ => action(), null);
        }

        private Task<T> Invoke<T>(string method, params object[] args) =>
            _connection.InvokeCoreAsync<T>(method, args ?? Array.Empty<object>());

        private Task Invoke(string method, params object[] args) =>
            _connection.InvokeCoreAsync(method, args ?? Array.Empty<object>());
    }
}
