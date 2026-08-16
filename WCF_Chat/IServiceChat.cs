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
        int CreateUser(byte[] publicKey, byte[] signPublicKey);

        [OperationContract(IsOneWay = true)]
        void Connect(int myID);

        [OperationContract(IsOneWay = true)]
        void Disconnect(int identificator);
        [OperationContract(IsOneWay = true)]
        void RemoveUserSearch(int identificator);

        [OperationContract(IsOneWay = true)]
        void SendMessage(byte[] hmac, byte[] message, int identificator);

        [OperationContract(IsOneWay = true)]
        void SendSignedMessage(byte[] hmac, byte[] message, byte[] signature, int identificator);

        [OperationContract(IsOneWay = true)]
        void SendMessageExit(string message, int identificator1);

        [OperationContract(IsOneWay = true)]
        void SendHashProtocol(byte[] key, byte[] hmac, int id);

        [OperationContract(IsOneWay = true)]
        void SendHashEquals(bool state, int id);

        [OperationContract(IsOneWay = true)]
        void SendCallRequest(int userId, string callerIP, int callerPort);

        [OperationContract(IsOneWay = true)]
        void SendCallAnswer(int userId, bool accept, string answererIP, int answererPort);

        [OperationContract(IsOneWay = true)]
        void SendCallEnd(int userId);

        [OperationContract(IsOneWay = true)]
        void RelayVoice(int fromUserId, byte[] voiceData);

        [OperationContract(IsOneWay = true)]
        void SendVoiceKeys(int fromUserId, byte[] sessionKey, byte[] iv);
    }

    public interface IServerChatCallback
    {
        [OperationContract(IsOneWay = true)]
        void MessageCallBack(byte[] hmac, string message, byte[] bytes);

        [OperationContract(IsOneWay = true)]
        void MessageCallBackSigned(byte[] hmac, string message, byte[] bytes, byte[] signature);
        [OperationContract(IsOneWay = true)]
        void GetConnectionAndPublicKey(byte[] publickey, byte[] signPublicKey);
        [OperationContract(IsOneWay = true)]
        void LeftChat();

        [OperationContract(IsOneWay = true)]
        void CompareHMAC(byte[] key, byte[] hmac);

        [OperationContract(IsOneWay = true)]
        void GetConnectionProtocol(bool state);

        [OperationContract(IsOneWay = true)]
        void MessageNotification(string message);

        [OperationContract(IsOneWay = true)]
        void IncomingCall(int fromUserId, string callerIP, int callerPort);

        [OperationContract(IsOneWay = true)]
        void CallAnswered(int fromUserId, bool accept, string answererIP, int answererPort);

        [OperationContract(IsOneWay = true)]
        void CallEnded(int fromUserId);

        [OperationContract(IsOneWay = true)]
        void ReceiveVoice(int fromUserId, byte[] voiceData);

        [OperationContract(IsOneWay = true)]
        void ReceiveVoiceKeys(int fromUserId, byte[] sessionKey, byte[] iv);

    }
}
