using System.Security.Cryptography;
using System.Text;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Encrypts recording track files at rest with AES-256 in CTR mode. CTR is chosen over CBC/GCM because it
/// is <em>seekable</em>: the keystream for any byte offset depends only on the counter block at that
/// offset, so the web player's HTTP range requests (which the &lt;video&gt;/&lt;audio&gt; elements rely on
/// to seek without downloading the whole file) map cleanly onto a partial decrypt. Each file gets a random
/// 16-byte nonce stored as a header prefix; the per-file counter starts at that nonce.
///
/// The key is derived (PBKDF2-HMAC-SHA256) from the same master keyring passphrase that protects the
/// DataProtection keyring (<c>DataProtection:MasterKeyPassphrase</c>), via a distinct info label so it is
/// independent of the credential-encryption key. No key material is written to disk; losing the passphrase
/// means the recordings are unrecoverable (by design).
///
/// NOTE: CTR provides confidentiality only, not integrity. Tamper-evidence is handled separately by the
/// SHA-256 hash chain on <see cref="Models.Recording"/> (computed over the ciphertext on disk).
/// </summary>
public sealed class RecordingCryptor
{
    public const int HeaderSize = 16;   // random nonce stored at the front of each encrypted file
    private const int Pbkdf2Iterations = 200_000;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("KSol.ZeroVDI.RecordingCryptor.v1");

    private readonly byte[] _key; // 32 bytes (AES-256)

    public RecordingCryptor(KeyringEncryptor keyring)
    {
        // Reuse the master passphrase but with a recorder-specific derivation so the recording key is not
        // the same bytes as the keyring/credential key.
        var passphrase = keyring.PassphraseBytes;
        _key = Rfc2898DeriveBytes.Pbkdf2(passphrase, Salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>
    /// Encrypt <paramref name="path"/> in place: write [16-byte nonce][AES-CTR ciphertext] to a temp file
    /// then atomically replace the plaintext. No-op-safe to call once per file at mux time.
    /// </summary>
    public void EncryptFileInPlace(string path)
    {
        var nonce = RandomNumberGenerator.GetBytes(HeaderSize);
        var tmp = path + ".enc.tmp";
        using (var src = File.OpenRead(path))
        using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            dst.Write(nonce, 0, nonce.Length);
            using var ctr = new CtrStream(dst, _key, nonce, leaveOpen: true);
            src.CopyTo(ctr);
        }
        File.Delete(path);
        File.Move(tmp, path);
    }

    /// <summary>
    /// Open an encrypted file as a transparently-decrypting, <em>seekable</em> stream positioned over the
    /// plaintext (the 16-byte header is hidden). Range requests on the returned stream decrypt only the
    /// requested span.
    /// </summary>
    public Stream OpenDecryptingStream(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var nonce = new byte[HeaderSize];
        if (fs.Read(nonce, 0, HeaderSize) != HeaderSize)
        {
            fs.Dispose();
            throw new InvalidDataException("Encrypted recording file is shorter than its header.");
        }
        return new CtrDecryptStream(fs, _key, nonce);
    }

    // ----- AES-CTR helpers -----------------------------------------------------------------------

    /// <summary>Produce the AES-CTR keystream block for a given 16-byte counter using AES-ECB on the counter.</summary>
    private static byte[] Keystream(Aes aes, byte[] counter)
    {
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(counter, 0, 16);
    }

    /// <summary>Build the counter block for AES-CTR at a given 16-byte block index: nonce treated as the base, big-endian add.</summary>
    private static byte[] CounterFor(byte[] nonce, long blockIndex)
    {
        var c = (byte[])nonce.Clone();
        // Add blockIndex to the 128-bit big-endian counter (nonce IS the starting counter).
        ulong add = (ulong)blockIndex;
        for (int i = 15; i >= 0 && add > 0; i--)
        {
            ulong sum = (ulong)c[i] + (add & 0xFF);
            c[i] = (byte)sum;
            add >>= 8;
            // propagate carry
            ulong carry = sum >> 8;
            for (int j = i - 1; j >= 0 && carry > 0; j--)
            {
                ulong s2 = (ulong)c[j] + carry;
                c[j] = (byte)s2;
                carry = s2 >> 8;
            }
        }
        return c;
    }

    /// <summary>Write-side CTR stream: XORs written bytes with the keystream and forwards to the inner stream.</summary>
    private sealed class CtrStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;
        private readonly Aes _aes;
        private readonly byte[] _nonce;
        private long _pos; // plaintext byte position

        public CtrStream(Stream inner, byte[] key, byte[] nonce, bool leaveOpen)
        {
            _inner = inner; _leaveOpen = leaveOpen; _nonce = nonce;
            _aes = Aes.Create(); _aes.Mode = CipherMode.ECB; _aes.Padding = PaddingMode.None; _aes.Key = key;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var outBuf = new byte[count];
            for (int i = 0; i < count; i++)
            {
                long block = _pos / 16;
                int within = (int)(_pos % 16);
                if (within == 0) _ks = Keystream(_aes, CounterFor(_nonce, block));
                outBuf[i] = (byte)(buffer[offset + i] ^ _ks![within]);
                _pos++;
            }
            _inner.Write(outBuf, 0, count);
        }
        private byte[]? _ks;

        public override void Flush() => _inner.Flush();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _aes.Dispose(); if (!_leaveOpen) _inner.Dispose(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Read-side seekable CTR stream over an encrypted file. Position 0 == first plaintext byte (the
    /// 16-byte file header is skipped). Seeking recomputes the keystream block at the target offset, so
    /// range requests decrypt only what they read.
    /// </summary>
    private sealed class CtrDecryptStream : Stream
    {
        private readonly FileStream _file;
        private readonly Aes _aes;
        private readonly byte[] _nonce;
        private readonly long _length;     // plaintext length
        private long _pos;                 // plaintext position
        private byte[]? _ks;

        public CtrDecryptStream(FileStream file, byte[] key, byte[] nonce)
        {
            _file = file; _nonce = nonce;
            _length = file.Length - HeaderSize;
            _aes = Aes.Create(); _aes.Mode = CipherMode.ECB; _aes.Padding = PaddingMode.None; _aes.Key = key;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _length) return 0;
            int toRead = (int)Math.Min(count, _length - _pos);
            int n = _file.Read(buffer, offset, toRead);
            for (int i = 0; i < n; i++)
            {
                long block = _pos / 16;
                int within = (int)(_pos % 16);
                if (within == 0 || _ks == null) _ks = Keystream(_aes, CounterFor(_nonce, block));
                buffer[offset + i] = (byte)(buffer[offset + i] ^ _ks[within]);
                _pos++;
            }
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => _length + offset,
                _ => offset,
            };
            target = Math.Clamp(target, 0, _length);
            _file.Seek(HeaderSize + target, SeekOrigin.Begin);
            _pos = target;
            _ks = null; // force keystream recompute at the new block
            return _pos;
        }

        public override void Flush() { }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _aes.Dispose(); _file.Dispose(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => Seek(value, SeekOrigin.Begin); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
