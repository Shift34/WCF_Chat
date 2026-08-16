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
    }
}
