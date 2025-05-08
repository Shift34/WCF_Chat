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
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
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
        public void CreateUser()
        {
            lock (_lock)
            {
                ServerUser user = new ServerUser(nextId++);
                usersNoSearch.Add(user.ID, user);
            }
        }

        public bool Connect(int myID)
        {
            lock (_lock)
            {
                ServerUser user = usersNoSearch[myID];
                usersNoSearch.Remove(myID);
                usersSearch.Add(myID, user);

                if (queue.Count > 0)
                {
                    ServerUser user1 =  queue.Dequeue();
                    user.OperationContext.GetCallbackChannel<IServerChatCallback>().GetIP(user1.ID, user.ID);
                    user1.OperationContext.GetCallbackChannel<IServerChatCallback>().GetIP(user.ID, user1.ID);
                    return true;
                }

                user.OperationContext.GetCallbackChannel<IServerChatCallback>().GetIP(user.ID, -1);
                return false;
            }
        }
        public void Disconnect(int identificator, int indetificator1)
        {

            var user = usersFound[identificator]; //поиск usera
            if (user != null)
            {
                SendMessageExit(": " + "покинул чат", identificator, indetificator1);
                usersFound.Remove(identificator);
                usersNoSearch.Add(user.ID, user);
            }
        }

        public void RemoveUser(int identificator)
        {
            if (!usersFound.Remove(identificator) && !usersNoSearch.Remove(identificator) && !usersSearch.Remove(identificator))
            {
                throw new Exception("Нельзя удалить не существующего пользователя");
            }
        }

        public void SendMessage(byte[] message, int identificator, int identificator1)
        {
            string answer = DateTime.Now.ToShortTimeString();
            string answer1 = answer + ": " + "Я" + ":  ";
            var user = usersFound[identificator];
            user.OperationContext.GetCallbackChannel<IServerChatCallback>().MessageCallBack(answer1, message);
            string answer2 = answer + ": " + "Собеседник" + ":  ";
            var user1 = usersFound[identificator1];
            user1.OperationContext.GetCallbackChannel<IServerChatCallback>().MessageCallBack(answer2, message);
        }

        public void SendMessageExit(string message, int identificator, int identificator1)
        {
            string answer = DateTime.Now.ToShortTimeString();
            answer += ": " + "Собеседник";
            answer += message;
            var user1 = usersFound[identificator1];
            user1.OperationContext.GetCallbackChannel<IServerChatCallback>().MessageCallBack(answer, null);
            user1.OperationContext.GetCallbackChannel<IServerChatCallback>().LeftChat();
        }
    }
}
