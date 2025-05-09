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
                    State = State.Search
                };
                usersNoSearch.Add(user.ID, user);
                return user.ID;

        }

        public bool Connect(int myID)
        {

                ServerUser user = usersNoSearch[myID];
                usersNoSearch.Remove(myID);
                usersSearch.Add(myID, user);


                if (queue.Count > 0)
                {
                    ServerUser user1 =  queue.Dequeue();
                user.Callback.GetIP(user.ID, user1.ID);
                if (user1 != null)
                {
                    user1.Callback.GetIP(user1.ID, user.ID);
                }
                return true;
                }

                queue.Enqueue(user);
                user.Callback.GetIP(user.ID, -1); // Safe call
                return false;

        }
        public void Disconnect(int identificator, int indetificator1)
        {
            var user = usersFound[identificator];//поиск usera
            var user1 = usersFound[indetificator1];
            if (user != null)
            {
                SendMessageExit(": " + "покинул чат", identificator, indetificator1);
                usersFound.Remove(identificator);
                usersNoSearch.Add(user.ID, user);
                if(user1 != null)
                {
                    usersFound.Remove(indetificator1);
                    usersNoSearch.Add(user1.ID, user1);
                }
            }
        }

        public void RemoveUserSearch(int identificator)
        {
            if (!usersSearch.Remove(identificator))
            {
                throw new Exception("Нельзя удалить не существующего пользователя");
            }
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
