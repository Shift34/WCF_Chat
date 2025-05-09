using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;

namespace WCF_Chat
{
    public class ServerUser
    {
        public int ID { get; set; }

        public State State {get;set;}

        public IServerChatCallback Callback { get; set; }

    }
    public enum State
    {
        Search,
        Found,
        NoSearch
    }
}
