using ChatClient.ProtocolSignal;
using GostCryptography;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.CryptoPro;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ChatClient.Protocol_Signal
{
    public class GostSignature
    {
        private AsymmetricCipherKeyPair _keyPair;
        private ECPublicKeyParameters _peerPublicKey;
        private readonly ECDomainParameters _domain;

        public GostSignature()
        {
            var ecSpec = ECGost3410NamedCurves.GetByName(
                "Tc26-Gost-3410-12-512-paramSetA");

            _domain = new ECDomainParameters(
                ecSpec.Curve,
                ecSpec.G,
                ecSpec.N,
                ecSpec.H);

            var gen = new ECKeyPairGenerator();
            gen.Init(new ECKeyGenerationParameters(_domain, new SecureRandom()));

            _keyPair = gen.GenerateKeyPair();
        }

        /// <summary>
        /// Публичный ключ (байты точки)
        /// </summary>
        public byte[] GetPublicKey()
        {
            var pub = (ECPublicKeyParameters)_keyPair.Public;
            return pub.Q.GetEncoded(false);
        }

        /// <summary>
        /// Установить публичный ключ собеседника
        /// </summary>
        public void SetPeerKey(byte[] publicKey)
        {
            var ecSpec = ECGost3410NamedCurves.GetByName(
                "Tc26-Gost-3410-12-512-paramSetA");

            var q = ecSpec.Curve.DecodePoint(publicKey);

            _peerPublicKey = new ECPublicKeyParameters(q, _domain);
        }

        /// <summary>
        /// Подписать сообщение
        /// </summary>
        public byte[] Sign(byte[] message)
        {
            var signer = SignerUtilities.GetSigner("ECGOST3410-2012-512");

            signer.Init(true, _keyPair.Private);
            signer.BlockUpdate(message, 0, message.Length);

            return signer.GenerateSignature();
        }

        /// <summary>
        /// Проверить подпись
        /// </summary>
        public bool Verify(byte[] message, byte[] signature)
        {
            if (_peerPublicKey == null)
                return false;

            try
            {
                var signer = SignerUtilities.GetSigner("ECGOST3410-2012-512");

                signer.Init(false, _peerPublicKey);
                signer.BlockUpdate(message, 0, message.Length);

                return signer.VerifySignature(signature);
            }
            catch
            {
                return false;
            }
        }
    }
}
