using System;
using System.ServiceModel;

namespace ChatHost
{
    public class ChatHost
    {
        static void Main(string[] args)
        {
            using (var host = new ServiceHost(typeof(WCF_Chat_Anonymous.ServiceChat)))
            {
                host.Open();
                Console.WriteLine("Host has been started");
                Console.ReadLine();
            }
        }
    }
}
