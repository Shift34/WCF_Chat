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
        List<ServerUser> users = new List<ServerUser>();
        int nextId = 0;

        public (int , int) Connect()
        {
            ServerUser user = new ServerUser()
            {
                ID = nextId,
                OperationContext = OperationContext.Current,
                State = State.Search
            };
            users.Add(user);
            foreach (var item in users)
            {
                if (item.State == State.Search && item.ID != nextId)
                {
                    item.State = State.Found;
                    user.State = State.Found;
                    item.OperationContext.GetCallbackChannel<IServerChatCallback>().FoundByIp(item.ID, user.ID);
                    nextId++;
                    return (user.ID, item.ID);
                }
            }
            nextId++;
            return (-1, -1);
        }

        public void Disconnect(int identificator, int indetificator1)
        {
            var user = users.FirstOrDefault(i => i.ID == identificator); //поиск usera
            if (user != null)
            {
                SendMessageExit(": " + "покинул чат", identificator, indetificator1);
                users.Remove(user);
            }
        }

        public void RemoveUser(int identificator)
        {
            var user = users.FirstOrDefault(i => i.ID == identificator);
            if (user != null)
            {
                users.Remove(user);
            }
        }

        public void SendMessage(byte[] message, int identificator, int identificator1)
        {
            string answer = DateTime.Now.ToShortTimeString();
            string answer1 = answer + ": " + "Я" + ":  ";
            var user = users.FirstOrDefault(i => i.ID == identificator);
            user.OperationContext.GetCallbackChannel<IServerChatCallback>().MessageCallBack(answer1, message);
            string answer2 = answer + ": " + "Собеседник" + ":  ";
            var user1 = users.FirstOrDefault(i => i.ID == identificator1);
            user1.OperationContext.GetCallbackChannel<IServerChatCallback>().MessageCallBack(answer2, message);
        }

        public void SendMessageExit(string message, int identificator, int identificator1)
        {
            string answer = DateTime.Now.ToShortTimeString();
            answer += ": " + "Собеседник";
            answer += message;
            var user1 = users.FirstOrDefault(i => i.ID == identificator1);
            user1.OperationContext.GetCallbackChannel<IServerChatCallback>().MessageCallBack(answer, null);
            user1.OperationContext.GetCallbackChannel<IServerChatCallback>().LeftChat();
        }
    }
}
