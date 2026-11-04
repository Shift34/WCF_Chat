using ChatClient.ProtocolSignal;
using System;

namespace ChatClient
{
    /// <summary>
    /// Шифрование кадров видео: Kuznechik (ГОСТ 34.12-2015) + имитовставка Streebog-256 (ГОСТ 34.11-2012).
    /// </summary>
    internal sealed class GostVideoFrameCrypto : IDisposable
    {
        private readonly KuznechikStreaming _stream;
        private readonly Streebog _streebog = new Streebog(256);
        private readonly byte[] _macKey;

        public GostVideoFrameCrypto(byte[] sessionKey, byte[] iv, byte[] macKey)
        {
            if (sessionKey == null || sessionKey.Length != 32)
                throw new ArgumentException("Ключ Kuznechik должен быть 32 байта.", nameof(sessionKey));
            if (iv == null || iv.Length != 16)
                throw new ArgumentException("IV должен быть 16 байт.", nameof(iv));
            if (macKey == null || macKey.Length != 32)
                throw new ArgumentException("Ключ Streebog должен быть 32 байта.", nameof(macKey));

            _macKey = new byte[32];
            Array.Copy(macKey, _macKey, 32);
            _stream = new KuznechikStreaming(sessionKey);
            _stream.InitSession(iv);
        }

        public void Protect(long sequence, byte[] buffer, int offset, int length)
        {
            _stream.XorPacket(buffer, offset, length, sequence);
        }

        public void Unprotect(long sequence, byte[] buffer, int offset, int length)
        {
            _stream.XorPacket(buffer, offset, length, sequence);
        }

        public byte[] ComputeMac(long sequence, byte[] payload, int offset, int length)
        {
            var input = new byte[8 + length];
            Array.Copy(BitConverter.GetBytes(sequence), 0, input, 0, 8);
            Array.Copy(payload, offset, input, 8, length);

            var macInput = new byte[_macKey.Length + input.Length];
            Array.Copy(_macKey, 0, macInput, 0, _macKey.Length);
            Array.Copy(input, 0, macInput, _macKey.Length, input.Length);

            byte[] hash = _streebog.GetHash(macInput);
            var tag = new byte[8];
            Array.Copy(hash, 0, tag, 0, 8);
            return tag;
        }

        public bool VerifyMac(long sequence, byte[] payload, int offset, int length, byte[] tag)
        {
            if (tag == null || tag.Length != 8)
                return false;

            byte[] expected = ComputeMac(sequence, payload, offset, length);
            for (int i = 0; i < 8; i++)
            {
                if (expected[i] != tag[i])
                    return false;
            }

            return true;
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}
