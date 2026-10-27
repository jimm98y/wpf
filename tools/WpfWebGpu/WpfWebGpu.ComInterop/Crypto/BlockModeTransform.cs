// A block cipher's ICryptoTransform as .NET's own SymmetricAlgorithm hands one out: ECB, CBC or CFB (the
// feedback a byte or a whole block), and padding to the segment size with PKCS #7, zeros, ANSI X9.23,
// ISO 10126 or none. Used by the managed ciphers that stand in for the browser's missing ones (ManagedDes,
// ManagedAes); the cipher itself is a block function.

using System.Security.Cryptography;

namespace WpfWebGpu.ComInterop;

internal interface IBlockCipher
{
    int BlockSize { get; }
    void Encrypt(ReadOnlySpan<byte> input, Span<byte> output);
    void Decrypt(ReadOnlySpan<byte> input, Span<byte> output);
}

internal sealed class BlockModeTransform : ICryptoTransform
{
    private readonly IBlockCipher _cipher;
    private readonly bool _encrypt;
    private readonly CipherMode _mode;
    private readonly PaddingMode _padding;
    private readonly int _segment;      // a block, or CFB's feedback
    private readonly byte[] _chain;     // CBC's previous block, CFB's shift register
    private readonly byte[] _scratch;
    private byte[]? _held;              // decrypting with padding: the last segment, until the final one is known

    public BlockModeTransform(IBlockCipher cipher, CipherMode mode, PaddingMode padding, int feedbackBytes, byte[]? iv, bool encrypt)
    {
        int block = cipher.BlockSize;
        if (mode is not (CipherMode.CBC or CipherMode.ECB or CipherMode.CFB)) throw new CryptographicException("Specified cipher mode is not valid for this algorithm.");
        if (mode == CipherMode.CFB && feedbackBytes != 1 && feedbackBytes != block) throw new CryptographicException("Specified feedback size is not valid for this algorithm.");
        if (padding is < PaddingMode.None or > PaddingMode.ISO10126) throw new CryptographicException("Specified padding mode is not valid for this algorithm.");
        _cipher = cipher;
        _encrypt = encrypt;
        _mode = mode;
        _padding = padding;
        _segment = mode == CipherMode.CFB ? feedbackBytes : block;
        _chain = new byte[block];
        _scratch = new byte[block];
        if (mode != CipherMode.ECB)
        {
            if (iv == null) throw new CryptographicException("The cipher mode specified requires that an initialization vector (IV) be used.");
            if (iv.Length < block) throw new CryptographicException("Specified initialization vector (IV) does not match the block size for this algorithm.");
            Buffer.BlockCopy(iv, 0, _chain, 0, block);
        }
    }

    public int InputBlockSize => _segment;
    public int OutputBlockSize => _segment;
    public bool CanTransformMultipleBlocks => true;
    public bool CanReuseTransform => false;
    public void Dispose() { Array.Clear(_chain); Array.Clear(_scratch); }

    private bool HoldsBack => !_encrypt && _padding is not (PaddingMode.None or PaddingMode.Zeros);

    public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
    {
        ArgumentNullException.ThrowIfNull(inputBuffer);
        ArgumentNullException.ThrowIfNull(outputBuffer);
        if (inputCount % _segment != 0) throw new CryptographicException("The input data is not a complete block.");
        int written = 0;
        if (HoldsBack)
        {
            // hold back the last segment: it may be the one with the padding
            if (inputCount == 0) return 0;
            if (_held != null) { Segment(_held, 0, outputBuffer, outputOffset); written = _segment; }
            for (int i = 0; i < inputCount - _segment; i += _segment, written += _segment)
                Segment(inputBuffer, inputOffset + i, outputBuffer, outputOffset + written);
            _held = new byte[_segment];
            Buffer.BlockCopy(inputBuffer, inputOffset + inputCount - _segment, _held, 0, _segment);
            return written;
        }
        for (int i = 0; i < inputCount; i += _segment) Segment(inputBuffer, inputOffset + i, outputBuffer, outputOffset + i);
        return inputCount;
    }

