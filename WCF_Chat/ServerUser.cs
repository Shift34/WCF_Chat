﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace WCF_Chat
{
    public class ServerUser
    {
        public int ID { get; set; }
        public int ID1 { get; set; }

        public byte[] PublicKey { get; set; }

        public IServerChatCallback Callback { get; set; }

    }
}
