using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProsperoPkgTool.Containers;
using ProsperoPkgTool.Content;
using ProsperoPkgTool.Crypto;
using ProsperoPkgTool.Gp5;
using ProsperoPkgTool.Native;

namespace ProsperoPkgTool;

public static class CliApplication
{
	public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default(CancellationToken))
	{
		bool flag = args.Length == 0;
		bool flag2;
		if (!flag)
		{
			if (args != null && args.Length == 1)
			{
				string text = args[0];
				if (text == "-h" || text == "--help")
				{
					flag2 = true;
					goto IL_0038;
				}
			}
			flag2 = false;
			goto IL_0038;
		}
		goto IL_003b;
		IL_0038:
		flag = flag2;
		goto IL_003b;
		IL_003b:
		if (flag)
		{
			PrintUsage();
			return Task.FromResult(0);
		}
		string text2 = args[0];
		string[] subArray = args[1..];
		try
		{
			int result;
			switch (text2.ToLowerInvariant())
			{
			case "formats":
				result = PrintFormats();
				break;
			case "gp5gen":
				result = RunGp5Generate(subArray);
				break;
			case "gp5info":
				result = RunGp5Info(subArray);
				break;
			case "gp5validate":
			case "gp5-validate":
				result = RunGp5Validate(subArray);
				break;
			case "gp5format":
			case "gp5-format":
				result = RunGp5Format(subArray);
				break;
			case "source-validate":
				result = RunSourceValidate(subArray);
				break;
			case "playgo-info":
				result = RunPlayGoInfo(subArray);
				break;
			case "pkg-detect":
				result = RunNativePackageInfo(subArray, validateOnly: false, kindOnly: true);
				break;
			case "native-info":
			case "fih-info":
				result = RunNativePackageInfo(subArray, validateOnly: false, kindOnly: false);
				break;
			case "fih-validate":
			case "native-validate":
				result = RunNativePackageInfo(subArray, validateOnly: true, kindOnly: false);
				break;
			case "cnt-entries":
			case "native-entries":
				result = RunNativePackageEntries(subArray);
				break;
			case "native-decrypt":
				result = RunNativeDecrypt(subArray);
				break;
			case "img-blocks":
			case "img-info":
			case "img_info":
				result = RunInnerImageInfo(subArray);
				break;
			case "img-file-list":
			case "img_file_list":
				result = RunInnerImageFileList(subArray);
				break;
			case "img-extract":
			case "img_extract":
				result = RunInnerImageExtract(subArray);
				break;
			case "img-verify":
			case "img_verify":
				result = RunInnerImageVerify(subArray);
				break;
			case "img-create":
			case "img_create":
				result = RunInnerImageCreate(subArray, null, cancellationToken);
				break;
			case "convert":
			case "backup-convert":
			case "backup_convert":
				result = RunBackupConvert(subArray);
				break;
			case "compare":
				result = RunCompare(subArray);
				break;
			case "fake-sign":
			case "fself":
				result = RunFakeSign(subArray);
				break;
			default:
				result = UnknownCommand(text2);
				break;
			}
			return Task.FromResult(result);
		}
		catch (OperationCanceledException)
		{
			Console.Error.WriteLine("Operation cancelled.");
			return Task.FromResult(130);
		}
		catch (ProsperoInsufficientSpaceException ex2)
		{
			Console.Error.WriteLine("Error: " + ex2.Message);
			return Task.FromResult(5);
		}
		catch (Exception ex3) when ((ex3 is IOException || ex3 is UnauthorizedAccessException || ex3 is ArgumentException || ex3 is InvalidDataException) ? true : false)
		{
			Console.Error.WriteLine("Error: " + ex3.Message);
			return Task.FromResult(2);
		}
	}

	private static int UnknownCommand(string command)
	{
		Console.Error.WriteLine("Unknown command '" + command + "'. Run 'ProsperoPkgTool --help' for the complete list.");
		return 2;
	}

	private static int RunGp5Generate(string[] args)
	{
		if (args.Length == 0)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool gp5gen <source-folder> [--out <file>] [--passcode <32 chars>] [--force]");
			return 2;
		}
		string path = args[0];
		string text = null;
		string passcode = "00000000000000000000000000000000";
		bool flag = false;
		for (int i = 1; i < args.Length; i++)
		{
			switch (args[i])
			{
			case "--out":
				if (i + 1 < args.Length)
				{
					text = args[++i];
					continue;
				}
				break;
			case "--passcode":
				if (i + 1 < args.Length)
				{
					passcode = args[++i];
					continue;
				}
				break;
			case "--force":
				flag = true;
				continue;
			}
			throw new ArgumentException("Unknown or incomplete gp5gen option '" + args[i] + "'.");
		}
		string fullPath = Path.GetFullPath(path);
		SourceFolderInspection sourceFolderInspection = SourceFolderValidator.Inspect(fullPath);
		if ((object)sourceFolderInspection.Param == null)
		{
			PrintValidation(sourceFolderInspection.Validation);
			return 4;
		}
		text = Path.GetFullPath(text ?? Path.Combine(fullPath, sourceFolderInspection.Param.TitleId + ".gp5"));
		if (!Path.GetDirectoryName(text).Equals(Path.TrimEndingDirectorySeparator(fullPath), StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("Generated GP5 must be written directly inside the source folder so relative source paths remain valid.");
		}
		if (File.Exists(text) && !flag)
		{
			throw new IOException("Output already exists: " + text + ". Use --force to replace it.");
		}
		Gp5GenerationResult gp5GenerationResult = Gp5Generator.Generate(fullPath, new Gp5GeneratorOptions
		{
			Passcode = passcode
		}, text);
		PrintValidation(gp5GenerationResult.Validation);
		if (!gp5GenerationResult.Validation.IsValid)
		{
			return 4;
		}
		Gp5Xml.Write(gp5GenerationResult.Project, text);
		Console.WriteLine("Generated: " + text);
		Console.WriteLine($"Included files: {gp5GenerationResult.Source.IncludedFiles.Count}");
		Console.WriteLine($"Excluded artifacts: {gp5GenerationResult.Source.ExcludedFiles.Count}");
		return 0;
	}

	private static int RunGp5Info(string[] args)
	{
		if (args.Length != 1)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool gp5info <project.gp5>");
			return 2;
		}
		Gp5Project gp5Project = Gp5Xml.Read(args[0]);
		Console.WriteLine("Format: " + gp5Project.Format);
		Console.WriteLine("Version: " + (gp5Project.Version ?? "(unspecified)"));
		Console.WriteLine("Volume type: " + gp5Project.Volume.VolumeType);
		Console.WriteLine("Content ID: " + (gp5Project.Volume.Package.ContentId ?? "(from param.json)"));
		Console.WriteLine($"Layout: {gp5Project.Layout}");
		Console.WriteLine($"Files: {((gp5Project.Layout == Gp5ContentLayout.FlatFiles) ? gp5Project.Files.Count : CountEntries(gp5Project.RootDirectory))}");
		Gp5ChunkInfo chunkInfo = gp5Project.Volume.ChunkInfo;
		if (chunkInfo != null)
		{
			Console.WriteLine($"Chunks: {chunkInfo.Chunks.Count}");
			Console.WriteLine($"Scenarios: {chunkInfo.Scenarios.Count} (default {chunkInfo.DefaultScenarioId})");
		}
		return 0;
	}

	private static int RunGp5Validate(string[] args)
	{
		if (args.Length != 1)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool gp5validate <project.gp5>");
			return 2;
		}
		string fullPath = Path.GetFullPath(args[0]);
		ValidationReport validationReport = Gp5Validator.Validate(Gp5Xml.Read(fullPath), fullPath);
		PrintValidation(validationReport);
		if (!validationReport.IsValid)
		{
			return 4;
		}
		return 0;
	}

	private static int RunGp5Format(string[] args)
	{
		if (args.Length < 3 || !args[1].Equals("--out", StringComparison.OrdinalIgnoreCase))
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool gp5format <input.gp5> --out <output.gp5> [--force]");
			return 2;
		}
		string fullPath = Path.GetFullPath(args[0]);
		string fullPath2 = Path.GetFullPath(args[2]);
		bool flag = args.Skip(3).Any((string argument) => argument.Equals("--force", StringComparison.OrdinalIgnoreCase));
		if (args.Skip(3).Any((string argument) => !argument.Equals("--force", StringComparison.OrdinalIgnoreCase)))
		{
			throw new ArgumentException("Unknown gp5format option.");
		}
		if (fullPath.Equals(fullPath2, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("gp5format requires a separate output path.");
		}
		if (File.Exists(fullPath2) && !flag)
		{
			throw new IOException("Output already exists: " + fullPath2 + ". Use --force to replace it.");
		}
		Gp5Project project = Gp5Xml.Read(fullPath);
		ValidationReport validationReport = Gp5Validator.Validate(project, fullPath);
		PrintValidation(validationReport);
		if (!validationReport.IsValid)
		{
			return 4;
		}
		Gp5Xml.Write(project, fullPath2);
		Console.WriteLine("Formatted: " + fullPath2);
		return 0;
	}

	private static int RunFakeSign(string[] args)
	{
		string text = null;
		string text2 = null;
		string text3 = null;
		string text4 = null;
		string text5 = null;
		string sceVersionName = null;
		string text6 = null;
		bool flag = false;
		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
			case "--out":
				if (i + 1 < args.Length)
				{
					text2 = args[++i];
					continue;
				}
				break;
			case "--authority":
				if (i + 1 < args.Length)
				{
					text3 = args[++i];
					continue;
				}
				break;
			case "--app-version":
				if (i + 1 < args.Length)
				{
					text4 = args[++i];
					continue;
				}
				break;
			case "--fw-version":
				if (i + 1 < args.Length)
				{
					text5 = args[++i];
					continue;
				}
				break;
			case "--sceversion-name":
				if (i + 1 < args.Length)
				{
					sceVersionName = args[++i];
					continue;
				}
				break;
			case "--sceversion-from":
				if (i + 1 < args.Length)
				{
					text6 = args[++i];
					continue;
				}
				break;
			case "--force":
				flag = true;
				continue;
			}
			if (args[i].StartsWith("--", StringComparison.Ordinal))
			{
				return UnknownOption(args[i]);
			}
			if (text == null)
			{
				text = args[i];
			}
		}
		if (text == null || text2 == null)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool fself <module.elf> --out <module.bin> [--authority <hex64>] [--app-version <hex64>] [--fw-version <hex64>] [--sceversion-name <name> --sceversion-from <reference.elf>] [--force]");
			return 2;
		}
		string fullPath = Path.GetFullPath(text);
		string fullPath2 = Path.GetFullPath(text2);
		if (fullPath.Equals(fullPath2, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("fself requires a separate output path.");
		}
		if (File.Exists(fullPath2) && !flag)
		{
			throw new IOException("Output already exists: " + fullPath2 + ". Use --force to replace it.");
		}
		byte[] record = null;
		if (text6 != null && !ProsperoSelfBuilder.TryGetSceVersionRecord(File.ReadAllBytes(text6), out record))
		{
			throw new InvalidDataException("No .sceversion record was found in the reference module.");
		}
		FselfOptions options = new FselfOptions
		{
			AuthorityId = ((text3 == null) ? ((ulong?)null) : new ulong?(Convert.ToUInt64(text3, 16))),
			AppVersion = ((text4 == null) ? 0 : Convert.ToUInt64(text4, 16)),
			FirmwareVersion = ((text5 == null) ? 0 : Convert.ToUInt64(text5, 16)),
			SceVersionName = sceVersionName,
			SceVersionRecord = record
		};
		byte[] array = ProsperoSelfBuilder.MakeFself(File.ReadAllBytes(fullPath), options);
		File.WriteAllBytes(fullPath2, array);
		Console.WriteLine($"Fake-signed: {fullPath} -> {fullPath2} ({array.Length} bytes)");
		return 0;
	}

	private static int RunSourceValidate(string[] args)
	{
		if (args.Length != 1)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool source-validate <source-folder>");
			return 2;
		}
		SourceFolderInspection sourceFolderInspection = SourceFolderValidator.Inspect(args[0]);
		ProsperoParam param = sourceFolderInspection.Param;
		if ((object)param != null)
		{
			Console.WriteLine("Title ID: " + param.TitleId);
			Console.WriteLine("Content ID: " + param.ContentId);
			Console.WriteLine("Content version: " + (param.ContentVersion ?? "(unspecified)"));
			Console.WriteLine("Volume type: " + param.VolumeType);
		}
		Console.WriteLine($"Included files: {sourceFolderInspection.IncludedFiles.Count}");
		Console.WriteLine($"Excluded artifacts: {sourceFolderInspection.ExcludedFiles.Count}");
		PrintValidation(sourceFolderInspection.Validation);
		if (!sourceFolderInspection.Validation.IsValid)
		{
			return 4;
		}
		return 0;
	}

	private static int RunPlayGoInfo(string[] args)
	{
		if (args.Length != 1)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool playgo-info <playgo-chunk.dat>");
			return 2;
		}
		PlayGoProject playGoProject = PlayGoChunkReader.Read(args[0]);
		Console.WriteLine($"Version: {playGoProject.VersionMajor:X4}.{playGoProject.VersionMinor:X4}");
		Console.WriteLine("Content ID: " + playGoProject.ContentId);
		Console.WriteLine($"Chunks: {playGoProject.Chunks.Count}");
		foreach (PlayGoChunk chunk in playGoProject.Chunks)
		{
			Console.WriteLine($"  {chunk.Id}: {chunk.Label} language-mask=0x{chunk.LanguageMask:X16}");
		}
		Console.WriteLine($"Scenarios: {playGoProject.Scenarios.Count} (default {playGoProject.DefaultScenarioId})");
		foreach (PlayGoScenario scenario in playGoProject.Scenarios)
		{
			Console.WriteLine($"  {scenario.Id}: {scenario.Label} initial={scenario.InitialChunkCount} sequence={Gp5Generator.FormatSequence(scenario.Chunks)}");
		}
		return 0;
	}

	private static int RunNativePackageInfo(string[] args, bool validateOnly, bool kindOnly)
	{
		if (args.Length != 1)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool pkg-detect|native-info|native-validate <package>");
			return 2;
		}
		ProsperoPackageInspection prosperoPackageInspection = ProsperoPackageReader.Read(args[0]);
		Console.WriteLine($"Kind: {prosperoPackageInspection.Kind}");
		if (kindOnly)
		{
			return 0;
		}
		if (!validateOnly)
		{
			Console.WriteLine($"File size: {prosperoPackageInspection.FileSize} (0x{prosperoPackageInspection.FileSize:X})");
			ProsperoFihHeader fih = prosperoPackageInspection.Fih;
			if ((object)fih != null)
			{
				Console.WriteLine($"FIH version: {fih.FormatVersion}");
				Console.WriteLine($"Signed byte: 0x{fih.SignedByte:X2}");
			}
			Console.WriteLine("Content ID: " + ((prosperoPackageInspection.Cnt.ContentId.Length == 0) ? "(empty)" : prosperoPackageInspection.Cnt.ContentId));
			Console.WriteLine($"CNT entries: {prosperoPackageInspection.Cnt.EntryCount}");
			Console.WriteLine($"DRM type: 0x{prosperoPackageInspection.Cnt.DrmType:X8}");
			Console.WriteLine($"Content type: 0x{prosperoPackageInspection.Cnt.ContentType:X8}");
			Console.WriteLine("Segments:");
			foreach (ProsperoPackageSegment segment in prosperoPackageInspection.Segments)
			{
				Console.WriteLine($"  {segment.Name,-3} offset=0x{segment.Offset:X12} size=0x{segment.Size:X12} ({segment.Size} bytes)");
			}
		}
		foreach (VerificationIssue issue in prosperoPackageInspection.Structure.Issues)
		{
			(issue.IsError ? Console.Error : Console.Out).WriteLine($"{(issue.IsError ? "Error" : "Warning")}: {issue.Code}: {issue.Message}");
		}
		Console.WriteLine(prosperoPackageInspection.Structure.IsValid ? "Native structural validation passed." : "Native structural validation failed.");
		if (!prosperoPackageInspection.Structure.IsValid)
		{
			return 4;
		}
		return 0;
	}

	private static int RunNativePackageEntries(string[] args)
	{
		if (args.Length != 1)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool native-entries <package>");
			return 2;
		}
		ProsperoPackageInspection prosperoPackageInspection = ProsperoPackageReader.Read(args[0]);
		foreach (ProsperoCntEntry entry in prosperoPackageInspection.Entries)
		{
			Console.WriteLine($"0x{entry.Id:X8} offset=0x{entry.DataOffset:X8} size=0x{entry.DataSize:X8} {(entry.IsEncrypted ? "encrypted" : "plain"),-9} {entry.DisplayName}");
		}
		Console.WriteLine($"CNT entries: {prosperoPackageInspection.Entries.Count}");
		if (!prosperoPackageInspection.Structure.IsValid)
		{
			return 4;
		}
		return 0;
	}

	private static int RunNativeDecrypt(string[] args)
	{
		string text = null;
		string text2 = null;
		string outputPath = null;
		for (int i = 0; i < args.Length; i++)
		{
			string text3 = args[i];
			if (!(text3 == "--passcode"))
			{
				if (text3 == "--out" && i + 1 < args.Length)
				{
					outputPath = args[++i];
					continue;
				}
			}
			else if (i + 1 < args.Length)
			{
				text2 = args[++i];
				continue;
			}
			if (args[i].StartsWith("--", StringComparison.Ordinal))
			{
				Console.Error.WriteLine("Unknown option: " + args[i]);
				return 2;
			}
			if (text == null)
			{
				text = args[i];
			}
		}
		if (text == null || text2 == null)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool native-decrypt <package> --passcode <32 chars> [--out <inner-image.bin>]");
			return 2;
		}
		CleanRoomDecrypt.Result result = CleanRoomDecrypt.DecryptOuter(text, text2, outputPath);
		Console.WriteLine("Content ID: " + result.ContentId);
		Console.WriteLine("Package kind: " + result.PackageKind);
		Console.WriteLine($"Outer PFS: {result.BlockCount} blocks, superblock at block {result.SuperblockIndex}");
		Console.WriteLine("Seed: " + result.SeedHex);
		Console.WriteLine("Superblock ICV: " + (result.SuperblockIcvValid ? "valid" : "INVALID"));
		Console.WriteLine($"Inner image: {result.InnerImageLength} bytes (0x{result.InnerImageLength:X})");
		Console.WriteLine("Keystone present: " + (result.KeystoneFound ? "yes" : "no"));
		if (result.InnerImagePath != null)
		{
			Console.WriteLine("Wrote inner image: " + result.InnerImagePath);
		}
		if (!result.SuperblockIcvValid || !result.KeystoneFound)
		{
			return 5;
		}
		return 0;
	}

	private static int RunInnerImageInfo(string[] args)
	{
		string text = null;
		string text2 = null;
		bool flag = false;
		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
			case "--passcode":
				if (i + 1 < args.Length)
				{
					text2 = args[++i];
					continue;
				}
				break;
			case "--no_passcode":
				flag = true;
				continue;
			case "--tmp_path":
				if (i + 1 < args.Length)
				{
					i++;
					continue;
				}
				break;
			}
			if (args[i].StartsWith("--", StringComparison.Ordinal))
			{
				return UnknownOption(args[i]);
			}
			if (text == null)
			{
				text = args[i];
			}
		}
		if (text == null)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool img_info [--passcode <passcode>|--no_passcode] <package>");
			return 2;
		}
		ProsperoPackageInspection prosperoPackageInspection = ProsperoPackageReader.Read(text);
		ProsperoPackageCategoryInfo prosperoPackageCategoryInfo = prosperoPackageInspection.Category();
		Console.WriteLine($"Package category: {prosperoPackageCategoryInfo.Category} (raw CNT type 0x{prosperoPackageCategoryInfo.RawContentType:X8}; {prosperoPackageCategoryInfo.Evidence})");
		Console.WriteLine($"Envelope: {prosperoPackageInspection.Kind}");
		Console.WriteLine($"Package size: {prosperoPackageInspection.FileSize} bytes (0x{prosperoPackageInspection.FileSize:X})");
		Console.WriteLine("Content ID: " + ((prosperoPackageInspection.Cnt.ContentId.Length == 0) ? "(unavailable)" : prosperoPackageInspection.Cnt.ContentId));
		Console.WriteLine($"CNT entries: {prosperoPackageInspection.Entries.Count}");
		ProsperoFihHeader fih = prosperoPackageInspection.Fih;
		if ((object)fih != null)
		{
			Console.WriteLine($"FIH: version {fih.FormatVersion}, signed byte 0x{fih.SignedByte:X2}");
			Console.WriteLine($"PFS: offset 0x{fih.PfsOffset:X}, size 0x{fih.PfsSize:X}");
		}
		foreach (ProsperoPackageSegment item in prosperoPackageInspection.Segments.Where((ProsperoPackageSegment s) =>
		{
			string name = s.Name;
			return (name == "SC" || name == "SI") ? true : false;
		}))
		{
			Console.WriteLine($"{item.Name}: offset 0x{item.Offset:X}, size 0x{item.Size:X}");
		}
		PrintParamSummary(text, prosperoPackageInspection);
		if (prosperoPackageInspection.Kind == ProsperoPackageKind.FinalizedDebug && ProsperoPackageContent.RequiresFileBacked(text, flag ? null : text2))
		{
			try
			{
				using ProsperoFileBackedPackage prosperoFileBackedPackage = ProsperoPackageContent.ReadFileBacked(text, flag ? null : text2);
				PrintDecodedSummary(prosperoFileBackedPackage.Naps, prosperoFileBackedPackage.Blocks, prosperoFileBackedPackage.Entries);
				PrintPlayGoSummary(text, prosperoPackageInspection);
				return (!prosperoPackageInspection.Structure.IsValid) ? 4 : 0;
			}
			catch (Exception ex) when ((ex is IOException || ex is InvalidDataException || ex is CryptographicException) ? true : false)
			{
				if (!IsEncryptionRequired(ex))
				{
					Console.Error.WriteLine("Error: " + ex.Message);
					return 4;
				}
				Console.WriteLine("Encrypted payload: key required");
				Console.WriteLine("Inner PFS/PFSC: unavailable");
				Console.WriteLine("PlayGo: unavailable");
				return (!prosperoPackageInspection.Structure.IsValid) ? 4 : 0;
			}
		}
		ProsperoDecodedPackage prosperoDecodedPackage;
		try
		{
			prosperoDecodedPackage = ProsperoPackageContent.Read(text, flag ? null : text2);
		}
		catch (Exception ex2) when ((ex2 is IOException || ex2 is InvalidDataException || ex2 is CryptographicException) ? true : false)
		{
			if (!IsEncryptionRequired(ex2))
			{
				Console.Error.WriteLine("Error: " + ex2.Message);
				return 4;
			}
			Console.WriteLine("Encrypted payload: key required");
			Console.WriteLine("Inner PFS/PFSC: unavailable");
			Console.WriteLine("PlayGo: unavailable");
			return (!prosperoPackageInspection.Structure.IsValid) ? 4 : 0;
		}
		PrintDecodedSummary(prosperoDecodedPackage.Naps, prosperoDecodedPackage.Blocks, ProsperoInnerPfsReader.Enumerate(prosperoDecodedPackage.Mount));
		PrintPlayGoSummary(text, prosperoPackageInspection);
		return 0;
	}

	private static void PrintDecodedSummary(byte[] naps, IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> blocks, IReadOnlyList<ProsperoInnerPfsReader.Entry> entries)
	{
		NapsLayoutDocument napsLayoutDocument = ProsperoNapsLayout.Parse(naps);
		if (blocks is NapsBlockPlan napsBlockPlan)
		{
			Console.WriteLine("NAPS dialect: " + napsBlockPlan.Dialect);
			foreach (string diagnostic in napsBlockPlan.Diagnostics)
			{
				Console.WriteLine("NAPS probe: " + diagnostic);
			}
		}
		Console.WriteLine($"Block kinds: stored {blocks.Count((ProsperoPs5InnerImageReader.InnerBlock b) => !b.IsKraken && !b.IsZeroHole && !b.IsAlias)}, Kraken {blocks.Count((ProsperoPs5InnerImageReader.InnerBlock b) => b.IsKraken)}, zero-fill {blocks.Count((ProsperoPs5InnerImageReader.InnerBlock b) => b.IsZeroHole)}, alias {blocks.Count((ProsperoPs5InnerImageReader.InnerBlock b) => b.IsAlias)}");
		long num = 0L;
		long num2 = 0L;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		foreach (ProsperoPs5InnerImageReader.InnerBlock block in blocks)
		{
			num += block.CompressedLength;
			num2 += block.UncompressedLength;
			if (block.CompressedLength == 0)
			{
				num5++;
			}
			else if (block.IsKraken)
			{
				num4++;
			}
			else
			{
				num3++;
			}
		}
		Console.WriteLine($"Compression type: {napsLayoutDocument.Counts.CompressionType} ({((napsLayoutDocument.Counts.CompressionType == 2) ? "Kraken" : "other")})");
		Console.WriteLine($"ublocks: {napsLayoutDocument.Counts.NumUBlocks}");
		Console.WriteLine($"CblockInfo entries: {napsLayoutDocument.Counts.NumCblockInfo}");
		Console.WriteLine($"Blocks walked: {blocks.Count} (raw {num3}, Kraken {num4}, dedup {num5})");
		Console.WriteLine($"Uncompressed total: {num2} (0x{num2:X})");
		Console.WriteLine($"Compressed total: {num} (0x{num:X})");
		Console.WriteLine($"Inner PFS: supported v2, {entries.Count} logical entries");
	}

	private static void PrintPlayGoSummary(string package, ProsperoPackageInspection structural)
	{
		ProsperoCntEntry prosperoCntEntry = structural.Entries.FirstOrDefault((ProsperoCntEntry e) => e.Id == 4097);
		if ((object)prosperoCntEntry == null)
		{
			Console.WriteLine("PlayGo: not present");
			return;
		}
		try
		{
			PlayGoProject playGoProject = PlayGoChunkReader.Read(ProsperoPackageContent.ReadCntEntry(package, structural, prosperoCntEntry));
			Console.WriteLine($"PlayGo: {playGoProject.Chunks.Count} chunk(s), {playGoProject.Scenarios.Count} scenario(s)");
		}
		catch (InvalidDataException)
		{
			Console.WriteLine("PlayGo: present, unreadable");
		}
	}

	private static int RunBackupConvert(string[] args)
	{
		string text = null;
		string text2 = null;
		string text3 = null;
		string contentId = null;
		string text4 = null;
		string text5 = null;
		bool keepStaging = false;
		bool useEmbeddedRightSprx = false;
		bool noPasscode = false;
		bool verbose = false;
		ProsperoInnerCompressionMode compressionMode = ProsperoInnerCompressionMode.Stored;
		int krakenLevel = 7;
		int krakenThreads = 0;
		ulong? sdkVersionOverride = null;
		string drmTypeOverride = null;
		string titleOverride = null;
		string titleIdOverride = null;
		bool sweepTemp = true;
		int staleHours = 12;
		string tempDir = null;
		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
			case "--out":
				if (i + 1 < args.Length)
				{
					text2 = args[++i];
					continue;
				}
				break;
			case "--decrypted":
				if (i + 1 < args.Length)
				{
					text3 = args[++i];
					continue;
				}
				break;
			case "--content-id":
				if (i + 1 < args.Length)
				{
					contentId = args[++i];
					continue;
				}
				break;
			case "--passcode":
				if (i + 1 < args.Length)
				{
					text4 = args[++i];
					continue;
				}
				break;
			case "--staging":
				if (i + 1 < args.Length)
				{
					text5 = args[++i];
					continue;
				}
				break;
			case "--verbose":
			case "-v":
				verbose = true;
				continue;
			case "--compression":
				if (i + 1 < args.Length)
				{
					ProsperoInnerCompressionMode prosperoInnerCompressionMode;
					switch (args[++i].ToLowerInvariant())
					{
					case "stored":
						prosperoInnerCompressionMode = ProsperoInnerCompressionMode.Stored;
						break;
					case "kraken":
					case "auto":
						prosperoInnerCompressionMode = ProsperoInnerCompressionMode.Auto;
						break;
					default:
						throw new ArgumentException("--compression must be 'stored' or 'kraken'.");
					}
					compressionMode = prosperoInnerCompressionMode;
					continue;
				}
				break;
			case "--kraken-level":
				if (i + 1 < args.Length)
				{
					krakenLevel = ParseIntOption("--kraken-level", args[++i]);
					continue;
				}
				break;
			case "--kraken-threads":
				if (i + 1 < args.Length)
				{
					krakenThreads = ParseIntOption("--kraken-threads", args[++i]);
					continue;
				}
				break;
			case "--sdk":
				if (i + 1 < args.Length)
				{
					sdkVersionOverride = ParseSdkOption(args[++i]);
					continue;
				}
				break;
			case "--drm-type":
				if (i + 1 < args.Length)
				{
					drmTypeOverride = ParseDrmTypeOption(args[++i]);
					continue;
				}
				break;
			case "--no-temp-sweep":
				sweepTemp = false;
				continue;
			case "--stale-hours":
				if (i + 1 < args.Length)
				{
					staleHours = ParseIntOption("--stale-hours", args[++i]);
					continue;
				}
				break;
			case "--temp":
				if (i + 1 < args.Length)
				{
					tempDir = args[++i];
					continue;
				}
				break;
			case "--keep-staging":
				keepStaging = true;
				continue;
			case "--use-embedded-right-sprx":
				useEmbeddedRightSprx = true;
				continue;
			case "--title":
				if (i + 1 < args.Length)
				{
					titleOverride = args[++i];
					continue;
				}
				break;
			case "--title-id":
				if (i + 1 < args.Length)
				{
					titleIdOverride = args[++i];
					continue;
				}
				break;
			case "--no_passcode":
				noPasscode = true;
				continue;
			}
			if (args[i].StartsWith("--", StringComparison.Ordinal))
			{
				return UnknownOption(args[i]);
			}
			if (text == null)
			{
				text = args[i];
			}
		}
		if (text == null || text2 == null)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool convert <backup-folder> --out <package.pkg> [--decrypted <subfolder>] [--content-id <id>] [--passcode <32 chars>|--no_passcode] [--compression stored|kraken] [--kraken-level -4..9] [--kraken-threads <n|0=auto>] [--sdk <major|0x-id>] [--drm-type <token>] [--title <name>] [--title-id <id>] [--no-temp-sweep] [--stale-hours <n>] [--temp <dir>] [--staging <folder>] [--keep-staging] [--use-embedded-right-sprx] [--verbose]");
			return 2;
		}
		string resolvedPasscode = ((noPasscode || string.IsNullOrEmpty(text4)) ? ProsperoDebugLicense.DefaultPasscode : text4);
		string outputPath = Path.GetFullPath(text2);
		ProsperoBackupConversionOptions prosperoBackupConversionOptions = new ProsperoBackupConversionOptions
		{
			BackupFolder = text
		};
		string directoryName = Path.GetDirectoryName(outputPath);
		prosperoBackupConversionOptions.OutputFolder = ((directoryName != null && directoryName.Length > 0) ? directoryName : ".");
		prosperoBackupConversionOptions.DecryptedSubfolder = text3 ?? "decrypted";
		prosperoBackupConversionOptions.ContentId = contentId ?? "";
		prosperoBackupConversionOptions.Passcode = resolvedPasscode;
		prosperoBackupConversionOptions.StagingFolder = text5 ?? "";
		prosperoBackupConversionOptions.KeepStaging = keepStaging;
		prosperoBackupConversionOptions.UseEmbeddedRightSprx = useEmbeddedRightSprx;
		prosperoBackupConversionOptions.Verbose = verbose;
		ProsperoConsoleLog log = new ProsperoConsoleLog();
		log.Write("Build started");
		log.Write("Source folder: " + text);
		log.Write("Output: " + outputPath);
		log.Write($"Compression: {((compressionMode == ProsperoInnerCompressionMode.Stored) ? "stored" : "kraken")}, level {krakenLevel}, threads {((krakenThreads == 0) ? "auto" : krakenThreads.ToString(CultureInfo.InvariantCulture))}");
		if (compressionMode != ProsperoInnerCompressionMode.Stored)
		{
			log.Write("Note: the clean-room Kraken encoder uses a single fixed strategy; the level is stored in the image header but does not change the compressed size.");
		}
		ConsoleProgress console = new ConsoleProgress(verbose, log);
		ProsperoBackupConversionResult result = ProsperoBackupConverter.Convert(prosperoBackupConversionOptions, (ProsperoBackupStagingResult staged) =>
		{
			console.Finish();
			List<string> list = new List<string> { staged.StagingFolder, "--out", outputPath };
			if (noPasscode)
			{
				list.Add("--no_passcode");
			}
			else
			{
				list.Add("--passcode");
				list.Add(resolvedPasscode);
			}
			if (!string.IsNullOrWhiteSpace(contentId))
			{
				list.Add("--content-id");
				list.Add(contentId);
			}
			list.Add("--compression");
			list.Add((compressionMode == ProsperoInnerCompressionMode.Stored) ? "stored" : "kraken");
			list.Add("--kraken-level");
			list.Add(krakenLevel.ToString(CultureInfo.InvariantCulture));
			list.Add("--kraken-threads");
			list.Add(krakenThreads.ToString(CultureInfo.InvariantCulture));
			if (sdkVersionOverride.HasValue)
			{
				ulong valueOrDefault = sdkVersionOverride.GetValueOrDefault();
				list.Add("--sdk");
				list.Add("0x" + valueOrDefault.ToString("X16", CultureInfo.InvariantCulture));
			}
			if (!string.IsNullOrWhiteSpace(drmTypeOverride))
			{
				list.Add("--drm-type");
				list.Add(drmTypeOverride);
			}
			if (!string.IsNullOrWhiteSpace(titleOverride))
			{
				list.Add("--title");
				list.Add(titleOverride);
			}
			if (!string.IsNullOrWhiteSpace(titleIdOverride))
			{
				list.Add("--title-id");
				list.Add(titleIdOverride);
			}
			if (!sweepTemp)
			{
				list.Add("--no-temp-sweep");
			}
			if (staleHours != 12)
			{
				list.Add("--stale-hours");
				list.Add(staleHours.ToString(CultureInfo.InvariantCulture));
			}
			if (!string.IsNullOrWhiteSpace(tempDir))
			{
				list.Add("--temp");
				list.Add(tempDir);
			}
			if (verbose)
			{
				list.Add("--verbose");
			}
			int num = RunInnerImageCreate(list.ToArray(), log);
			if (num != 0)
			{
				throw new InvalidDataException($"The package build failed (exit code {num}).");
			}
			return outputPath;
		}, log.Write, console);
		console.Finish();
		if (File.Exists(outputPath))
		{
			log.Write($"Build finished in {log.Elapsed}; output {new FileInfo(outputPath).Length:N0} bytes.");
		}
		PrintConversionReport(result, log);
		return 0;
	}

	private static void PrintConversionReport(ProsperoBackupConversionResult result, ProsperoConsoleLog log)
	{
		log.Write("Output: " + result.OutputPath);
		log.Write("Content ID: " + result.DebugLicense.ContentId);
		log.Write("Passcode: " + result.DebugLicense.Passcode);
		log.Write($"Debug license requires rif: {result.DebugLicense.RequiresRif}");
		if (result.StagingFolder.Length > 0)
		{
			log.Write("Staging folder: " + result.StagingFolder);
		}
	}

	private static int RunInnerImageCreate(string[] args, ProsperoConsoleLog? sharedLog = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		string text = null;
		string text2 = null;
		string text3 = null;
		string text4 = null;
		string text5 = null;
		string tempDirectory = null;
		ProsperoInnerCompressionMode compression = ProsperoInnerCompressionMode.Auto;
		string text6 = "00000000000000000000000000000000";
		bool flag = false;
		bool flag2 = false;
		bool fakeSignModules = true;
		bool injectRightSprx = true;
		bool enabled = false;
		int krakenLevel = 7;
		int krakenThreads = 0;
		byte[] array = null;
		ulong? sdkVersionOverride = null;
		string drmTypeOverride = null;
		string titleOverride = null;
		string titleIdOverride = null;
		bool sweepStaleTempWorkspaces = true;
		int staleWorkspaceHours = 12;
		int playGoChunkCount = 1;
		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
			case "--out":
				if (i + 1 < args.Length)
				{
					text2 = args[++i];
					continue;
				}
				break;
			case "--passcode":
				if (i + 1 < args.Length)
				{
					text6 = args[++i];
					flag = true;
					continue;
				}
				break;
			case "--content-id":
				if (i + 1 < args.Length)
				{
					text3 = args[++i];
					continue;
				}
				break;
			case "--gp5":
				if (i + 1 < args.Length)
				{
					text4 = args[++i];
					continue;
				}
				break;
			case "--type":
				if (i + 1 < args.Length)
				{
					text5 = args[++i].ToLowerInvariant();
					continue;
				}
				break;
			case "--verbose":
			case "-v":
				enabled = true;
				continue;
			case "--compression":
				if (i + 1 < args.Length)
				{
					ProsperoInnerCompressionMode prosperoInnerCompressionMode;
					switch (args[++i].ToLowerInvariant())
					{
					case "stored":
						prosperoInnerCompressionMode = ProsperoInnerCompressionMode.Stored;
						break;
					case "kraken":
					case "auto":
						prosperoInnerCompressionMode = ProsperoInnerCompressionMode.Auto;
						break;
					default:
						throw new ArgumentException("--compression must be 'stored' or 'kraken'.");
					}
					compression = prosperoInnerCompressionMode;
					continue;
				}
				break;
			case "--kraken-level":
				if (i + 1 < args.Length)
				{
					krakenLevel = ParseIntOption("--kraken-level", args[++i]);
					continue;
				}
				break;
			case "--kraken-threads":
				if (i + 1 < args.Length)
				{
					krakenThreads = ParseIntOption("--kraken-threads", args[++i]);
					continue;
				}
				break;
			case "--seed":
				if (i + 1 < args.Length)
				{
					array = ParseSeedOption(args[++i]);
					continue;
				}
				break;
			case "--sdk":
				if (i + 1 < args.Length)
				{
					sdkVersionOverride = ParseSdkOption(args[++i]);
					continue;
				}
				break;
			case "--drm-type":
				if (i + 1 < args.Length)
				{
					drmTypeOverride = ParseDrmTypeOption(args[++i]);
					continue;
				}
				break;
			case "--no-temp-sweep":
				sweepStaleTempWorkspaces = false;
				continue;
			case "--stale-hours":
				if (i + 1 < args.Length)
				{
					staleWorkspaceHours = ParseIntOption("--stale-hours", args[++i]);
					continue;
				}
				break;
			case "--temp":
				if (i + 1 < args.Length)
				{
					tempDirectory = args[++i];
					continue;
				}
				break;
			case "--playgo-chunks":
				if (i + 1 < args.Length)
				{
					playGoChunkCount = ParseIntOption("--playgo-chunks", args[++i]);
					continue;
				}
				break;
			case "--title":
				if (i + 1 < args.Length)
				{
					titleOverride = args[++i];
					continue;
				}
				break;
			case "--title-id":
				if (i + 1 < args.Length)
				{
					titleIdOverride = args[++i];
					continue;
				}
				break;
			case "--no_passcode":
				flag2 = true;
				continue;
			case "--no_fself":
			case "--no-fake-sign":
			case "--no-fake-sign-modules":
				fakeSignModules = false;
				continue;
			case "--no-right-sprx":
			case "--no_right_sprx":
				injectRightSprx = false;
				continue;
			}
			if (args[i].StartsWith("--", StringComparison.Ordinal))
			{
				return UnknownOption(args[i]);
			}
			if (text == null)
			{
				text = args[i];
			}
		}
		if (text == null || text2 == null)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool img_create <source-folder | project.gp5> --out <package.pkg> [--type base] [--compression stored|kraken] [--kraken-level -4..9] [--kraken-threads <n|0=auto>] [--seed <32-hex>] [--sdk <major|0x-id>] [--drm-type <token>] [--no-temp-sweep] [--stale-hours <n>] [--temp <dir>] [--playgo-chunks <n>] [--title <name>] [--title-id <id>] [--content-id <id>] [--passcode <32 chars>|--no_passcode] [--no_fself] [--no_right_sprx] [--verbose]");
			return 2;
		}
		ProsperoConsoleLog prosperoConsoleLog = sharedLog ?? new ProsperoConsoleLog();
		ConsoleProgress consoleProgress = new ConsoleProgress(enabled, prosperoConsoleLog);
		if (flag2)
		{
			text6 = "00000000000000000000000000000000";
			flag = false;
		}
		string fullPath = Path.GetFullPath(text);
		int num;
		if (File.Exists(fullPath))
		{
			num = (fullPath.EndsWith(".gp5", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
			if (num != 0)
			{
				goto IL_070d;
			}
		}
		else
		{
			num = 0;
		}
		if (!Directory.Exists(fullPath))
		{
			throw new DirectoryNotFoundException("Source folder or GP5 manifest not found: " + fullPath);
		}
		goto IL_070d;
		IL_070d:
		Gp5Project gp5Project = null;
		IReadOnlyList<Gp5ManifestFile> readOnlyList = null;
		byte[] array2;
		ProsperoParam prosperoParam;
		if (num != 0)
		{
			if (text4 != null && !Path.GetFullPath(text4).Equals(fullPath, StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("In GP5-manifest mode the manifest is the PlayGo source; --gp5 must not name a different file.");
			}
			text4 = fullPath;
			gp5Project = Gp5Xml.Read(fullPath);
			ValidateGp5OrThrow(gp5Project, fullPath);
			readOnlyList = Gp5Manifest.Resolve(gp5Project, fullPath);
			array2 = File.ReadAllBytes((readOnlyList.FirstOrDefault((Gp5ManifestFile file) => file.DestinationPath.Equals("sce_sys/param.json", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("The GP5 manifest does not list sce_sys/param.json.")).SourceFullPath);
			prosperoParam = ProsperoParamReader.Read(array2);
			if (!flag && IsValidPasscode(gp5Project.Volume.Package.Passcode))
			{
				text6 = gp5Project.Volume.Package.Passcode;
			}
		}
		else
		{
			SourceFolderInspection sourceFolderInspection = SourceFolderValidator.Inspect(fullPath);
			if ((object)sourceFolderInspection.Param == null)
			{
				PrintValidation(sourceFolderInspection.Validation);
				return 4;
			}
			prosperoParam = sourceFolderInspection.Param;
			array2 = File.ReadAllBytes(Path.Combine(fullPath, "sce_sys", "param.json"));
		}
		string text7 = ((prosperoParam.ApplicationCategoryType == 0) ? "base" : "dlc");
		string text8 = text5 ?? text7;
		if (text8 != "base")
		{
			bool flag3;
			switch (text8)
			{
			case "dlc":
				throw new InvalidDataException("DLC-data creation is not enabled: no clean-room CNT/GP5 oracle proves its category and required metadata.");
			case "dlc-nodata":
			case "dlc_nodata":
				flag3 = true;
				break;
			default:
				flag3 = false;
				break;
			}
			if (flag3)
			{
				throw new InvalidDataException("No-data DLC creation is not enabled: its PFS/CNT absence and entitlement semantics are unverified.");
			}
			throw new ArgumentException("--type must be 'base'. DLC categories are intentionally not guessed.");
		}
		if (text3 == null)
		{
			text3 = prosperoParam.ContentId;
		}
		if (text3 == null || text3.Length != 36)
		{
			throw new ArgumentException("A 36-character content id is required (supply --content-id or set it in sce_sys/param.json).");
		}
		if (text6.Length != 32)
		{
			throw new ArgumentException("Passcode must be exactly 32 characters.");
		}
		PlayGoProject playGo = null;
		if (gp5Project != null)
		{
			ValidateGp5ContentId(gp5Project, text3);
			playGo = PlayGoProjectFactory.FromGp5(gp5Project, text3);
		}
		else if (text4 != null)
		{
			Gp5Project gp = Gp5Xml.Read(text4);
			ValidateGp5OrThrow(gp, text4);
			ValidateGp5ContentId(gp, text3);
			playGo = PlayGoProjectFactory.FromGp5(gp, text3);
		}
		if (sharedLog == null)
		{
			prosperoConsoleLog.Write("Build started");
			prosperoConsoleLog.Write("Source: " + fullPath);
			prosperoConsoleLog.Write("Output: " + text2);
		}
		prosperoConsoleLog.Write("Building debug base/application package: " + text3);
		List<ProsperoInnerImageAssembler.InnerFile> list = new List<ProsperoInnerImageAssembler.InnerFile>();
		if (readOnlyList != null)
		{
			foreach (Gp5ManifestFile item in readOnlyList)
			{
				if (!IsExcludedFromInner(item.DestinationPath) && !SourceFolderValidator.IsPublishingArtifact(item.DestinationPath))
				{
					list.Add(CreateInnerFile(item.DestinationPath, item.SourceFullPath, AssignmentForInnerPath(playGo, item.DestinationPath)));
				}
			}
		}
		else
		{
			foreach (string item2 in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories).OrderBy((string p) => p, StringComparer.Ordinal))
			{
				string fileName = Path.GetFileName(item2);
				if (!fileName.EndsWith(".gp4", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".gp5", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
				{
					string relativePath = Path.GetRelativePath(fullPath, item2).Replace('\\', '/');
					if (!IsExcludedFromInner(relativePath))
					{
						list.Add(CreateInnerFile(relativePath, item2, AssignmentForInnerPath(playGo, relativePath)));
					}
				}
			}
		}
		IReadOnlyDictionary<string, byte[]> sceSysFiles = CollectSceSysFiles(fullPath, readOnlyList);
		DebugPackageBuildLog log = new DebugPackageBuildLog
		{
			Progress = consoleProgress,
			Log = prosperoConsoleLog.Write,
			Finish = consoleProgress.Finish
		};
		DebugPackageBuildResult debugPackageBuildResult = ProsperoDebugPackageBuilder.Build(list, new DebugPackageBuildOptions
		{
			OutputPath = text2,
			ContentId = text3,
			Passcode = text6,
			ParamJson = array2,
			Compression = compression,
			KrakenLevel = krakenLevel,
			KrakenThreads = krakenThreads,
			OuterSeed = array,
			DeterministicEntryKeys = (array != null),
			SdkVersionOverride = sdkVersionOverride,
			DrmTypeOverride = drmTypeOverride,
			TitleOverride = titleOverride,
			TitleIdOverride = titleIdOverride,
			SweepStaleTempWorkspaces = sweepStaleTempWorkspaces,
			StaleWorkspaceHours = staleWorkspaceHours,
			TempDirectory = tempDirectory,
			PlayGo = playGo,
			PlayGoChunkCount = playGoChunkCount,
			SceSysFiles = sceSysFiles,
			FakeSignModules = fakeSignModules,
			InjectRightSprx = injectRightSprx,
			Log = log,
			CancellationToken = cancellationToken
		});
		if (sharedLog == null)
		{
			prosperoConsoleLog.Write($"Build finished in {prosperoConsoleLog.Elapsed}; output {debugPackageBuildResult.PackageLength:N0} bytes.");
		}
		return 0;
	}

	private static bool IsExcludedFromInner(string relativePath)
	{
		if (!relativePath.StartsWith("sce_sys/", StringComparison.Ordinal))
		{
			return false;
		}
		return ProsperoSceSysMedia.IsOuterCntFile(relativePath.Substring("sce_sys/".Length));
	}

	private static byte[] RepairSceSysUcp(string relativePath, byte[] data)
	{
		if (!relativePath.StartsWith("sce_sys/", StringComparison.Ordinal) || !ProsperoUcpArchive.IsRepairableSceSysPath(relativePath.Substring("sce_sys/".Length)))
		{
			return data;
		}
		return ProsperoUcpArchive.RepairIfNeeded(data);
	}

	private static IReadOnlyDictionary<string, byte[]> CollectSceSysFiles(string sourceFolder, IReadOnlyList<Gp5ManifestFile>? manifestFiles)
	{
		Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		if (manifestFiles != null)
		{
			foreach (Gp5ManifestFile manifestFile in manifestFiles)
			{
				if (manifestFile.DestinationPath.StartsWith("sce_sys/", StringComparison.OrdinalIgnoreCase))
				{
					dictionary[manifestFile.DestinationPath.Substring("sce_sys/".Length)] = RepairSceSysUcp(manifestFile.DestinationPath, File.ReadAllBytes(manifestFile.SourceFullPath));
				}
			}
			return dictionary;
		}
		string text = Path.Combine(sourceFolder, "sce_sys");
		if (Directory.Exists(text))
		{
			foreach (string item in Directory.EnumerateFiles(text, "*", SearchOption.AllDirectories))
			{
				string text2 = Path.GetRelativePath(text, item).Replace('\\', '/');
				dictionary[text2] = (ProsperoUcpArchive.IsRepairableSceSysPath(text2) ? ProsperoUcpArchive.RepairIfNeeded(File.ReadAllBytes(item)) : File.ReadAllBytes(item));
			}
		}
		return dictionary;
	}

	private static byte AssignmentForInnerPath(PlayGoProject? playGo, string relativePath)
	{
		if (playGo == null)
		{
			return 0;
		}
		string key = ProsperoPlayGoRecordBuilder.NormalizePath(relativePath);
		if (!playGo.FileChunkAssignments.TryGetValue(key, out var value))
		{
			return 0;
		}
		return value;
	}

	private static ProsperoInnerImageAssembler.InnerFile CreateInnerFile(string relativePath, string sourceFullPath, byte playGoChunkId)
	{
		if (relativePath.StartsWith("sce_sys/", StringComparison.Ordinal) && ProsperoUcpArchive.IsRepairableSceSysPath(relativePath.Substring("sce_sys/".Length)))
		{
			return new ProsperoInnerImageAssembler.InnerFile
			{
				Path = "/" + relativePath,
				Data = ProsperoUcpArchive.RepairIfNeeded(File.ReadAllBytes(sourceFullPath)),
				PlayGoChunkId = playGoChunkId
			};
		}
		return new ProsperoInnerImageAssembler.InnerFile
		{
			Path = "/" + relativePath,
			SourcePath = sourceFullPath,
			Length = new FileInfo(sourceFullPath).Length,
			PlayGoChunkId = playGoChunkId
		};
	}

	private static void ValidateGp5OrThrow(Gp5Project gp5, string path)
	{
		ValidationReport validationReport = Gp5Validator.Validate(gp5, path);
		if (validationReport.IsValid)
		{
			return;
		}
		ValidationIssue validationIssue = validationReport.Issues.First((ValidationIssue i) => i.Severity == ValidationSeverity.Error);
		throw new InvalidDataException("GP5 validation failed: " + validationIssue.Code + ": " + validationIssue.Message);
	}

	private static void ValidateGp5ContentId(Gp5Project gp5, string contentId)
	{
		if (!string.IsNullOrWhiteSpace(gp5.Volume.Package.ContentId) && !string.Equals(gp5.Volume.Package.ContentId, contentId, StringComparison.Ordinal))
		{
			throw new ArgumentException("GP5 content_id does not match the package content ID.");
		}
	}

	private static bool IsValidPasscode(string? passcode)
	{
		if (passcode != null && passcode.Length == 32)
		{
			return passcode.All((char character) => character >= ' ' && character <= '~');
		}
		return false;
	}

	private static int RunInnerImageExtract(string[] args)
	{
		string text = null;
		string text2 = null;
		string text3 = null;
		string text4 = null;
		string referencePackagePath = null;
		bool flag = false;
		bool flag2 = false;
		bool enabled = false;
		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
			case "--passcode":
				if (i + 1 < args.Length)
				{
					text4 = args[++i];
					continue;
				}
				break;
			case "--no_passcode":
				flag = true;
				continue;
			case "--mount":
				flag2 = true;
				continue;
			case "--verbose":
			case "-v":
				enabled = true;
				continue;
			case "--tmp_path":
				if (i + 1 < args.Length)
				{
					i++;
					continue;
				}
				break;
			case "--ref_pkg_path":
				if (i + 1 < args.Length)
				{
					referencePackagePath = args[++i];
					continue;
				}
				break;
			}
			if (args[i].StartsWith("--", StringComparison.Ordinal))
			{
				return UnknownOption(args[i]);
			}
			if (text == null)
			{
				(text, text2) = SplitPackageSelection(args[i]);
			}
			else if (text3 == null)
			{
				text3 = args[i];
			}
		}
		if (text == null || text3 == null)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool img_extract [--mount] [--ref_pkg_path <reference.pkg>] [--passcode <passcode>|--no_passcode] <package[:inner-path]> <out_path>");
			return 2;
		}
		ConsoleProgress consoleProgress = new ConsoleProgress(enabled);
		if (ProsperoPackageReader.Read(text).Kind == ProsperoPackageKind.FinalizedDebug && ProsperoPackageContent.RequiresFileBacked(text, flag ? null : text4))
		{
			return RunInnerImageExtractFileBacked(text, text2, text3, flag ? null : text4, flag2, consoleProgress);
		}
		ProsperoDecodedPackage prosperoDecodedPackage = ProsperoPackageContent.Read(text, flag ? null : text4, referencePackagePath);
		if (flag2)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(text3)));
			File.WriteAllBytes(text3, prosperoDecodedPackage.Mount);
			Console.WriteLine("Wrote uncompressed inner mount: " + text3);
			return 0;
		}
		IReadOnlyList<ProsperoInnerPfsReader.Entry> source = ProsperoInnerPfsReader.Enumerate(prosperoDecodedPackage.Mount);
		string normalized = NormalizeInnerSelection(text2);
		ProsperoInnerPfsReader.Entry entry = source.FirstOrDefault((ProsperoInnerPfsReader.Entry e) => string.Equals(e.Path, normalized, StringComparison.Ordinal));
		bool hasUroot = source.Any((ProsperoInnerPfsReader.Entry e) => e.Path == "/uroot");
		IReadOnlyList<ProsperoInnerPfsReader.Entry> readOnlyList;
		if (text2 == null)
		{
			readOnlyList = source.Where((ProsperoInnerPfsReader.Entry e) => !e.IsDirectory && (!hasUroot || e.Path.StartsWith("/uroot/", StringComparison.Ordinal))).ToArray();
		}
		else
		{
			readOnlyList = (((object)entry != null && !entry.IsDirectory) ? new ProsperoInnerPfsReader.Entry[1] { entry } : source.Where((ProsperoInnerPfsReader.Entry e) => !e.IsDirectory && (e.Path == normalized || e.Path.StartsWith(normalized + "/", StringComparison.Ordinal))).ToArray());
		}
		if (text2 != null && readOnlyList.Count == 0)
		{
			throw new InvalidDataException("Inner path '" + text2 + "' does not exist.");
		}
		if ((object)entry != null && !entry.IsDirectory)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(text3)));
			File.WriteAllBytes(text3, ProsperoInnerPfsReader.ReadFile(prosperoDecodedPackage.Mount, entry));
			Console.WriteLine("Extracted " + entry.Path + " -> " + text3);
			return 0;
		}
		Directory.CreateDirectory(text3);
		string text5 = ((text2 == null) ? "/uroot/" : (normalized.TrimEnd('/') + "/"));
		int num = 0;
		foreach (ProsperoInnerPfsReader.Entry item in readOnlyList)
		{
			string text6 = (item.Path.StartsWith(text5, StringComparison.Ordinal) ? item.Path.Substring(text5.Length) : item.Path.TrimStart('/'));
			if (text6.Length != 0)
			{
				string fullPath = Path.GetFullPath(Path.Combine(text3, text6.Replace('/', Path.DirectorySeparatorChar)));
				if (!fullPath.StartsWith(Path.GetFullPath(text3).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("Inner extraction path escapes the output directory.");
				}
				Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
				File.WriteAllBytes(fullPath, ProsperoInnerPfsReader.ReadFile(prosperoDecodedPackage.Mount, item));
				num++;
				consoleProgress.Report(num, readOnlyList.Count, item.Path);
			}
		}
		consoleProgress.Finish();
		Console.WriteLine($"Extracted {num} file(s) to {text3}");
		return 0;
	}

	private static int RunInnerImageExtractFileBacked(string package, string? selection, string output, string? passcode, bool mountMode, ConsoleProgress console)
	{
		using ProsperoFileBackedPackage prosperoFileBackedPackage = ProsperoPackageContent.ReadFileBacked(package, passcode);
		if (mountMode)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
			using Stream stream = prosperoFileBackedPackage.OpenMount();
			using FileStream destination = File.Create(output);
			stream.CopyTo(destination, 1048576);
			Console.WriteLine("Wrote uncompressed inner mount: " + output);
			return 0;
		}
		IReadOnlyList<ProsperoInnerPfsReader.Entry> entries = prosperoFileBackedPackage.Entries;
		string normalized = NormalizeInnerSelection(selection);
		ProsperoInnerPfsReader.Entry entry = entries.FirstOrDefault((ProsperoInnerPfsReader.Entry e) => string.Equals(e.Path, normalized, StringComparison.Ordinal));
		bool hasUroot = entries.Any((ProsperoInnerPfsReader.Entry e) => e.Path == "/uroot");
		IReadOnlyList<ProsperoInnerPfsReader.Entry> readOnlyList;
		if (selection == null)
		{
			readOnlyList = entries.Where((ProsperoInnerPfsReader.Entry e) => !e.IsDirectory && (!hasUroot || e.Path.StartsWith("/uroot/", StringComparison.Ordinal))).ToArray();
		}
		else
		{
			readOnlyList = (((object)entry != null && !entry.IsDirectory) ? new ProsperoInnerPfsReader.Entry[1] { entry } : entries.Where((ProsperoInnerPfsReader.Entry e) => !e.IsDirectory && (e.Path == normalized || e.Path.StartsWith(normalized + "/", StringComparison.Ordinal))).ToArray());
		}
		if (selection != null && readOnlyList.Count == 0)
		{
			throw new InvalidDataException("Inner path '" + selection + "' does not exist.");
		}
		using Stream mount = prosperoFileBackedPackage.OpenMount();
		if ((object)entry != null && !entry.IsDirectory)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
			using FileStream destination2 = File.Create(output);
			ProsperoInnerPfsReader.ExtractFile(mount, entry, destination2);
			Console.WriteLine("Extracted " + entry.Path + " -> " + output);
			return 0;
		}
		Directory.CreateDirectory(output);
		string text = ((selection == null) ? "/uroot/" : (normalized.TrimEnd('/') + "/"));
		int num = 0;
		foreach (ProsperoInnerPfsReader.Entry item in readOnlyList)
		{
			string text2 = (item.Path.StartsWith(text, StringComparison.Ordinal) ? item.Path.Substring(text.Length) : item.Path.TrimStart('/'));
			if (text2.Length != 0)
			{
				string fullPath = Path.GetFullPath(Path.Combine(output, text2.Replace('/', Path.DirectorySeparatorChar)));
				if (!fullPath.StartsWith(Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("Inner extraction path escapes the output directory.");
				}
				Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
				using FileStream destination3 = File.Create(fullPath);
				ProsperoInnerPfsReader.ExtractFile(mount, item, destination3);
				num++;
				console.Report(num, readOnlyList.Count, item.Path);
			}
		}
		console.Finish();
		Console.WriteLine($"Extracted {num} file(s) to {output}");
		return 0;
	}

	private static int RunInnerImageFileList(string[] args)
	{
		string text = null;
		string passcode = null;
		string selection = null;
		string referencePackagePath = null;
		bool flag = false;
		for (int i = 0; i < args.Length; i++)
		{
			if (args[i] == "--passcode" && i + 1 < args.Length)
			{
				passcode = args[++i];
			}
			else if (args[i] == "--path" && i + 1 < args.Length)
			{
				selection = args[++i];
			}
			else if (args[i] == "--ref_pkg_path" && i + 1 < args.Length)
			{
				referencePackagePath = args[++i];
			}
			else if (args[i] == "--repair-offsets")
			{
				flag = true;
			}
			else if (!(args[i] == "--no_passcode"))
			{
				if (args[i].StartsWith("--", StringComparison.Ordinal))
				{
					return UnknownOption(args[i]);
				}
				if (text == null)
				{
					text = args[i];
				}
			}
		}
		if (text == null)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool img_file_list [--ref_pkg_path <reference.pkg>] [--passcode <passcode>|--no_passcode] [--path <inner-path>] [--repair-offsets] <package>");
			return 2;
		}
		ProsperoPackageInspection prosperoPackageInspection = ProsperoPackageReader.Read(text);
		try
		{
			bool num = prosperoPackageInspection.Kind == ProsperoPackageKind.FinalizedDebug && ProsperoPackageContent.RequiresFileBacked(text, passcode);
			string prefix = NormalizeInnerSelection(selection);
			if (flag)
			{
				Console.Error.WriteLine("Note: reconstructing truncated 64-bit directory offsets for listing; file data offsets may remain truncated.");
			}
			if (num)
			{
				using (ProsperoFileBackedPackage prosperoFileBackedPackage = ProsperoPackageContent.ReadFileBacked(text, passcode, flag))
				{
					foreach (ProsperoInnerPfsReader.Entry item in prosperoFileBackedPackage.Entries.Where((ProsperoInnerPfsReader.Entry e) => e.Path == prefix || e.Path.StartsWith(prefix + "/", StringComparison.Ordinal)))
					{
						Console.WriteLine($"{(item.IsDirectory ? "dir " : "file"),-4} {item.Size,10} {item.Path}");
					}
					return 0;
				}
			}
			foreach (ProsperoInnerPfsReader.Entry item2 in from e in ProsperoInnerPfsReader.Enumerate(ProsperoPackageContent.Read(text, passcode, referencePackagePath).Mount, flag)
				where e.Path == prefix || e.Path.StartsWith(prefix + "/", StringComparison.Ordinal)
				select e)
			{
				Console.WriteLine($"{(item2.IsDirectory ? "dir " : "file"),-4} {item2.Size,10} {item2.Path}");
			}
			return 0;
		}
		catch (Exception ex) when ((ex is IOException || ex is InvalidDataException || ex is CryptographicException) ? true : false)
		{
			bool flag2 = IsEncryptionRequired(ex);
			if (prosperoPackageInspection.Kind == ProsperoPackageKind.FinalizedPatchDebug || !flag2)
			{
				Console.Error.WriteLine("Error: " + ex.Message);
				return 4;
			}
			Console.WriteLine("Encrypted payload: key required");
			Console.WriteLine("Structural CNT entries:");
			foreach (ProsperoCntEntry entry in prosperoPackageInspection.Entries)
			{
				Console.WriteLine($"  0x{entry.Id:X8} {entry.DisplayName} ({entry.DataSize} bytes)");
			}
			return (!prosperoPackageInspection.Structure.IsValid) ? 4 : 0;
		}
	}

	private static bool IsEncryptionRequired(Exception ex)
	{
		if (!(ex is CryptographicException))
		{
			return ex.Message.Contains("Encrypted payload", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static int RunInnerImageVerify(string[] args)
	{
		string text = null;
		string passcode = null;
		string referencePackagePath = null;
		bool flag = false;
		VerificationPolicy result = VerificationPolicy.DebugPackage;
		for (int i = 0; i < args.Length; i++)
		{
			if (args[i] == "--passcode" && i + 1 < args.Length)
			{
				passcode = args[++i];
			}
			else if (args[i] == "--ref_pkg_path" && i + 1 < args.Length)
			{
				referencePackagePath = args[++i];
			}
			else if (args[i] == "--policy" && i + 1 < args.Length)
			{
				if (!Enum.TryParse<VerificationPolicy>(args[++i], ignoreCase: true, out result))
				{
					Console.Error.WriteLine("Unknown --policy. Use Structural, DebugPackage, BuildAcceptance or FullAvailableChecks.");
					return 2;
				}
			}
			else
			{
				if (args[i] == "--no_passcode")
				{
					continue;
				}
				if (args[i] == "--strict")
				{
					flag = true;
					continue;
				}
				if (args[i].StartsWith("--", StringComparison.Ordinal))
				{
					return UnknownOption(args[i]);
				}
				if (text == null)
				{
					text = args[i];
				}
			}
		}
		if (text == null)
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool img_verify [--ref_pkg_path <reference.pkg>] [--passcode <passcode>|--no_passcode] [--policy <Structural|DebugPackage|BuildAcceptance|FullAvailableChecks>] [--strict] <package>");
			return 2;
		}
		ProsperoPackageVerificationReport prosperoPackageVerificationReport = ProsperoPackageVerifier.Verify(text, passcode, referencePackagePath, result);
		foreach (VerificationCheck check in prosperoPackageVerificationReport.Checks)
		{
			Console.WriteLine($"{(check.Required ? "*" : " ")}{check.State.ToString().ToUpperInvariant(),-11} {check.Name}: {check.Detail}");
		}
		int value = prosperoPackageVerificationReport.Checks.Count((VerificationCheck c) => c.State == VerificationState.Pass);
		int value2 = prosperoPackageVerificationReport.Checks.Count((VerificationCheck c) => c.State == VerificationState.Fail);
		int value3 = prosperoPackageVerificationReport.Checks.Count((VerificationCheck c) => c.State == VerificationState.Unavailable);
		int value4 = prosperoPackageVerificationReport.Checks.Count((VerificationCheck c) => c.State == VerificationState.Unsupported);
		int value5 = prosperoPackageVerificationReport.Checks.Count((VerificationCheck c) => c.State == VerificationState.Error);
		int value6 = prosperoPackageVerificationReport.Checks.Count((VerificationCheck c) => c.State == VerificationState.NotPresent);
		Console.WriteLine($"Summary ({result}): {value} pass, {value2} fail, {value5} error, {value3} unavailable, {value4} unsupported, {value6} not present.");
		Console.WriteLine("Outcome: " + prosperoPackageVerificationReport.Outcome.ToString().ToUpperInvariant() + ".");
		if (prosperoPackageVerificationReport.Outcome == VerificationOutcome.Incomplete)
		{
			Console.WriteLine("Verification is incomplete: not all required checks were performed (use --strict to fail on this).");
		}
		if (prosperoPackageVerificationReport.HasFailure)
		{
			return 4;
		}
		if (!flag || prosperoPackageVerificationReport.Outcome != VerificationOutcome.Incomplete)
		{
			return 0;
		}
		return 6;
	}

	private static void PrintParamSummary(string packagePath, ProsperoPackageInspection package)
	{
		ProsperoCntEntry prosperoCntEntry = package.Entries.FirstOrDefault((ProsperoCntEntry e) => e.Id == 8192);
		if ((object)prosperoCntEntry == null)
		{
			Console.WriteLine("Title ID/name/version: unavailable (param.json absent)");
			return;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(ProsperoPackageContent.ReadCntEntry(packagePath, package, prosperoCntEntry));
			JsonElement root = jsonDocument.RootElement;
			Console.WriteLine("Title ID: " + Value("titleId"));
			Console.WriteLine("Title name: " + TitleName());
			Console.WriteLine("Application/package version: " + Value("contentVersion"));
			Console.WriteLine("Master version / SDK version / required system software: unavailable");
			string TitleName()
			{
				string text = Value("titleName");
				if (text != "(unavailable)")
				{
					return text;
				}
				if (root.TryGetProperty("localizedParameters", out var value) && value.ValueKind == JsonValueKind.Object)
				{
					string[] array = new string[2] { "default", "en-US" };
					foreach (string propertyName in array)
					{
						if (value.TryGetProperty(propertyName, out var value2) && value2.TryGetProperty("titleName", out var value3) && value3.ValueKind == JsonValueKind.String)
						{
							return value3.GetString() ?? "(unavailable)";
						}
					}
				}
				return "(unavailable)";
			}
			string Value(string property)
			{
				if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
				{
					return "(unavailable)";
				}
				return value.GetString() ?? "(unavailable)";
			}
		}
		catch (Exception)
		{
			Console.WriteLine("Title ID/name/version: unavailable (param.json unreadable)");
		}
	}

	private static (string Package, string? Selection) SplitPackageSelection(string argument)
	{
		int num = argument.IndexOf(':', 2);
		if (num >= 0)
		{
			return (Package: argument.Substring(0, num), Selection: argument.Substring(num + 1));
		}
		return (Package: argument, Selection: null);
	}

	private static string NormalizeInnerSelection(string? selection)
	{
		if (string.IsNullOrWhiteSpace(selection))
		{
			return "/uroot";
		}
		string text = selection.Replace('\\', '/').Trim();
		if (!text.StartsWith('/'))
		{
			text = "/" + text;
		}
		if (!text.StartsWith("/uroot", StringComparison.Ordinal))
		{
			text = "/uroot" + text;
		}
		if (text.Split('/', StringSplitOptions.RemoveEmptyEntries).Any((string s) => (s == "." || s == "..") ? true : false))
		{
			throw new InvalidDataException("Inner path must not contain '.' or '..'.");
		}
		return text.TrimEnd('/');
	}

	private static int UnknownOption(string option)
	{
		Console.Error.WriteLine("Unknown option: " + option);
		return 2;
	}

	private static int ParseIntOption(string option, string value)
	{
		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			throw new ArgumentException(option + " expects an integer but got '" + value + "'.");
		}
		return result;
	}

	private static byte[] ParseSeedOption(string value)
	{
		byte[] array;
		try
		{
			array = Convert.FromHexString(value);
		}
		catch (FormatException)
		{
			throw new ArgumentException("--seed must be 32 hexadecimal characters (16 bytes).");
		}
		if (array.Length != 16)
		{
			throw new ArgumentException("--seed must be 32 hexadecimal characters (16 bytes).");
		}
		return array;
	}

	private static ulong ParseSdkOption(string value)
	{
		if (!ProsperoSdkVersions.TryParse(value, out var executableVersion))
		{
			throw new ArgumentException("--sdk expects a major (9), a dotted version (9.00.00.40) or a 0x 64-bit identifier.");
		}
		return executableVersion;
	}

	private static string ParseDrmTypeOption(string value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Trim();
		}
		throw new ArgumentException("--drm-type expects a DRM token such as 'standard'.");
	}

	private static (byte[] InnerImage, byte[] Naps) ReadInnerImageAndNaps(string package, string? passcode, bool noPasscode)
	{
		ProsperoPackageInspection prosperoPackageInspection = ProsperoPackageReader.Read(package);
		ProsperoFihHeader fih = prosperoPackageInspection.Fih;
		if ((object)fih == null)
		{
			throw new InvalidDataException("Package has no FIH header; expected a finalized image.");
		}
		string contentId = prosperoPackageInspection.Cnt.ContentId;
		if (contentId.Length == 0)
		{
			throw new InvalidDataException("Package CNT has an empty content id.");
		}
		using FileStream stream = File.OpenRead(Path.GetFullPath(package));
		OuterPfsReader outerPfsReader = checked(OuterPfsReader.Open(stream, (long)fih.PfsOffset, (long)fih.PfsSize));
		(byte[] TweakKey, byte[] DataKey) tuple = ProsperoKeys.DeriveOuterPfsXtsKeys(ResolvePasscode(contentId, passcode), outerPfsReader.Seed);
		var (array, _) = tuple;
		using AesXts xts = new AesXts(tuple.DataKey, array);
		byte[] item = outerPfsReader.ReadNaps(xts);
		return (InnerImage: outerPfsReader.ReadInnerImage(xts), Naps: item);
	}

	private static byte[] ResolvePasscode(string contentId, string? passcode)
	{
		if (passcode != null)
		{
			return ProsperoKeys.ComputeEkpfs(contentId, passcode);
		}
		return ProsperoKeys.ComputeEkpfs(contentId, "00000000000000000000000000000000");
	}

	private static void PrintValidation(ValidationReport report)
	{
		foreach (ValidationIssue issue in report.Issues)
		{
			((issue.Severity == ValidationSeverity.Error) ? Console.Error : Console.Out).WriteLine($"{issue.Severity}: {issue.Code}: {issue.Message}{((issue.Path == null) ? string.Empty : (" [" + issue.Path + "]"))}");
		}
		Console.WriteLine($"Validation: {report.ErrorCount} error(s), {report.WarningCount} warning(s).");
	}

	private static int CountEntries(Gp5DirectoryEntry directory)
	{
		return directory.Children.Sum((Gp5ContentEntry child) => 1 + ((child is Gp5DirectoryEntry directory2) ? CountEntries(directory2) : 0));
	}

	private static int PrintFormats()
	{
		foreach (ContainerBackendDescriptor item in ContainerBackendCatalog.All)
		{
			Console.WriteLine($"{item.FormatId,-10} {item.Status,-12} {item.DisplayName} [{item.Capabilities}]");
		}
		return 0;
	}

	private static int RunCompare(string[] args)
	{
		if (args.Length != 2 || args.Any((string a) => a.StartsWith("--", StringComparison.Ordinal)))
		{
			Console.Error.WriteLine("Usage: ProsperoPkgTool compare <reference.pkg> <candidate.pkg>");
			return 2;
		}
		try
		{
			IReadOnlyList<string> readOnlyList = ProsperoContainerComparer.Compare(args[0], args[1]);
			if (readOnlyList.Count == 0)
			{
				Console.WriteLine("MATCH");
				return 0;
			}
			foreach (string item in readOnlyList)
			{
				Console.WriteLine(item);
			}
			return 1;
		}
		catch (Exception ex) when ((ex is IOException || ex is InvalidDataException || ex is ArgumentException) ? true : false)
		{
			Console.Error.WriteLine("Error: " + ex.Message);
			return 2;
		}
	}

	private static void PrintUsage()
	{
		Console.WriteLine("ProsperoPkgTool 0.3 - clean-room PS5 package toolkit (no Sony tools required)");
		Console.WriteLine();
		Console.WriteLine("Usage:");
		Console.WriteLine("  ProsperoPkgTool <command> [arguments]");
		Console.WriteLine();
		Console.WriteLine("General:");
		Console.WriteLine("  formats                 List supported/planned container backends");
		Console.WriteLine("GP5 and source:");
		Console.WriteLine("  gp5gen                  Generate a GP5 project from an unpacked title folder");
		Console.WriteLine("  gp5info                 Show GP5 project information");
		Console.WriteLine("  gp5validate             Validate a GP5 project");
		Console.WriteLine("  gp5format               Rewrite a GP5 to a separate file");
		Console.WriteLine("  source-validate         Validate a source folder (param.json, files)");
		Console.WriteLine("  playgo-info             Inspect a playgo-chunk.dat");
		Console.WriteLine("  fself <module.elf> --out <module.bin>");
		Console.WriteLine("                          Fake-sign a raw 64-bit ELF into a debug SELF container");
		Console.WriteLine("                          [--authority <hex64>] [--app-version <hex64>] [--fw-version <hex64>]");
		Console.WriteLine("                          [--sceversion-name <name> --sceversion-from <reference.elf>] [--force]");
		Console.WriteLine("Native package commands (clean-room, no Sony tool required):");
		Console.WriteLine("  pkg-detect              Identify finalized-debug, finalized-retail, or bare CNT");
		Console.WriteLine("  native-info             Read FIH/CNT metadata and segment geometry");
		Console.WriteLine("  native-validate         Validate unkeyed package structure and segment bounds");
		Console.WriteLine("  native-entries          List CNT metadata records");
		Console.WriteLine("  native-decrypt <pkg> --passcode <32 chars> [--out <inner-image.bin>]");
		Console.WriteLine("                          Derive keys, verify the superblock, decrypt the outer PFS");
		Console.WriteLine("  img_info <pkg>          Summarize package, container, and decoded inner-image state");
		Console.WriteLine("                          [--passcode <passcode>|--no_passcode] [--tmp_path <dir>]");
		Console.WriteLine("  img_file_list <pkg>     List logical inner-PFS paths; patches use --ref_pkg_path <base.pkg>");
		Console.WriteLine("                          [--passcode <passcode>|--no_passcode] [--path <inner-path>] [--repair-offsets]");
		Console.WriteLine("  img_extract <pkg> <out> Extract logical files; patches use --ref_pkg_path <base.pkg>");
		Console.WriteLine("                          --mount writes the reconstructed mount image for diagnostics");
		Console.WriteLine("                          [--passcode <passcode>|--no_passcode] [--tmp_path <dir>] [--verbose]");
		Console.WriteLine("  img_verify <pkg>        Run available structural, CNT, PFS, NAPS, and inner-PFS checks");
		Console.WriteLine("  img_create <src> --out <pkg>  Build a debug base/application package");
		Console.WriteLine("                          <src> is a source folder or a project.gp5 manifest (manifest mode)");
		Console.WriteLine("                          [--gp5 <project.gp5>] [--compression stored|kraken]");
		Console.WriteLine("                          [--content-id <id>] [--passcode <32 chars>|--no_passcode] [--no_fself] [--verbose]");
		Console.WriteLine("  convert <backup> --out <pkg>  Convert a decrypted backup into a debug base package");
		Console.WriteLine("                          Substitutes raw ELF modules from the decrypted subtree, fake-signs");
		Console.WriteLine("                          them, then reports module classification and launch-readiness");
		Console.WriteLine("                          [--decrypted <subfolder>] [--content-id <id>] [--passcode <32 chars>|--no_passcode]");
		Console.WriteLine("                          [--compression stored|kraken] [--drm-type <token>] [--title <name>]");
		Console.WriteLine("                          [--title-id <id>] [--sdk <major|0x-id>] [--staging <folder>]");
		Console.WriteLine("                          [--keep-staging] [--use-embedded-right-sprx]");
		Console.WriteLine("Parity:");
		Console.WriteLine("  compare <reference.pkg> <candidate.pkg>  Field-by-field container diff (exit 1 on differences)");
	}
}
