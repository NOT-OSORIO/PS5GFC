using System;
using System.Text;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Content;

public sealed class ProsperoDebugLicense
{
	public const int EkpfsSize = 32;

	public const int PasscodeLength = 32;

	public const int ContentIdMaxLength = 36;

	public static string DefaultPasscode => new string('0', 32);

	public required string ContentId { get; init; }

	public required string Passcode { get; init; }

	public bool RequiresRif => false;

	public static ProsperoDebugLicense Create(string contentId, string? passcode = null)
	{
		string passcode2 = passcode ?? DefaultPasscode;
		ProsperoDebugLicense prosperoDebugLicense = new ProsperoDebugLicense
		{
			ContentId = contentId,
			Passcode = passcode2
		};
		if (!prosperoDebugLicense.Validate(out string error))
		{
			throw new ArgumentException(error, "contentId");
		}
		return prosperoDebugLicense;
	}

	public byte[] DeriveEkpfs()
	{
		return ProsperoKeys.ComputeEkpfs(ContentId, Passcode);
	}

	public (byte[] TweakKey, byte[] DataKey) DeriveImageEncryptionKeys(byte[] seed)
	{
		return ProsperoKeys.DeriveOuterPfsXtsKeys(DeriveEkpfs(), seed);
	}

	public bool Validate(out string? error)
	{
		if (string.IsNullOrEmpty(ContentId) || Encoding.ASCII.GetByteCount(ContentId) > 36)
		{
			error = $"A content id is 1..{36} ASCII bytes.";
			return false;
		}
		if (string.IsNullOrEmpty(Passcode) || Passcode.Length != 32)
		{
			error = $"A passcode is exactly {32} characters.";
			return false;
		}
		error = null;
		return true;
	}

	public override string ToString()
	{
		return $"DebugLicense({ContentId}, rif required={RequiresRif})";
	}
}
