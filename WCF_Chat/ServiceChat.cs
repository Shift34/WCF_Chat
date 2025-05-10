using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading;

namespace WCF_Chat
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Reentrant)]
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
        public int CreateUser()
        {
            ServerUser user = new ServerUser()
            {
                ID = nextId++,
                Callback = OperationContext.Current.GetCallbackChannel<IServerChatCallback>(),
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
                usersSearch.Remove(user.ID);
                usersSearch.Remove(user1.ID);
                usersFound.Add(user.ID, user);
                usersFound.Add(user1.ID, user1);
                user.Callback.GetIP(user.ID, user1.ID);
                user1.Callback.GetIP(user1.ID, user.ID);
                return;
            }

            queue.Enqueue(user);
            user.Callback.GetIP(user.ID, -1); // Safe call
        }
        public void Disconnect(int identificator, int identificator1)
        {

            var user = usersFound[identificator];//поиск usera
            if (user != null)
            {
                if (identificator1 != -1)
                {
                    SendMessageExit(": " + "покинул чат", identificator, identificator1);
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

        public void SendMessage(byte[] message, int identificator, int identificator1)
        {
            string answer = DateTime.Now.ToShortTimeString();
            string answer1 = answer + ": " + "Я" + ":  ";
            var user = usersFound[identificator];
            user.Callback.MessageCallBack(answer1, message);
            string answer2 = answer + ": " + "Собеседник" + ":  ";
            var user1 = usersFound[identificator1];
            user1.Callback.MessageCallBack(answer2, message);
        }

        public void SendMessageExit(string message, int identificator, int identificator1)
        {
            string answer = DateTime.Now.ToShortTimeString();
            answer += ": " + "Собеседник";
            answer += message;
            var user1 = usersFound[identificator1];
            user1.Callback.MessageCallBack(answer, null);
            user1.Callback.LeftChat();
        }
    }
}
