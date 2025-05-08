using System;
using System.Linq;
using System.Security.Cryptography;

namespace ChatClient.ProtocolSignal
{
    internal static class GostCurve
    { 
        public static ECCurve GetGost3410Curve()
        {
            return new ECCurve
            {
                // Параметры кривой id-tc26-gost-3410-2012-256-paramSetA
                CurveType = ECCurve.ECCurveType.PrimeShortWeierstrass,
                A = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFDC4".HexToByteArray(),
                B = "E8C2505DEDFC86DDC1BD0B2B6667F1DA34B82574761CB0E879BD081CFD0B6265EE3CB090F30D27614CB4574010DA90DD862EF9D4EBEE4761503190785A71C760".HexToByteArray(),
                Prime = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFDC7".HexToByteArray(),
                Order = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF27E69532F48D89116FF22B8D4E0560609B4B38ABFAD2B85DCACDB1411F10B275".HexToByteArray(),
                Cofactor = "01".HexToByteArray(),
                G = new ECPoint
                {
                    X = "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000003".HexToByteArray(),
                    Y = "7503CFE87A836AE3A61B8816E25450E6CE5E1C93ACF1ABC1778064FDCBEFA921DF1626BE4FD036E93D75E6A50E3A41E98028FE5FC235F5B889A589CB5215F2A4".HexToByteArray()
                }
            };
        }
    }
    public static class StringExtensions
    {
        public static byte[] HexToByteArray(this string hex)
        {
            if (hex.Length % 2 != 0)
                throw new ArgumentException("Длина HEX-строки должна быть четной.");

            return Enumerable.Range(0, hex.Length / 2)
                .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                .ToArray();
        }
    }
}
