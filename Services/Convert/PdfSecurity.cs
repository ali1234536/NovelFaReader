using System;
using System.IO;
using System.Security.Cryptography;

namespace EpubReader.Services
{

// PDF password states (port of PdfSecurity.kt): the importer tells "ask the user", "wrong password" and "unsupported encryption" apart
// instead of lumping them all into "damaged file".
public class PdfPasswordRequiredException : Exception
{
	public PdfPasswordRequiredException() : base("PDF requires a password")
	{
	}
}

public class PdfPasswordWrongException : Exception
{
	public PdfPasswordWrongException() : base("PDF password is wrong")
	{
	}
}

public class PdfUnsupportedEncryptionException : Exception
{
	public PdfUnsupportedEncryptionException(string message) : base(message)
	{
	}
}

// Pure crypto for the PDF Standard security handler (ISO 32000-1 7.6): MD5/SHA digests, RC4, AES, the R2-R4 password/key algorithms
// and the R5/R6 (AES-256) algorithms, including the iterative R6 hash (2.B). RC4 is implemented here (no framework class for it).
// Password encoding: R<5 passwords are truncated to 32 bytes, R5/R6 to 127 bytes, both as UTF-8.
internal static class PdfCrypto
{
	// The 32-byte padding string every R<5 password computation uses.
	public static readonly byte[] Padding = new byte[]
	{
		0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
		0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
		0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
		0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
	};

	public static byte[] Concat(params byte[][] parts)
	{
		int total = 0;
		foreach (byte[] p in parts)
		{
			total += p.Length;
		}
		byte[] result = new byte[total];
		int pos = 0;
		foreach (byte[] p in parts)
		{
			Buffer.BlockCopy(p, 0, result, pos, p.Length);
			pos += p.Length;
		}
		return result;
	}

	// Kotlin copyOf(newSize): truncates, or zero-pads when newSize is larger.
	public static byte[] CopyOf(byte[] data, int newSize)
	{
		byte[] result = new byte[newSize];
		Buffer.BlockCopy(data, 0, result, 0, Math.Min(data.Length, newSize));
		return result;
	}

	// Kotlin copyOfRange(from, to).
	public static byte[] CopyOfRange(byte[] data, int from, int to)
	{
		byte[] result = new byte[to - from];
		Buffer.BlockCopy(data, from, result, 0, Math.Min(to, data.Length) - from);
		return result;
	}

	public static bool SameBytes(byte[] a, byte[] b)
	{
		if (a == null || b == null || a.Length != b.Length)
		{
			return false;
		}
		for (int i = 0; i < a.Length; i++)
		{
			if (a[i] != b[i])
			{
				return false;
			}
		}
		return true;
	}

	public static byte[] Md5(params byte[][] parts)
	{
		using (MD5 d = MD5.Create())
		{
			return d.ComputeHash(Concat(parts));
		}
	}

	public static byte[] Sha256(params byte[][] parts)
	{
		using (SHA256 d = SHA256.Create())
		{
			return d.ComputeHash(Concat(parts));
		}
	}

	public static byte[] Sha384(byte[] data)
	{
		using (SHA384 d = SHA384.Create())
		{
			return d.ComputeHash(data);
		}
	}

	public static byte[] Sha512(byte[] data)
	{
		using (SHA512 d = SHA512.Create())
		{
			return d.ComputeHash(data);
		}
	}

	// RC4 (symmetric: the same function encrypts and decrypts).
	public static byte[] Rc4(byte[] key, byte[] data)
	{
		int[] s = new int[256];
		for (int i = 0; i < 256; i++)
		{
			s[i] = i;
		}
		int j = 0;
		for (int i = 0; i < 256; i++)
		{
			j = (j + s[i] + (key[i % key.Length] & 0xFF)) & 0xFF;
			int t = s[i];
			s[i] = s[j];
			s[j] = t;
		}
		byte[] output = new byte[data.Length];
		int x = 0;
		int y = 0;
		for (int n = 0; n < data.Length; n++)
		{
			x = (x + 1) & 0xFF;
			y = (y + s[x]) & 0xFF;
			int t = s[x];
			s[x] = s[y];
			s[y] = t;
			output[n] = (byte)((data[n] & 0xFF) ^ s[(s[x] + s[y]) & 0xFF]);
		}
		return output;
	}

	// AES-ECB, no padding (UE/OE/Perms are exact multiples of 16). Null on any failure.
	public static byte[] AesEcbDecrypt(byte[] key, byte[] data)
	{
		try
		{
			using (Aes aes = Aes.Create())
			{
				aes.Mode = CipherMode.ECB;
				aes.Padding = PaddingMode.None;
				aes.Key = key;
				using (ICryptoTransform t = aes.CreateDecryptor())
				{
					return t.TransformFinalBlock(data, 0, data.Length);
				}
			}
		}
		catch (Exception)
		{
			return null;
		}
	}

