using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Diagnostics;

namespace ChatClient.ProtocolSignal
{
    class SignalProtocolExample
    {
        // Генерация общего секрета с использованием ECDH
        public static byte[] GenerateSharedSecret(ECDiffieHellmanPublicKey publicKey, ECDiffieHellman privateKey)
        {
            return privateKey.DeriveKeyMaterial(publicKey);
        }



        // Вычисление HMAC
        public static byte[] ComputeHmac(byte[] key, byte[] data)
        {
            HMAC hmac = new HMAC(key);
            return hmac.ComputeHash(data);
        }

        // Генерация ключей с использованием SHA-256
        public static void DeriveKeys(byte[] sharedSecret, out byte[] aesKey, out byte[] hmacKey)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(sharedSecret);

                var streebog512 = new Streebog(512);
                var hash512 = streebog512.GetHash(Encoding.UTF8.GetBytes("Hello, Streebog!"));
 
                aesKey = new byte[32];
                hmacKey = new byte[32];

                // Первые 32 байта хэша для AES
                Array.Copy(hash512, 0, aesKey, 0, 32);

                // Следующие 32 байта хэша для HMAC
                Array.Copy(hash512, 32, hmacKey, 0, 32);
            }
        }

        //static void Main()
        //{
        //    // Первоначальна эллиптическая кривая ECCurve.NamedCurves.nistP256
        //    // Генерация ключей для Alice и Bob

        //    Stopwatch sw = new Stopwatch();
        //    sw.Start();
        //    //using (ECDiffieHellman alice = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        //    //using (ECDiffieHellman bob = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        //    using (ECDiffieHellman alice = ECDiffieHellman.Create(GostCurve.GetGost3410Curve()))
        //    using (ECDiffieHellman bob = ECDiffieHellman.Create(GostCurve.GetGost3410Curve()))
        //    {
        //        sw.Stop();
        //        Console.WriteLine(sw.ElapsedMilliseconds);
        //        // Alice и Bob обмениваются публичными ключами
        //        byte[] alicePublicKey = alice.PublicKey.ToByteArray();
        //        byte[] bobPublicKey = bob.PublicKey.ToByteArray();

        //        // Alice и Bob вычисляют общий секрет
        //        byte[] aliceSharedSecret = GenerateSharedSecret(bob.PublicKey, alice);
        //        byte[] bobSharedSecret = GenerateSharedSecret(alice.PublicKey, bob);

        //        // Проверка, что секреты совпадают
        //        if (!aliceSharedSecret.SequenceEqual(bobSharedSecret))
        //        {
        //            throw new Exception("Секреты не совпадают!");
        //        }

        //        // Генерация ключей с использованием HKDF
        //        byte[] aesKey, hmacKey;
        //        DeriveKeys(aliceSharedSecret, out aesKey, out hmacKey);

        //        var kuznechik = new Kuznechik();

        //        // Шифрование сообщения
        //        string message = "Обычно я просыпаюсь в семь утра. Сначала иду умываться, чистить зубы и принимать душ.";
        //        byte[] encryptedMessage = kuznechik.KuzEncript(Encoding.UTF8.GetBytes(message), aesKey);

        //        // Вычисление HMAC для зашифрованного сообщения
        //        byte[] hmac = ComputeHmac(hmacKey, encryptedMessage);

        //        // Дешифрование сообщения
        //        byte[] decryptedMessage = kuznechik.KuzDecript(encryptedMessage, aesKey);
        //        string decryptedText = Encoding.UTF8.GetString(decryptedMessage);

        //        Console.WriteLine("Оригинал: {0}", message);
        //        Console.WriteLine("Расшифровано: {0}", decryptedText);
        //    }
        //}
    }
}
