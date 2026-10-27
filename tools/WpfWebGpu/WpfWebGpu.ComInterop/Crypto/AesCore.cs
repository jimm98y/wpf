// AES, FIPS 197: the block cipher alone, one 16-byte block at a time, for the places .NET's own refuses
// to run -- the browser build throws PlatformNotSupportedException from Aes.Create and from AesGcm.
// Compiled into WpfWebGpu.ComInterop (ManagedAes, Aes.Create's stand-in) and into the ProtectedData
// shim, which may not depend on anything outside the framework.

using System;

namespace WpfWebGpu.Crypto
{
    internal sealed class AesCore
    {
        public const int BlockSize = 16;

        private readonly byte[] _roundKeys;
        private readonly int _rounds;

        public AesCore(ReadOnlySpan<byte> key)
        {
            if (key.Length != 16 && key.Length != 24 && key.Length != 32)
                throw new System.Security.Cryptography.CryptographicException("Specified key is not a valid size for this algorithm.");
            int nk = key.Length / 4;
            _rounds = nk + 6;
            int words = 4 * (_rounds + 1);
            _roundKeys = new byte[words * 4];
            key.CopyTo(_roundKeys);
            Span<byte> t = stackalloc byte[4];
            byte rcon = 1;
            for (int i = nk; i < words; i++)
            {
                _roundKeys.AsSpan((i - 1) * 4, 4).CopyTo(t);
                if (i % nk == 0)
                {
                    byte first = t[0];
                    t[0] = (byte)(Sbox[t[1]] ^ rcon);
                    t[1] = Sbox[t[2]];
                    t[2] = Sbox[t[3]];
                    t[3] = Sbox[first];
                    rcon = Xtime(rcon);
                }
                else if (nk > 6 && i % nk == 4)
                {
                    for (int k = 0; k < 4; k++) t[k] = Sbox[t[k]];
                }
                for (int k = 0; k < 4; k++) _roundKeys[i * 4 + k] = (byte)(_roundKeys[(i - nk) * 4 + k] ^ t[k]);
            }
        }

        public void Encrypt(ReadOnlySpan<byte> input, Span<byte> output)
        {
            Span<byte> s = stackalloc byte[16];
            input.Slice(0, 16).CopyTo(s);
            AddRoundKey(s, 0);
            for (int r = 1; r < _rounds; r++)
            {
                SubShift(s);
                MixColumns(s);
                AddRoundKey(s, r);
            }
            SubShift(s);
            AddRoundKey(s, _rounds);
            s.CopyTo(output);
        }

        public void Decrypt(ReadOnlySpan<byte> input, Span<byte> output)
        {
            Span<byte> s = stackalloc byte[16];
            input.Slice(0, 16).CopyTo(s);
            AddRoundKey(s, _rounds);
            for (int r = _rounds - 1; r > 0; r--)
            {
                InvShiftSub(s);
                AddRoundKey(s, r);
                InvMixColumns(s);
            }
            InvShiftSub(s);
            AddRoundKey(s, 0);
            s.CopyTo(output);
        }

        private void AddRoundKey(Span<byte> s, int round)
        {
            ReadOnlySpan<byte> k = _roundKeys.AsSpan(round * 16, 16);
            for (int i = 0; i < 16; i++) s[i] ^= k[i];
        }

        // the state is column-major: byte 4c + r is row r of column c
        private static void SubShift(Span<byte> s)
        {
            Span<byte> t = stackalloc byte[16];
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++) t[4 * c + r] = Sbox[s[4 * ((c + r) & 3) + r]];
            t.CopyTo(s);
        }

        private static void InvShiftSub(Span<byte> s)
        {
            Span<byte> t = stackalloc byte[16];
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++) t[4 * ((c + r) & 3) + r] = InvSbox[s[4 * c + r]];
            t.CopyTo(s);
        }

        private static void MixColumns(Span<byte> s)
        {
            for (int c = 0; c < 4; c++)
            {
                int o = 4 * c;
                byte a0 = s[o], a1 = s[o + 1], a2 = s[o + 2], a3 = s[o + 3];
                byte all = (byte)(a0 ^ a1 ^ a2 ^ a3);
                s[o] ^= (byte)(all ^ Xtime((byte)(a0 ^ a1)));
                s[o + 1] ^= (byte)(all ^ Xtime((byte)(a1 ^ a2)));
                s[o + 2] ^= (byte)(all ^ Xtime((byte)(a2 ^ a3)));
                s[o + 3] ^= (byte)(all ^ Xtime((byte)(a3 ^ a0)));
            }
        }

        private static void InvMixColumns(Span<byte> s)
        {
            for (int c = 0; c < 4; c++)
            {
                int o = 4 * c;
                byte a0 = s[o], a1 = s[o + 1], a2 = s[o + 2], a3 = s[o + 3];
                s[o] = (byte)(Mul(a0, 14) ^ Mul(a1, 11) ^ Mul(a2, 13) ^ Mul(a3, 9));
                s[o + 1] = (byte)(Mul(a0, 9) ^ Mul(a1, 14) ^ Mul(a2, 11) ^ Mul(a3, 13));
                s[o + 2] = (byte)(Mul(a0, 13) ^ Mul(a1, 9) ^ Mul(a2, 14) ^ Mul(a3, 11));
                s[o + 3] = (byte)(Mul(a0, 11) ^ Mul(a1, 13) ^ Mul(a2, 9) ^ Mul(a3, 14));
            }
        }

        private static byte Xtime(byte b) => (byte)((b << 1) ^ ((b & 0x80) != 0 ? 0x1B : 0));

        private static byte Mul(byte a, int b)
        {
            byte r = 0;
            for (; b != 0; b >>= 1, a = Xtime(a)) if ((b & 1) != 0) r ^= a;
            return r;
        }

        private static readonly byte[] Sbox = MakeSbox();
        private static readonly byte[] InvSbox = Invert(Sbox);

        // the multiplicative inverse in GF(2^8) followed by FIPS 197's affine transform
        private static byte[] MakeSbox()
        {
            var box = new byte[256];
            for (int x = 0; x < 256; x++)
            {
                byte inv = 0;
                if (x != 0)
                    for (int y = 1; y < 256; y++)
                        if (Mul((byte)x, y) == 1) { inv = (byte)y; break; }
                int s = inv;
                for (int i = 1; i <= 4; i++) s ^= (byte)((inv << i) | (inv >> (8 - i)));
                box[x] = (byte)(s ^ 0x63);
            }
            return box;
        }

        private static byte[] Invert(byte[] box)
        {
            var inv = new byte[256];
            for (int i = 0; i < 256; i++) inv[box[i]] = (byte)i;
            return inv;
        }
    }
}
