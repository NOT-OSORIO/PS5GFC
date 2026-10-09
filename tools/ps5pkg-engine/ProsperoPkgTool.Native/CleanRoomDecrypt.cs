using System;
using System.IO;
using ProsperoPkgTool.Containers;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Native;

public static class CleanRoomDecrypt
{
	public sealed record Result(string ContentId, string PackageKind, int SuperblockIndex, int BlockCount, string SeedHex, bool SuperblockIcvValid, bool KeystoneFound, long InnerImageLength, string? InnerImagePath);

	public static Result DecryptOuter(string packagePath, string passcode, string? outputPath)
	{
		ProsperoPackageInspection prosperoPackageInspection = ProsperoPackageReader.Read(packagePath);
		ProsperoFihHeader fih = prosperoPackageInspection.Fih;
		if ((object)fih == null)
		{
			throw new InvalidDataException("Package has no FIH header; expected a finalized image.");
		}
		if (prosperoPackageInspection.Kind == ProsperoPackageKind.FinalizedPatchDebug)
		{
			throw new InvalidDataException("Patch outer-PFS decryption is disabled until patch PFS/NAPS overlay semantics are verified.");
		}
		string contentId = prosperoPackageInspection.Cnt.ContentId;
		if (contentId.Length == 0)
		{
			throw new InvalidDataException("Package CNT has an empty content id.");
		}
		using FileStream stream = File.OpenRead(Path.GetFullPath(packagePath));
		OuterPfsReader outerPfsReader = checked(OuterPfsReader.Open(stream, (long)fih.PfsOffset, (long)fih.PfsSize));
		(byte[] TweakKey, byte[] DataKey) tuple = ProsperoKeys.DeriveOuterPfsXtsKeys(ProsperoKeys.ComputeEkpfs(contentId, passcode), outerPfsReader.Seed);
		var (array, _) = tuple;
		using AesXts xts = new AesXts(tuple.DataKey, array);
		bool superblockIcvValid = outerPfsReader.VerifySuperblockIcv();
		byte[] array2 = outerPfsReader.ReadInnerImage(xts);
		bool keystoneFound = Contains(array2, "keystone"u8.ToArray());
		string innerImagePath = null;
		if (outputPath != null)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
			File.WriteAllBytes(outputPath, array2);
			innerImagePath = outputPath;
		}
		return new Result(contentId, prosperoPackageInspection.Kind.ToString(), outerPfsReader.SuperblockIndex, outerPfsReader.BlockCount, Convert.ToHexString(outerPfsReader.Seed).ToLowerInvariant(), superblockIcvValid, keystoneFound, array2.Length, innerImagePath);
	}

	private static bool Contains(byte[] haystack, byte[] needle)
	{
		for (int i = 0; i + needle.Length <= haystack.Length; i++)
		{
			if (((ReadOnlySpan<byte>)haystack.AsSpan(i, needle.Length)).SequenceEqual((ReadOnlySpan<byte>)needle))
			{
				return true;
			}
		}
		return false;
	}
}
