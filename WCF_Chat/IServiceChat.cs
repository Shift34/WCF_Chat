using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.ServiceModel;
using System.Text;

namespace WCF_Chat
{
    [ServiceContract(CallbackContract = typeof(IServerChatCallback))]
    public interface IServiceChat
    {
        [OperationContract]
        int CreateUser(byte[] publicKey);

        [OperationContract(IsOneWay = true)]
        void Connect(int myID);

        [OperationContract(IsOneWay = true)]
        void Disconnect(int identificator);
        [OperationContract(IsOneWay = true)]
        void RemoveUserSearch(int identificator);

        [OperationContract(IsOneWay = true)]
        void SendMessage(byte[] hmac, byte[] message, int identificator);
        [OperationContract(IsOneWay = true)]
        void SendMessageExit(string message, int identificator1);

        [OperationContract(IsOneWay = true)]
        void SendHashProtocol(byte[] key, byte[] hmac, int id);

        [OperationContract(IsOneWay = true)]
        void SendHashEquals(bool state, int id);
    }

    public interface IServerChatCallback
    {
        [OperationContract(IsOneWay = true)]
        void MessageCallBack(byte[] hmac, string message, byte[] bytes);
        [OperationContract(IsOneWay = true)]
        void GetConnectionAndPublicKey(byte[] publickey);
        [OperationContract(IsOneWay = true)]
        void LeftChat();

        [OperationContract(IsOneWay = true)]
        void CompareHMAC(byte[] key, byte[] hmac);

        [OperationContract(IsOneWay = true)]
        void GetConnectionProtocol(bool state);

        [OperationContract(IsOneWay = true)]
        void MessageNotification(string message);

    }
}
