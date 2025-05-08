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

        public OperationContext OperationContext { get; set; }

        public ServerUser(int id)
        {
            ID = id;
            OperationContext = OperationContext.Current;
            State = State.Search;
        }
    }
    public enum State
    {
        Search,
        Found,
        NoSearch
    }
}
