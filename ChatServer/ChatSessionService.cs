using Microsoft.AspNetCore.SignalR;

namespace ChatServer
{
    public class ChatSessionService
    {
        private readonly object _lock = new object();
        private readonly IHubContext<ChatHub> _hub;
        private readonly Dictionary<int, ServerUser> _usersSearch = new Dictionary<int, ServerUser>();
        private readonly Dictionary<int, ServerUser> _usersFound = new Dictionary<int, ServerUser>();
        private readonly Dictionary<int, ServerUser> _usersNoSearch = new Dictionary<int, ServerUser>();
        private readonly Queue<ServerUser> _queue = new Queue<ServerUser>();
        private int _nextId;

        public ChatSessionService(IHubContext<ChatHub> hub)
        {
            _hub = hub;
        }

        public int CreateUser(string connectionId, byte[] publicKey, byte[] signPublicKey)
        {
            lock (_lock)
            {
                var user = new ServerUser
                {
                    ID = _nextId++,
                    ConnectionId = connectionId,
                    PublicKey = publicKey,
                    SignPublicKey = signPublicKey,
                    ID1 = -1
                };
                _usersNoSearch.Add(user.ID, user);
                return user.ID;
            }
        }

        public async Task Connect(string connectionId, int myId)
        {
            ServerUser user;
            ServerUser? peer = null;

            lock (_lock)
            {
                if (!_usersNoSearch.TryGetValue(myId, out user!))
                    return;

                BindConnection(user, connectionId);
                _usersNoSearch.Remove(myId);
                _usersSearch.Add(myId, user);

                if (_queue.Count > 0)
                {
                    peer = _queue.Dequeue();
                    peer.ID1 = user.ID;
                    user.ID1 = peer.ID;
                    _usersSearch.Remove(user.ID);
                    _usersSearch.Remove(peer.ID);
                    _usersFound.Add(user.ID, user);
                    _usersFound.Add(peer.ID, peer);
                }
                else
                {
                    _queue.Enqueue(user);
                }
            }

            if (peer != null)
            {
                await Send(user, "GetConnectionAndPublicKey", peer.PublicKey, peer.SignPublicKey);
                await Send(peer, "GetConnectionAndPublicKey", user.PublicKey, user.SignPublicKey);
                return;
            }

            await Send(user, "GetConnectionAndPublicKey", null, null);
        }

        public async Task Disconnect(string connectionId, int identificator)
        {
            ServerUser? peer = null;
            ServerUser? user;

            lock (_lock)
            {
                if (!_usersFound.TryGetValue(identificator, out user))
                    return;

                BindConnection(user, connectionId);

                if (user.ID1 != -1 && _usersFound.TryGetValue(user.ID1, out peer))
                {
                    peer.ID1 = -1;
                    user.ID1 = -1;
                }

                _usersFound.Remove(identificator);
                user.InCall = false;
                _usersNoSearch[user.ID] = user;
            }

            if (peer != null)
            {
                string answer = DateTime.Now.ToShortTimeString() + ": Собеседник покинул чат";
                await Send(peer, "MessageNotification", answer);
                await Send(peer, "LeftChat");
            }
        }

        public Task RemoveUserSearch(string connectionId, int identificator)
        {
            lock (_lock)
            {
                if (!_usersSearch.TryGetValue(identificator, out var user))
                    return Task.CompletedTask;

                BindConnection(user, connectionId);

                int initialCount = _queue.Count;
                for (int i = 0; i < initialCount; i++)
                {
                    ServerUser current = _queue.Dequeue();
                    if (current != user)
                        _queue.Enqueue(current);
                }

                _usersSearch.Remove(identificator);
                _usersNoSearch[user.ID] = user;
            }

            return Task.CompletedTask;
        }

        public async Task SendMessage(int identificator, byte[] hmac, byte[] message)
        {
            ServerUser? user;
            ServerUser? peer;
            string time = DateTime.Now.ToShortTimeString();

            lock (_lock)
            {
                if (!_usersFound.TryGetValue(identificator, out user) || user.ID1 == -1)
                    return;
                _usersFound.TryGetValue(user.ID1, out peer);
            }

            if (user == null || peer == null)
                return;

            await Send(user, "MessageCallBack", hmac, time + ": Я:  ", message);
            await Send(peer, "MessageCallBack", hmac, time + ": Собеседник:  ", message);
        }

        public async Task SendSignedMessage(int identificator, byte[] hmac, byte[] message, byte[] signature)
        {
            ServerUser? peer;
            string time = DateTime.Now.ToShortTimeString();

            lock (_lock)
            {
                if (!_usersFound.TryGetValue(identificator, out var user) || user.ID1 == -1)
                    return;
                _usersFound.TryGetValue(user.ID1, out peer);
            }

            if (peer == null)
                return;

            await Send(peer, "MessageCallBackSigned", hmac, time + ": Собеседник:  ", message, signature);
        }