    public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
    {
        ArgumentNullException.ThrowIfNull(inputBuffer);
        int u = _segment;
        if (_encrypt)
        {
            int rem = inputCount % u;
            if (_padding == PaddingMode.None && rem != 0) throw new CryptographicException("The input data is not a complete block.");
            int pad = _padding switch
            {
                PaddingMode.None => 0,
                PaddingMode.Zeros => rem == 0 ? 0 : u - rem,
                _ => u - rem,
            };
            var data = new byte[inputCount + pad];
            Buffer.BlockCopy(inputBuffer, inputOffset, data, 0, inputCount);
            if (_padding == PaddingMode.PKCS7) data.AsSpan(inputCount).Fill((byte)pad);
            else if (_padding == PaddingMode.ANSIX923 && pad > 0) data[^1] = (byte)pad;
            else if (_padding == PaddingMode.ISO10126 && pad > 0) { RandomNumberGenerator.Fill(data.AsSpan(inputCount, pad - 1)); data[^1] = (byte)pad; }
            var output = new byte[data.Length];
            for (int i = 0; i < data.Length; i += u) Segment(data, i, output, i);
            return output;
        }
        if (inputCount % u != 0) throw new CryptographicException("The input data is not a complete block.");
        int heldLen = _held?.Length ?? 0;
        var all = new byte[heldLen + inputCount];
        if (_held != null) Buffer.BlockCopy(_held, 0, all, 0, heldLen);
        Buffer.BlockCopy(inputBuffer, inputOffset, all, heldLen, inputCount);
        _held = null;
        var plain = new byte[all.Length];
        for (int i = 0; i < all.Length; i += u) Segment(all, i, plain, i);
        if (!HoldsBack || plain.Length == 0) return plain;
        int n = plain[^1];
        if (n < 1 || n > u) throw Bad();
        for (int i = plain.Length - n; i < plain.Length - 1; i++)
        {
            if (_padding == PaddingMode.PKCS7 && plain[i] != n) throw Bad();
            if (_padding == PaddingMode.ANSIX923 && plain[i] != 0) throw Bad();
        }
        return plain[..^n];
    }

    private static CryptographicException Bad() => new("Padding is invalid and cannot be removed.");

    private void Segment(byte[] input, int at, byte[] output, int to)
    {
        int b = _cipher.BlockSize;
        switch (_mode)
        {
            case CipherMode.ECB:
                if (_encrypt) _cipher.Encrypt(input.AsSpan(at, b), output.AsSpan(to, b));
                else _cipher.Decrypt(input.AsSpan(at, b), output.AsSpan(to, b));
                break;
            case CipherMode.CBC:
                if (_encrypt)
                {
                    for (int i = 0; i < b; i++) _scratch[i] = (byte)(input[at + i] ^ _chain[i]);
                    _cipher.Encrypt(_scratch, output.AsSpan(to, b));
                    Buffer.BlockCopy(output, to, _chain, 0, b);
                }
                else
                {
                    Buffer.BlockCopy(input, at, _scratch, 0, b);    // the input may be the output
                    _cipher.Decrypt(_scratch, output.AsSpan(to, b));
                    for (int i = 0; i < b; i++) output[to + i] ^= _chain[i];
                    Buffer.BlockCopy(_scratch, 0, _chain, 0, b);
                }
                break;
            default:
            {
                int u = _segment;
                Span<byte> keystream = stackalloc byte[b];
                Span<byte> cipherText = stackalloc byte[u];
                _cipher.Encrypt(_chain, keystream);
                for (int i = 0; i < u; i++)
                {
                    byte x = input[at + i], y = (byte)(x ^ keystream[i]);
                    output[to + i] = y;
                    cipherText[i] = _encrypt ? y : x;
                }
                Buffer.BlockCopy(_chain, u, _chain, 0, b - u);
                cipherText.CopyTo(_chain.AsSpan(b - u));
                break;
            }
        }
    }

    /// <summary>SymmetricAlgorithm's one-shot members (EncryptCbc and the rest) over a fresh transform.</summary>
    public static bool OneShot(IBlockCipher cipher, CipherMode mode, PaddingMode padding, int feedbackBytes, ReadOnlySpan<byte> iv, bool encrypt,
        ReadOnlySpan<byte> input, Span<byte> destination, out int bytesWritten)
    {
        using var t = new BlockModeTransform(cipher, mode, padding, feedbackBytes, mode == CipherMode.ECB ? null : iv.ToArray(), encrypt);
        byte[] result = t.TransformFinalBlock(input.ToArray(), 0, input.Length);
        if (result.Length > destination.Length) { bytesWritten = 0; return false; }
        result.CopyTo(destination);
        bytesWritten = result.Length;
        return true;
    }
}
