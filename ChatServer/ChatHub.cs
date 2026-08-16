using Microsoft.AspNetCore.SignalR;

namespace ChatServer
{
    public class ChatHub : Hub
    {
        private readonly ChatSessionService _sessions;

        public ChatHub(ChatSessionService sessions)
        {
            _sessions = sessions;
        }

        public int CreateUser(byte[] publicKey, byte[] signPublicKey)
        {
            return _sessions.CreateUser(Context.ConnectionId, publicKey, signPublicKey);
        }

        public Task Connect(int myId)
        {
            return _sessions.Connect(Context.ConnectionId, myId);
        }

        public Task Disconnect(int identificator)
        {
            return _sessions.Disconnect(Context.ConnectionId, identificator);
        }

        public Task RemoveUserSearch(int identificator)
        {
            return _sessions.RemoveUserSearch(Context.ConnectionId, identificator);
        }

        public Task SendMessage(byte[] hmac, byte[] message, int identificator)
        {
            return _sessions.SendMessage(identificator, hmac, message);
        }

        public Task SendSignedMessage(byte[] hmac, byte[] message, byte[] signature, int identificator)
        {
            return _sessions.SendSignedMessage(identificator, hmac, message, signature);
        }

        public Task SendHashProtocol(byte[] key, byte[] hmac, int id)
        {
            return _sessions.SendHashProtocol(id, key, hmac);
        }

        public Task SendHashEquals(bool state, int id)
        {
            return _sessions.SendHashEquals(id, state);
        }

        public Task SendCallRequest(int userId, string callerIP, int callerPort)
        {
            return _sessions.SendCallRequest(userId, callerIP, callerPort);
        }

        public Task SendCallAnswer(int userId, bool accept, string answererIP, int answererPort)
        {
            return _sessions.SendCallAnswer(userId, accept, answererIP, answererPort);
        }

        public Task SendCallEnd(int userId)
        {
            return _sessions.SendCallEnd(userId);
        }

        public Task RelayVoice(int fromUserId, byte[] voiceData)
        {
            return _sessions.RelayVoice(fromUserId, voiceData);
        }

        public Task SendVoiceKeys(int fromUserId, byte[] sessionKey, byte[] iv)
        {
            return _sessions.SendVoiceKeys(fromUserId, sessionKey, iv);
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await _sessions.HandleConnectionLost(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
