using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading;
using System.Security.Cryptography;

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
        int nextId;

        public ServiceChat() 
        {
            usersSearch = new Dictionary<int, ServerUser>();
            usersFound = new Dictionary<int, ServerUser>();
            usersNoSearch = new Dictionary<int, ServerUser>();
            queue = new Queue<ServerUser>();
            nextId = 0;
        }
        public int CreateUser(byte[] publicKey)
        {
            ServerUser user = new ServerUser()
            {
                ID = nextId++,
                Callback = OperationContext.Current.GetCallbackChannel<IServerChatCallback>(),
                PublicKey = publicKey,
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
                user.Callback.GetConnectionAndPublicKey(user1.PublicKey);
                user1.Callback.GetConnectionAndPublicKey(user.PublicKey);
                return;
            }

            queue.Enqueue(user);
            user.Callback.GetConnectionAndPublicKey(null);
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
    }
}
