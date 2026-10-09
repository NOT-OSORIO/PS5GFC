using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPackageVerifier
{
	private const int ScopeStructural = 1;

	private const int ScopeDebug = 2;

	private const int ScopeBuild = 4;

	private const int ScopeFull = 8;

	private const int ScopeEnvelope = 15;

	private const int ScopePayload = 14;

	public static ProsperoPackageVerificationReport Verify(string packagePath, string? passcode = null, string? referencePackagePath = null)
	{
		return Verify(packagePath, passcode, referencePackagePath, VerificationPolicy.DebugPackage);
	}

	public static ProsperoPackageVerificationReport Verify(string packagePath, string? passcode, string? referencePackagePath, VerificationPolicy policy)
	{
		int scope = policy switch
		{
			VerificationPolicy.Structural => 1,
			VerificationPolicy.BuildAcceptance => 4,
			VerificationPolicy.FullAvailableChecks => 8,
			_ => 2,
		};
		List<VerificationCheck> checks = new List<VerificationCheck>();
		ProsperoPackageInspection prosperoPackageInspection;
		try
		{
			prosperoPackageInspection = ProsperoPackageReader.Read(packagePath);
		}
		catch (Exception ex) when ((ex is IOException || ex is InvalidDataException) ? true : false)
		{
			return new ProsperoPackageVerificationReport(new _003C_003Ez__ReadOnlySingleElementList<VerificationCheck>(new VerificationCheck("Package structural bounds", VerificationState.Fail, ex.ToString())));
		}
		Add("Package structural bounds", (!prosperoPackageInspection.Structure.IsValid) ? VerificationState.Fail : VerificationState.Pass, prosperoPackageInspection.Structure.IsValid ? "FIH/CNT ranges are valid" : string.Join("; ", from i in prosperoPackageInspection.Structure.Issues
			where i.IsError
			select i.Code), 15);
		VerifyCnt(packagePath, prosperoPackageInspection, Add);
		bool flag = ProsperoPackageContent.TryOpenSi(packagePath, prosperoPackageInspection, out string error);
		Add("SI ZIP readability", (!flag) ? (prosperoPackageInspection.Segments.Any((ProsperoPackageSegment s) => s.Name == "SI" && s.Size > 0) ? VerificationState.Fail : VerificationState.NotPresent) : VerificationState.Pass, flag ? "Stored SI ZIP opens and its directory enumerates (member contents are not parsed)" : (error ?? "No SI segment"), 8);
		VerificationCheck verificationCheck = VerifyFihDigest(packagePath, prosperoPackageInspection);
		Add(verificationCheck.Name, verificationCheck.State, verificationCheck.Detail, 15);
		Add("RSA signature", VerificationState.Unsupported, "Public-key signature verification is not implemented in this clean-room build", 8);
		bool flag2 = prosperoPackageInspection.Kind == ProsperoPackageKind.FinalizedRetail;
		bool flag3 = prosperoPackageInspection.Kind == ProsperoPackageKind.FinalizedPatchDebug && string.IsNullOrWhiteSpace(referencePackagePath);
		if (flag2 | flag3)
		{
			string detail = (flag2 ? "Retail payload key material is unavailable to this clean-room reader" : "Patch payload reconstruction requires a base/reference package path");
			AddPayloadChecks(Add, VerificationState.Unavailable, detail, 14);
			return new ProsperoPackageVerificationReport(checks);
		}
		try
		{
			if (prosperoPackageInspection.Kind == ProsperoPackageKind.FinalizedDebug && ProsperoPackageContent.RequiresFileBacked(packagePath, passcode))
			{
				using ProsperoFileBackedPackage prosperoFileBackedPackage = ProsperoPackageContent.ReadFileBacked(packagePath, passcode);
				prosperoFileBackedPackage.DecodeAllBlocks();
				AddDecodedChecks(Add, prosperoFileBackedPackage.Blocks, prosperoFileBackedPackage.Entries, prosperoFileBackedPackage.OuterPfsIcvValid, ProsperoPackageKind.FinalizedDebug);
			}
			else
			{
				ProsperoDecodedPackage prosperoDecodedPackage = ProsperoPackageContent.Read(packagePath, passcode, referencePackagePath);
				AddDecodedChecks(Add, prosperoDecodedPackage.Blocks, ProsperoInnerPfsReader.Enumerate(prosperoDecodedPackage.Mount), prosperoDecodedPackage.OuterPfsIcvValid, prosperoDecodedPackage.Inspection.Kind);
			}
		}
		catch (Exception ex2) when ((ex2 is InvalidDataException || ex2 is IOException || ex2 is CryptographicException || ex2 is NotSupportedException || ex2 is ArgumentException) ? true : false)
		{
			VerificationState state = ((ex2 is NotSupportedException) ? VerificationState.Unsupported : VerificationState.Error);
			AddPayloadChecks(Add, state, ex2.ToString(), 14);
		}
		return new ProsperoPackageVerificationReport(checks);
		void Add(string name, VerificationState state2, string detail2, int mask)
		{
			checks.Add(new VerificationCheck(name, state2, detail2, (mask & scope) != 0));
		}
	}

	private static void AddPayloadChecks(Action<string, VerificationState, string, int> add, VerificationState state, string detail, int mask)
	{
		add("Outer PFS superblock ICV", state, detail, mask);
		add("NAPS structure", state, detail, mask);
		add("Kraken decode/reconstruction", state, detail, mask);
		add("Inner PFS structural integrity", state, detail, mask);
		add("PFS block hashes/digests", VerificationState.Unsupported, "Per-block digest verification is not implemented", 8);
		add("PlayGo CRC", VerificationState.Unsupported, "Independent PlayGo CRC verification is not implemented", 8);
	}

	private static void AddDecodedChecks(Action<string, VerificationState, string, int> add, IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> blocks, IReadOnlyList<ProsperoInnerPfsReader.Entry> entries, bool icvValid, ProsperoPackageKind kind)
	{
		add("Outer PFS superblock ICV", (!icvValid) ? VerificationState.Fail : VerificationState.Pass, icvValid ? "SHA3-256 ICV matches" : "SHA3-256 ICV mismatch", 14);
		add("NAPS structure", VerificationState.Pass, (kind == ProsperoPackageKind.FinalizedPatchDebug) ? "RUN-addressed patch NAPS reconstructed" : $"{blocks.Count} CblockInfo data blocks walked", 14);
		add("Kraken decode/reconstruction", VerificationState.Pass, "All stored/Kraken/alias blocks decoded", 14);
		add("Inner PFS structural integrity", VerificationState.Pass, $"{entries.Count} logical entries", 14);
		add("PFS block hashes/digests", VerificationState.Unsupported, "Per-block digest verification is not implemented", 8);
		add("PlayGo CRC", VerificationState.Unsupported, "Independent PlayGo CRC verification is not implemented", 8);
	}

	private static VerificationCheck VerifyFihDigest(string packagePath, ProsperoPackageInspection inspection)
	{
		ProsperoFihHeader fih = inspection.Fih;
		if ((object)fih == null || fih.PfsSize < 65536)
		{
			return new VerificationCheck("FIH digest", VerificationState.Unavailable, "No usable FIH outer-PFS region");
		}
		try
		{
			long position = (long)(inspection.Lih?.FihOffset ?? 0);
			using FileStream fileStream = File.OpenRead(packagePath);
			byte[] array = new byte[256];
			fileStream.Position = position;
			fileStream.ReadExactly(array);
			byte[] array2 = array.AsSpan(48, 32).ToArray();
			bool flag = ((ReadOnlySpan<byte>)array.AsSpan(112, 32)).SequenceEqual((ReadOnlySpan<byte>)array2) && ((ReadOnlySpan<byte>)array.AsSpan(208, 32)).SequenceEqual((ReadOnlySpan<byte>)array2);
			OuterPfsReader outerPfsReader = OuterPfsReader.Open(fileStream, (long)fih.PfsOffset, (long)fih.PfsSize);
			byte[] array3 = new byte[65536];
			fileStream.Position = (long)fih.PfsOffset + (long)outerPfsReader.SuperblockIndex * 65536L;
			fileStream.ReadExactly(array3);
			bool flag2 = ((ReadOnlySpan<byte>)Sha3.Sha3_256(array3).AsSpan()).SequenceEqual((ReadOnlySpan<byte>)array2);
			return (flag2 & flag) ? new VerificationCheck("FIH digest", VerificationState.Pass, "FIH game digest = SHA3-256(plaintext outer-PFS superblock)") : new VerificationCheck("FIH digest", VerificationState.Fail, (!flag2) ? "FIH game digest does not match SHA3-256(outer-PFS superblock)" : "FIH digest mirrors at 0x70/0xD0 disagree");
		}
		catch (Exception ex) when ((ex is IOException || ex is InvalidDataException) ? true : false)
		{
			return new VerificationCheck("FIH digest", VerificationState.Error, ex.ToString());
		}
	}

	private static void VerifyCnt(string packagePath, ProsperoPackageInspection inspection, Action<string, VerificationState, string, int> add)
	{
		try
		{
			ProsperoCntEntry prosperoCntEntry = inspection.Entries.FirstOrDefault((ProsperoCntEntry e) => e.Id == 1);
			if ((object)prosperoCntEntry == null)
			{
				add("CNT body/entry digests", VerificationState.NotPresent, "DIGESTS entry is absent", 15);
				return;
			}
			byte[] array = ProsperoPackageContent.ReadCntEntry(packagePath, inspection, prosperoCntEntry);
			ProsperoCntEntry[] array2 = inspection.Entries.OrderBy((ProsperoCntEntry e) => e.Id).ToArray();
			if (array.Length != array2.Length * 32)
			{
				throw new InvalidDataException("DIGESTS length does not match the CNT entry count.");
			}
			for (int num = 0; num < array2.Length; num++)
			{
				if (array2[num].Id == 1)
				{
					continue;
				}
				bool flag = false;
				foreach (long item in ProsperoPackageContent.CntDigestLengths(array2[num]))
				{
					byte[] array3;
					try
					{
						array3 = ProsperoPackageContent.ReadCntEntry(packagePath, inspection, array2[num], item);
					}
					catch (InvalidDataException)
					{
						continue;
					}
					if (((ReadOnlySpan<byte>)Sha3.Sha3_256(array3).AsSpan()).SequenceEqual((ReadOnlySpan<byte>)array.AsSpan(num * 32, 32)))
					{
						flag = true;
						break;
					}
				}
				if (!flag)
				{
					throw new InvalidDataException($"Digest mismatch for CNT entry 0x{array2[num].Id:X8}.");
				}
			}
			add("CNT body/entry digests", VerificationState.Pass, $"{array2.Length - 1} entry digests match", 15);
			add("GeneralDigests", (!VerifyGeneral(packagePath, inspection)) ? VerificationState.Fail : VerificationState.Pass, "Verified param/playgo slots supported by the clean-room writer", 15);
		}
		catch (Exception ex2) when ((ex2 is InvalidDataException || ex2 is IOException) ? true : false)
		{
			add("CNT body/entry digests", VerificationState.Fail, ex2.Message, 15);
			add("GeneralDigests", VerificationState.Unavailable, "CNT digest table is invalid", 15);
		}
	}

	private static bool VerifyGeneral(string path, ProsperoPackageInspection inspection)
	{
		ProsperoCntEntry prosperoCntEntry = inspection.Entries.FirstOrDefault((ProsperoCntEntry e) => e.Id == 128);
		if ((object)prosperoCntEntry == null)
		{
			return false;
		}
		byte[] array = ProsperoPackageContent.ReadCntEntry(path, inspection, prosperoCntEntry);
		if (array.Length != 480)
		{
			return false;
		}
		ProsperoCntEntry prosperoCntEntry2 = inspection.Entries.FirstOrDefault((ProsperoCntEntry e) => e.Id == 8192);
		if ((object)prosperoCntEntry2 != null && !((ReadOnlySpan<byte>)Sha3.Sha3_256(ProsperoPackageContent.ReadCntEntry(path, inspection, prosperoCntEntry2)).AsSpan()).SequenceEqual((ReadOnlySpan<byte>)array.AsSpan(192, 32)))
		{
			return false;
		}
		byte[] array2 = new uint[3] { 4097u, 8208u, 8209u }.SelectMany((uint id) => from e in inspection.Entries
			where e.Id == id
			select Sha3.Sha3_256(ProsperoPackageContent.ReadCntEntry(path, inspection, e))).SelectMany((byte[] digest) => digest).ToArray();
		if (array2.Length != 0)
		{
			return ((ReadOnlySpan<byte>)Sha3.Sha3_256(array2).AsSpan()).SequenceEqual((ReadOnlySpan<byte>)array.AsSpan(224, 32));
		}
		return true;
	}
}