	public static byte[] AesEcbEncrypt(byte[] key, byte[] data)
	{
		try
		{
			using (Aes aes = Aes.Create())
			{
				aes.Mode = CipherMode.ECB;
				aes.Padding = PaddingMode.None;
				aes.Key = key;
				using (ICryptoTransform t = aes.CreateEncryptor())
				{
					return t.TransformFinalBlock(data, 0, data.Length);
				}
			}
		}
		catch (Exception)
		{
			return null;
		}
	}

	// AES-CBC with an explicit IV, no padding (the caller strips PKCS#7).
	public static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] data)
	{
		try
		{
			using (Aes aes = Aes.Create())
			{
				aes.Mode = CipherMode.CBC;
				aes.Padding = PaddingMode.None;
				aes.Key = key;
				aes.IV = iv;
				using (ICryptoTransform t = aes.CreateDecryptor())
				{
					return t.TransformFinalBlock(data, 0, data.Length);
				}
			}
		}
		catch (Exception)
		{
			return null;
		}
	}

	public static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] data)
	{
		try
		{
			using (Aes aes = Aes.Create())
			{
				aes.Mode = CipherMode.CBC;
				aes.Padding = PaddingMode.None;
				aes.Key = key;
				aes.IV = iv;
				using (ICryptoTransform t = aes.CreateEncryptor())
				{
					return t.TransformFinalBlock(data, 0, data.Length);
				}
			}
		}
		catch (Exception)
		{
			return null;
		}
	}

	// CBC data with a random 16-byte IV prefix, PKCS#7 padded: the layout AESV2/AESV3 streams and strings use.
	// Null when the padding is malformed (wrong key, or corrupt data).
	public static byte[] AesCbcDecryptWithIvPrefix(byte[] key, byte[] data)
	{
		if (data.Length < 32 || (data.Length - 16) % 16 != 0)
		{
			return null;
		}
		byte[] plain = AesCbcDecrypt(key, CopyOfRange(data, 0, 16), CopyOfRange(data, 16, data.Length));
		if (plain == null || plain.Length == 0)
		{
			return null;
		}
		int pad = plain[plain.Length - 1] & 0xFF;
		if (pad < 1 || pad > 16 || pad > plain.Length)
		{
			return null;
		}
		for (int i = plain.Length - pad; i < plain.Length; i++)
		{
			if ((plain[i] & 0xFF) != pad)
			{
				return null;
			}
		}
		return CopyOfRange(plain, 0, plain.Length - pad);
	}

	// R<5 password preparation: truncate to 32 bytes, pad to 32.
	public static byte[] PadPassword32(byte[] password)
	{
		byte[] truncated = CopyOf(password, Math.Min(password.Length, 32));
		if (truncated.Length == 32)
		{
			return truncated;
		}
		return Concat(truncated, CopyOf(Padding, 32 - truncated.Length));
	}

	// R5/R6 password preparation: truncate to 127 bytes, no padding.
	public static byte[] TruncatePassword127(byte[] password)
	{
		return CopyOf(password, Math.Min(password.Length, 127));
	}

	// The R6 iterative hash (Algorithm 2.B): K starts as SHA-256, then each round AES-128-CBC-encrypts (password + K + userKey) x 64
	// and re-hashes with SHA-256/384/512 chosen by E mod 3, until round >= 64 and E's last byte <= round - 32.
	// userKey is empty for the user password and the 48-byte U value for the owner password.
	public static byte[] R6Hash(byte[] password, byte[] salt, byte[] userKey)
	{
		byte[] k = Sha256(password, salt, userKey);
		int round = 0;
		while (true)
		{
			byte[] baseBytes = Concat(password, k, userKey);
			byte[] k1 = new byte[baseBytes.Length * 64];
			for (int i = 0; i < 64; i++)
			{
				Buffer.BlockCopy(baseBytes, 0, k1, i * baseBytes.Length, baseBytes.Length);
			}
			byte[] e = AesCbcEncrypt(CopyOfRange(k, 0, 16), CopyOfRange(k, 16, 32), k1);
			if (e == null)
			{
				return CopyOf(k, 32);
			}
			int rem = 0;
			for (int i = 0; i < 16; i++)
			{
				rem = (rem * 256 + (e[i] & 0xFF)) % 3;
			}
			if (rem == 0)
			{
				k = Sha256(e);
			}
			else if (rem == 1)
			{
				k = Sha384(e);
			}
			else
			{
				k = Sha512(e);
			}
			round++;
			if (round >= 64 && (e[e.Length - 1] & 0xFF) <= round - 32)
			{
				break;
			}
		}
		return CopyOf(k, 32);
	}
}
}
