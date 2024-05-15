using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.Text;

namespace WCF_Chat
{
    [ServiceContract(CallbackContract = typeof(IServerChatCallback))]
    public interface IServiceChat
    {
        [OperationContract]
        (int ID , int ID1) Connect();

        [OperationContract]
        void Disconnect(int identificator, int indetificator1);
        [OperationContract]
        void RemoveUser(int identificator);

        [OperationContract(IsOneWay = true)]
        void SendMessage(byte[] bytes, int identificator, int identificator1);
        [OperationContract(IsOneWay = true)]
        void SendMessageExit(string message, int identificator, int identificator1);
    }

    public interface IServerChatCallback
    {
        [OperationContract(IsOneWay = true)]
        void MessageCallBack(string message, byte[] bytes);
        [OperationContract(IsOneWay = true)]
        void FoundByIp(int ID,int ID1);
        [OperationContract(IsOneWay = true)]
        void LeftChat();
    }
}
