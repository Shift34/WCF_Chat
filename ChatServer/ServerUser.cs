namespace ChatServer
{
    public class ServerUser
    {
        public int ID { get; set; }
        public int ID1 { get; set; } = -1;
        public byte[]? PublicKey { get; set; }
        public byte[]? SignPublicKey { get; set; }
        public string ConnectionId { get; set; } = string.Empty;
        public bool InCall { get; set; }
        public string DirectHost { get; set; } = string.Empty;
        public int DirectPort { get; set; }
        public bool DirectLinkReady { get; set; }
        public bool DirectP2PActive { get; set; }
    }
}
