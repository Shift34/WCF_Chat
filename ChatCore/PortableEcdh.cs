using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;

namespace ChatCore
{
    /// <summary>
    /// ECDH on the same explicit curve as the Windows client.
    /// Public keys are EccFullPublicBlob so WPF can import them.
    /// Shared secret is SHA-256(X), matching ECDiffieHellman.DeriveKeyMaterial.
    /// </summary>
    public sealed class PortableEcdh
    {
        private static readonly byte[] Prime = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFDC7");
        private static readonly byte[] A = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFDC4");
        private static readonly byte[] B = Convert.FromHexString("E8C2505DEDFC86DDC1BD0B2B6667F1DA34B82574761CB0E879BD081CFD0B6265EE3CB090F30D27614CB4574010DA90DD862EF9D4EBEE4761503190785A71C760");
        private static readonly byte[] Order = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF27E69532F48D89116FF22B8D4E0560609B4B38ABFAD2B85DCACDB1411F10B275");
        private static readonly byte[] Gx = Convert.FromHexString("00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000003");
        private static readonly byte[] Gy = Convert.FromHexString("7503CFE87A836AE3A61B8816E25450E6CE5E1C93ACF1ABC1778064FDCBEFA921DF1626BE4FD036E93D75E6A50E3A41E98028FE5FC235F5B889A589CB5215F2A4");

        private readonly ECPrivateKeyParameters _privateKey;
        private readonly ECDomainParameters _domain;
        private readonly FpCurve _curve;

        public PortableEcdh()
        {
            _curve = new FpCurve(U(Prime), U(A), U(B), U(Order), BigInteger.One);
            var g = _curve.CreatePoint(U(Gx), U(Gy));
            _domain = new ECDomainParameters(_curve, g, U(Order), BigInteger.One);
            var gen = new ECKeyPairGenerator();
            gen.Init(new ECKeyGenerationParameters(_domain, new SecureRandom()));
            var pair = gen.GenerateKeyPair();
            _privateKey = (ECPrivateKeyParameters)pair.Private;
            var pub = (ECPublicKeyParameters)pair.Public;
            PublicBlob = EccFullPublicBlob.Create(Pad(pub.Q.AffineXCoord.ToBigInteger()), Pad(pub.Q.AffineYCoord.ToBigInteger()));
        }

        public byte[] PublicBlob { get; }

        public byte[] DeriveKeyMaterial(byte[] peerBlob)
        {
            var (qx, qy) = EccFullPublicBlob.ReadPoint(peerBlob);
            var agree = new ECDHBasicAgreement();
            agree.Init(_privateKey);
            var point = _curve.CreatePoint(U(qx), U(qy));
            byte[] x = agree.CalculateAgreement(new ECPublicKeyParameters(point, _domain)).ToByteArrayUnsigned();
            return SHA256.HashData(Pad(x));
        }

        private static BigInteger U(byte[] bytes) => new BigInteger(1, bytes);

        private static byte[] Pad(BigInteger value)
        {
            return Pad(value.ToByteArrayUnsigned());
        }

        private static byte[] Pad(byte[] value)
        {
            if (value.Length == 64)
                return value;
            if (value.Length > 64)
                throw new InvalidOperationException("Координата длиннее поля кривой.");

            var padded = new byte[64];
            Buffer.BlockCopy(value, 0, padded, 64 - value.Length, value.Length);
            return padded;
        }
    }

    internal static class EccFullPublicBlob
    {
        public static byte[] Create(byte[] qx, byte[] qy)
        {
            var blob = new byte[545];
            WriteInt(blob, 0, 0x504B4345);
            WriteInt(blob, 4, 1);
            WriteInt(blob, 8, 1);
            WriteInt(blob, 12, 0);
            WriteInt(blob, 16, 64);
            WriteInt(blob, 20, 64);
            WriteInt(blob, 24, 1);
            WriteInt(blob, 28, 0);
            Buffer.BlockCopy(PortableField.Prime, 0, blob, 32, 64);
            Buffer.BlockCopy(PortableField.A, 0, blob, 96, 64);
            Buffer.BlockCopy(PortableField.B, 0, blob, 160, 64);
            Buffer.BlockCopy(PortableField.Gx, 0, blob, 224, 64);
            Buffer.BlockCopy(PortableField.Gy, 0, blob, 288, 64);
            Buffer.BlockCopy(PortableField.Order, 0, blob, 352, 64);
            blob[416] = 1;
            Buffer.BlockCopy(qx, 0, blob, 417, 64);
            Buffer.BlockCopy(qy, 0, blob, 481, 64);
            return blob;
        }

        public static (byte[] X, byte[] Y) ReadPoint(byte[] blob)
        {
            if (blob == null || blob.Length < 545)
                throw new ArgumentException("Публичный ключ ECDH не в формате EccFullPublicBlob.", nameof(blob));

            var x = new byte[64];
            var y = new byte[64];
            Buffer.BlockCopy(blob, 417, x, 0, 64);
            Buffer.BlockCopy(blob, 481, y, 0, 64);
            return (x, y);
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }
    }

    internal static class PortableField
    {
        public static readonly byte[] Prime = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFDC7");
        public static readonly byte[] A = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFDC4");
        public static readonly byte[] B = Convert.FromHexString("E8C2505DEDFC86DDC1BD0B2B6667F1DA34B82574761CB0E879BD081CFD0B6265EE3CB090F30D27614CB4574010DA90DD862EF9D4EBEE4761503190785A71C760");
        public static readonly byte[] Order = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF27E69532F48D89116FF22B8D4E0560609B4B38ABFAD2B85DCACDB1411F10B275");
        public static readonly byte[] Gx = Convert.FromHexString("00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000003");
        public static readonly byte[] Gy = Convert.FromHexString("7503CFE87A836AE3A61B8816E25450E6CE5E1C93ACF1ABC1778064FDCBEFA921DF1626BE4FD036E93D75E6A50E3A41E98028FE5FC235F5B889A589CB5215F2A4");
    }
}
