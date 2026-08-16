using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading;

namespace WCF_Chat
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Reentrant)]
    [KnownType(typeof(ECDiffieHellmanPublicKey))]
    public class ServiceChat : IServiceChat
    {
        private readonly object _lock = new object();
        Dictionary<int, ServerUser> usersSearch;
        Dictionary<int, ServerUser> usersFound;
        Dictionary<int, ServerUser> usersNoSearch;
        Queue<ServerUser> queue;
        private Dictionary<int, UdpClient> _voiceRelays = new Dictionary<int, UdpClient>();
        int nextId;

        public ServiceChat() 
        {
            usersSearch = new Dictionary<int, ServerUser>();
            usersFound = new Dictionary<int, ServerUser>();
            usersNoSearch = new Dictionary<int, ServerUser>();
            queue = new Queue<ServerUser>();
            nextId = 0;
        }
        public int CreateUser(byte[] publicKey, byte[] signPublicKey)
        {
            ServerUser user = new ServerUser()
            {
                ID = nextId++,
                Callback = OperationContext.Current.GetCallbackChannel<IServerChatCallback>(),
                PublicKey = publicKey,
                SignPublicKey = signPublicKey,
                ID1 = -1
            };
            usersNoSearch.Add(user.ID, user);
            return user.ID;
        }

        public void Connect(int myID)
        {
            ServerUser user = usersNoSearch[myID];
            usersNoSearch.Remove(myID);
            usersSearch.Add(myID, user);

            if (queue.Count > 0)
            {
                ServerUser user1 = queue.Dequeue();
                user1.ID1 = user.ID;
                user.ID1 = user1.ID;
                usersSearch.Remove(user.ID);
                usersSearch.Remove(user1.ID);
                usersFound.Add(user.ID, user);
                usersFound.Add(user1.ID, user1);
                user.Callback.GetConnectionAndPublicKey(user1.PublicKey, user1.SignPublicKey);
                user1.Callback.GetConnectionAndPublicKey(user.PublicKey, user.SignPublicKey);
                return;
            }

            queue.Enqueue(user);
            user.Callback.GetConnectionAndPublicKey(null, null);
        }
        public void Disconnect(int identificator)
        {
            var user = usersFound[identificator];//поиск usera
            if (user != null)
            {
                if (user.ID1 != -1)
                {
                    SendMessageExit(": " + "покинул чат", user.ID1);
                    usersFound[user.ID1].ID1 = -1;
                    user.ID1 = -1;
                }
                usersFound.Remove(identificator);
                usersNoSearch.Add(user.ID, user);
            }
        }

        public void RemoveUserSearch(int identificator)
        {
            ServerUser user = usersSearch[identificator];
            int initialCount = queue.Count;

            for (int i = 0; i < initialCount; i++)
            {
                ServerUser current = queue.Dequeue();
                if (current != user)
                {
                    queue.Enqueue(current); // Возвращаем обратно, если не удаляем
                }
            }

            usersSearch.Remove(identificator);
            usersNoSearch.Add(user.ID, user);
        }

        public void SendMessage(byte[] hmac, byte[] message, int identificator)
        {
            string answer = DateTime.Now.ToShortTimeString();
            string answer1 = answer + ": " + "Я" + ":  ";
            var user = usersFound[identificator];
            user.Callback.MessageCallBack(hmac, answer1, message);
            string answer2 = answer + ": " + "Собеседник" + ":  ";
            var user1 = usersFound[user.ID1];
            user1.Callback.MessageCallBack(hmac, answer2, message);
        }

        public void SendSignedMessage(byte[] hmac, byte[] message, byte[] signature, int identificator)
        {
            string answer = DateTime.Now.ToShortTimeString();
            var user = usersFound[identificator];

            string answer2 = answer + ": " + "Собеседник" + ":  ";
            var user1 = usersFound[user.ID1];
            user1.Callback.MessageCallBackSigned(hmac, answer2, message, signature);
        }

        public void SendMessageExit(string message, int identificator1)
        {
            string answer = DateTime.Now.ToShortTimeString();
            answer += ": " + "Собеседник";
            answer += message;
            var user1 = usersFound[identificator1];
            user1.Callback.MessageNotification(answer);
            user1.Callback.LeftChat();
        }

        public void SendHashProtocol(byte[] key, byte[] hmac, int id)
        {
            ServerUser user = usersFound[id];
            usersFound[user.ID1].Callback.CompareHMAC(key, hmac);
        }

        public void SendHashEquals(bool state, int id)
        {
            ServerUser user = usersFound[id];
            usersFound[user.ID].Callback.GetConnectionProtocol(state);
            usersFound[user.ID1].Callback.GetConnectionProtocol(state);
        }

        /// <summary>
        /// Отправить запрос на звонок
        /// </summary>
        public void SendCallRequest(int userId, string callerIP, int callerPort)
        {
            lock (_lock)
            {
                ServerUser caller = null;

                if (usersFound.ContainsKey(userId))
                    caller = usersFound[userId];
                else if (usersSearch.ContainsKey(userId))
                    caller = usersSearch[userId];
                else if (usersNoSearch.ContainsKey(userId))
                    caller = usersNoSearch[userId];

                if (caller == null || caller.ID1 == -1) return;

                ServerUser callee = null;

                if (usersFound.ContainsKey(caller.ID1))
                    callee = usersFound[caller.ID1];
                else if (usersSearch.ContainsKey(caller.ID1))
                    callee = usersSearch[caller.ID1];
                else if (usersNoSearch.ContainsKey(caller.ID1))
                    callee = usersNoSearch[caller.ID1];

                if (callee != null)
                {
                    callee.Callback?.IncomingCall(userId, callerIP, callerPort);
                }
            }
        }

        /// <summary>
        /// Ответ на звонок
        /// </summary>
        public void SendCallAnswer(int userId, bool accept, string answererIP, int answererPort)
        {
            lock (_lock)
            {
                ServerUser answerer = null;

                if (usersFound.ContainsKey(userId))
                    answerer = usersFound[userId];
                else if (usersSearch.ContainsKey(userId))
                    answerer = usersSearch[userId];
                else if (usersNoSearch.ContainsKey(userId))
                    answerer = usersNoSearch[userId];

                if (answerer == null || answerer.ID1 == -1) return;

                ServerUser caller = null;

                if (usersFound.ContainsKey(answerer.ID1))
                    caller = usersFound[answerer.ID1];
                else if (usersSearch.ContainsKey(answerer.ID1))
                    caller = usersSearch[answerer.ID1];
                else if (usersNoSearch.ContainsKey(answerer.ID1))
                    caller = usersNoSearch[answerer.ID1];

                if (caller != null)
                {
                    caller.Callback?.CallAnswered(userId, accept, answererIP, answererPort);

                    if (accept)
                    {
                        answerer.InCall = true;
                        caller.InCall = true;
                    }
                }
            }
        }

        /// <summary>
        /// Завершение звонка
        /// </summary>
        public void SendCallEnd(int userId)
        {
            lock (_lock)
            {
                ServerUser user = null;

                if (usersFound.ContainsKey(userId))
                    user = usersFound[userId];
                else if (usersSearch.ContainsKey(userId))
                    user = usersSearch[userId];
                else if (usersNoSearch.ContainsKey(userId))
                    user = usersNoSearch[userId];

                if (user == null || user.ID1 == -1) return;

                ServerUser peer = null;

                if (usersFound.ContainsKey(user.ID1))
                    peer = usersFound[user.ID1];
                else if (usersSearch.ContainsKey(user.ID1))
                    peer = usersSearch[user.ID1];
                else if (usersNoSearch.ContainsKey(user.ID1))
                    peer = usersNoSearch[user.ID1];

                if (peer != null)
                {
                    peer.Callback?.CallEnded(userId);

                    user.InCall = false;
                    peer.InCall = false;
                }
            }
        }
        public void RelayVoice(int fromUserId, byte[] voiceData)
        {
            lock (_lock)
            {
                ServerUser user = null;

                // Ищем пользователя во всех словарях
                if (usersFound.ContainsKey(fromUserId))
                    user = usersFound[fromUserId];
                else if (usersSearch.ContainsKey(fromUserId))
                    user = usersSearch[fromUserId];
                else if (usersNoSearch.ContainsKey(fromUserId))
                    user = usersNoSearch[fromUserId];

                if (user == null || user.ID1 == -1) return;

                ServerUser peer = null;

                // Ищем собеседника
                if (usersFound.ContainsKey(user.ID1))
                    peer = usersFound[user.ID1];
                else if (usersSearch.ContainsKey(user.ID1))
                    peer = usersSearch[user.ID1];
                else if (usersNoSearch.ContainsKey(user.ID1))
                    peer = usersNoSearch[user.ID1];

                if (peer != null)
                {
                    peer.Callback?.ReceiveVoice(fromUserId, voiceData);
                }
            }
        }
        public void SendVoiceKeys(int fromUserId, byte[] sessionKey, byte[] iv)
        {
            lock (_lock)
            {
                ServerUser user = null;

                // Находим отправителя
                if (usersFound.ContainsKey(fromUserId))
                    user = usersFound[fromUserId];
                else if (usersSearch.ContainsKey(fromUserId))
                    user = usersSearch[fromUserId];
                else if (usersNoSearch.ContainsKey(fromUserId))
                    user = usersNoSearch[fromUserId];

                if (user == null || user.ID1 == -1) return;

                ServerUser peer = null;

                // Находим собеседника
                if (usersFound.ContainsKey(user.ID1))
                    peer = usersFound[user.ID1];
                else if (usersSearch.ContainsKey(user.ID1))
                    peer = usersSearch[user.ID1];
                else if (usersNoSearch.ContainsKey(user.ID1))
                    peer = usersNoSearch[user.ID1];

                if (peer != null)
                {
                    // Отправляем ключи собеседнику
                    peer.Callback?.ReceiveVoiceKeys(fromUserId, sessionKey, iv);
                }
            }
        }
    }
}
