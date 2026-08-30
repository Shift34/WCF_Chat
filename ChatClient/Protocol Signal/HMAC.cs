using System;

namespace ChatClient.ProtocolSignal
{
    internal class HMAC
    {
        private readonly Streebog _streebog;
        private readonly byte[] _key;
        private readonly int _blockSize = 64; // Размер блока для Streebog (64 байта)

        public HMAC(byte[] key, int hashSize = 512)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            if (hashSize != 256 && hashSize != 512)
                throw new ArgumentException("Hash size must be 256 or 512");

            _streebog = new Streebog(hashSize);

            // RFC 2104: ключ длиннее блока хешируется, короче — дополняется нулями.
            byte[] prepared = key.Length > _blockSize ? _streebog.GetHash(key) : key;
            if (prepared.Length == _blockSize)
            {
                _key = (byte[])prepared.Clone();
            }
            else
            {
                _key = new byte[_blockSize];
                Array.Copy(prepared, _key, prepared.Length);
            }
        }

        public byte[] ComputeHash(byte[] message)
        {
            // Шаг 1: Создаем внутренний ключ (ipad)
            var iKeyPad = Xor(_key, 0x36);

            // Шаг 2: Добавляем сообщение
            var innerData = new byte[iKeyPad.Length + message.Length];
            Array.Copy(iKeyPad, 0, innerData, 0, iKeyPad.Length);
            Array.Copy(message, 0, innerData, iKeyPad.Length, message.Length);

            // Шаг 3: Хешируем внутренние данные
            var innerHash = _streebog.GetHash(innerData);

            // Шаг 4: Создаем внешний ключ (opad)
            var oKeyPad = Xor(_key, 0x5C);

            // Шаг 5: Добавляем внутренний хеш
            var outerData = new byte[oKeyPad.Length + innerHash.Length];
            Array.Copy(oKeyPad, 0, outerData, 0, oKeyPad.Length);
            Array.Copy(innerHash, 0, outerData, oKeyPad.Length, innerHash.Length);

            // Шаг 6: Хешируем внешние данные
            return _streebog.GetHash(outerData);
        }

        private byte[] Xor(byte[] data, byte value)
        {
            var result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = (byte)(data[i] ^ value);
            return result;
        }
    }
}