        public async Task SendHashProtocol(int id, byte[] key, byte[] hmac)
        {
            ServerUser? peer;
            lock (_lock)
            {
                if (!_usersFound.TryGetValue(id, out var user) || user.ID1 == -1)
                    return;
                _usersFound.TryGetValue(user.ID1, out peer);
            }

            if (peer == null)
                return;

            await Send(peer, "CompareHMAC", key, hmac);
        }

        public async Task SendHashEquals(int id, bool state)
        {
            ServerUser? user;
            ServerUser? peer;
            lock (_lock)
            {
                if (!_usersFound.TryGetValue(id, out user) || user.ID1 == -1)
                    return;
                _usersFound.TryGetValue(user.ID1, out peer);
            }

            if (user == null || peer == null)
                return;

            await Send(user, "GetConnectionProtocol", state);
            await Send(peer, "GetConnectionProtocol", state);
        }

        public async Task SendCallRequest(int userId, string callerIP, int callerPort)
        {
            ServerUser? callee;
            lock (_lock)
            {
                var caller = FindUser(userId);
                if (caller == null || caller.ID1 == -1)
                    return;
                callee = FindUser(caller.ID1);
            }

            if (callee != null)
                await Send(callee, "IncomingCall", userId, callerIP, callerPort);
        }

        public async Task SendCallAnswer(int userId, bool accept, string answererIP, int answererPort)
        {
            ServerUser? caller;
            lock (_lock)
            {
                var answerer = FindUser(userId);
                if (answerer == null || answerer.ID1 == -1)
                    return;

                caller = FindUser(answerer.ID1);
                if (accept && caller != null)
                {
                    answerer.InCall = true;
                    caller.InCall = true;
                }
            }

            if (caller != null)
                await Send(caller, "CallAnswered", userId, accept, answererIP, answererPort);
        }

        public async Task SendCallEnd(int userId)
        {
            ServerUser? peer;
            lock (_lock)
            {
                var user = FindUser(userId);
                if (user == null || user.ID1 == -1)
                    return;

                peer = FindUser(user.ID1);
                user.InCall = false;
                if (peer != null)
                    peer.InCall = false;
            }

            if (peer != null)
                await Send(peer, "CallEnded", userId);
        }

        public async Task RelayVoice(int fromUserId, byte[] voiceData)
        {
            ServerUser? peer;
            lock (_lock)
            {
                var user = FindUser(fromUserId);
                if (user == null || user.ID1 == -1)
                    return;
                peer = FindUser(user.ID1);
            }

            if (peer != null)
                await Send(peer, "ReceiveVoice", fromUserId, voiceData);
        }

        public async Task SendVoiceKeys(int fromUserId, byte[] sessionKey, byte[] iv)
        {
            ServerUser? peer;
            lock (_lock)
            {
                var user = FindUser(fromUserId);
                if (user == null || user.ID1 == -1)
                    return;
                peer = FindUser(user.ID1);
            }

            if (peer != null)
                await Send(peer, "ReceiveVoiceKeys", fromUserId, sessionKey, iv);
        }

        public async Task HandleConnectionLost(string connectionId)
        {
            int? userId = null;
            bool wasFound = false;
            bool wasSearch = false;

            lock (_lock)
            {
                var user = FindByConnection(connectionId);
                if (user == null)
                    return;

                userId = user.ID;
                wasFound = _usersFound.ContainsKey(user.ID);
                wasSearch = _usersSearch.ContainsKey(user.ID);
            }

            if (userId == null)
                return;

            if (wasFound)
                await Disconnect(connectionId, userId.Value);
            else if (wasSearch)
                await RemoveUserSearch(connectionId, userId.Value);
            else
            {
                lock (_lock)
                {
                    _usersNoSearch.Remove(userId.Value);
                }
            }
        }

        private ServerUser? FindUser(int id)
        {
            if (_usersFound.TryGetValue(id, out var user))
                return user;
            if (_usersSearch.TryGetValue(id, out user))
                return user;
            if (_usersNoSearch.TryGetValue(id, out user))
                return user;
            return null;
        }

        private ServerUser? FindByConnection(string connectionId)
        {
            foreach (var user in _usersFound.Values)
            {
                if (user.ConnectionId == connectionId)
                    return user;
            }
            foreach (var user in _usersSearch.Values)
            {
                if (user.ConnectionId == connectionId)
                    return user;
            }
            foreach (var user in _usersNoSearch.Values)
            {
                if (user.ConnectionId == connectionId)
                    return user;
            }
            return null;
        }

        private static void BindConnection(ServerUser user, string connectionId)
        {
            if (!string.IsNullOrEmpty(connectionId))
                user.ConnectionId = connectionId;
        }

        private Task Send(ServerUser user, string method, params object?[] args)
        {
            if (string.IsNullOrEmpty(user.ConnectionId))
                return Task.CompletedTask;

            return _hub.Clients.Client(user.ConnectionId).SendCoreAsync(method, args);
        }
    }
}
