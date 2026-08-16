using Microsoft.AspNetCore.SignalR.Client;
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
                .WithUrl(url)
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
                (fromUserId, voiceData) => Post(() => ReceiveVoice?.Invoke(fromUserId, voiceData)));
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

        public void Start()
        {
            _connection.StartAsync().GetAwaiter().GetResult();
        }

        public int CreateUser(byte[] publicKey, byte[] signPublicKey)
        {
            return Invoke<int>(nameof(CreateUser), publicKey, signPublicKey);
        }

        public void Connect(int myId)
        {
            Invoke(nameof(Connect), myId);
        }

        public void Disconnect(int identificator)
        {
            Invoke(nameof(Disconnect), identificator);
        }

        public void RemoveUserSearch(int identificator)
        {
            Invoke(nameof(RemoveUserSearch), identificator);
        }

        public void SendSignedMessage(byte[] hmac, byte[] message, byte[] signature, int identificator)
        {
            Invoke(nameof(SendSignedMessage), hmac, message, signature, identificator);
        }

        public void SendHashProtocol(byte[] key, byte[] hmac, int id)
        {
            Invoke(nameof(SendHashProtocol), key, hmac, id);
        }

        public void SendHashEquals(bool state, int id)
        {
            Invoke(nameof(SendHashEquals), state, id);
        }

        public void SendCallRequest(int userId, string callerIP, int callerPort)
        {
            Invoke(nameof(SendCallRequest), userId, callerIP, callerPort);
        }

        public void SendCallAnswer(int userId, bool accept, string answererIP, int answererPort)
        {
            Invoke(nameof(SendCallAnswer), userId, accept, answererIP, answererPort);
        }

        public void SendCallEnd(int userId)
        {
            Invoke(nameof(SendCallEnd), userId);
        }

        public void RelayVoice(int fromUserId, byte[] voiceData)
        {
            _ = _connection.SendAsync(nameof(RelayVoice), fromUserId, voiceData);
        }

        public void SendVoiceKeys(int fromUserId, byte[] sessionKey, byte[] iv)
        {
            Invoke(nameof(SendVoiceKeys), fromUserId, sessionKey, iv);
        }

        public void Dispose()
        {
            if (_connection.State != HubConnectionState.Disconnected)
            {
                _connection.StopAsync().GetAwaiter().GetResult();
            }

            _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private void Post(Action action)
        {
            _ui.Post(_ => action(), null);
        }

        private T Invoke<T>(string method, params object[] args)
        {
            return _connection.InvokeCoreAsync<T>(method, args).GetAwaiter().GetResult();
        }

        private void Invoke(string method, params object[] args)
        {
            _connection.InvokeCoreAsync(method, args).GetAwaiter().GetResult();
        }
    }
}
