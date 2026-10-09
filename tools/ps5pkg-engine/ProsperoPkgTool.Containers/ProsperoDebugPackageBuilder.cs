using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ProsperoPkgTool.Content;
using ProsperoPkgTool.Crypto;
using ProsperoPkgTool.Gp5;

namespace ProsperoPkgTool.Containers;

public static class ProsperoDebugPackageBuilder
{
	public static DebugPackageBuildResult Build(IReadOnlyList<ProsperoInnerImageAssembler.InnerFile> files, DebugPackageBuildOptions options, CancellationToken cancellationToken = default(CancellationToken))
	{
		CancellationToken cancellationToken2 = (cancellationToken.CanBeCanceled ? cancellationToken : options.CancellationToken);
		cancellationToken2.ThrowIfCancellationRequested();
		ArgumentNullException.ThrowIfNull(files, "files");
		ArgumentNullException.ThrowIfNull(options, "options");
		if (files.Count == 0)
		{
			throw new ArgumentException("At least one inner file is required.", "files");
		}
		if (options.ContentId.Length != 36)
		{
			throw new ArgumentException("Content id must be exactly 36 characters.", "options");
		}
		if (options.Passcode.Length != 32)
		{
			throw new ArgumentException("Passcode must be exactly 32 characters.", "options");
		}
		byte[] outerSeed = options.OuterSeed;
		if (outerSeed != null && outerSeed.Length != 16)
		{
			throw new ArgumentException("OuterSeed must be 16 bytes when supplied.", "options");
		}
		DebugPackageBuildLog debugPackageBuildLog = options.Log ?? new DebugPackageBuildLog();
		long timestampSeconds = options.TimestampSeconds;
		uint timestampNanoseconds = options.TimestampNanoseconds;
		if (options.SweepStaleTempWorkspaces)
		{
			TempWorkspace.SweepOrphans(options.TempDirectory ?? Path.GetTempPath(), TimeSpan.FromHours(Math.Max(1, options.StaleWorkspaceHours)), debugPackageBuildLog.Log);
		}
		if (!options.SkipFreeSpaceCheck)
		{
			long num = 0L;
			foreach (ProsperoInnerImageAssembler.InnerFile file in files)
			{
				cancellationToken2.ThrowIfCancellationRequested();
				num += file.PayloadLength;
			}
			DiskSpaceReport diskSpaceReport = DiskSpaceGuard.Check(DiskSpaceGuard.Estimate(num, options.OutputPath, options.TempDirectory));
			if (diskSpaceReport.Status == DiskSpaceStatus.Insufficient)
			{
				throw new ProsperoInsufficientSpaceException(DiskSpaceGuard.Describe(diskSpaceReport), diskSpaceReport.RequiredBytes, diskSpaceReport.AvailableBytes, diskSpaceReport.Root);
			}
			if (diskSpaceReport.Status == DiskSpaceStatus.NearLimit)
			{
				debugPackageBuildLog.Log("  warning: " + DiskSpaceGuard.Describe(diskSpaceReport));
			}
		}
		ProsperoParam prosperoParam = ProsperoParamReader.Read(options.ParamJson);
		byte[] array = options.ParamJson;
		ulong? sdkVersionOverride = options.SdkVersionOverride;
		if (sdkVersionOverride.HasValue)
		{
			ulong valueOrDefault = sdkVersionOverride.GetValueOrDefault();
			array = ProsperoPublisherParamJson.ApplySdkOverride(array, valueOrDefault);
			debugPackageBuildLog.Log($"  SDK override: executable 0x{valueOrDefault:X16}, package 0x{ProsperoSdkVersions.ToPackageVersion(valueOrDefault):X16}");
		}
		if (!string.IsNullOrWhiteSpace(options.DrmTypeOverride))
		{
			array = ProsperoPublisherParamJson.ApplyDrmType(array, options.DrmTypeOverride);
			debugPackageBuildLog.Log("  DRM type override: " + options.DrmTypeOverride);
		}
		if (!string.IsNullOrWhiteSpace(options.TitleIdOverride))
		{
			array = ProsperoPublisherParamJson.ApplyTitleIdOverride(array, options.TitleIdOverride);
			debugPackageBuildLog.Log("  Title ID override: " + options.TitleIdOverride);
		}
		if (!string.IsNullOrWhiteSpace(options.TitleOverride))
		{
			array = ProsperoPublisherParamJson.ApplyTitleOverride(array, options.TitleOverride);
			debugPackageBuildLog.Log("  Title override: " + options.TitleOverride);
		}
		List<ProsperoInnerImageAssembler.InnerFile> list = new List<ProsperoInnerImageAssembler.InnerFile>(files);
		if (options.FakeSignModules)
		{
			FakeSignInnerModules(list, debugPackageBuildLog, options.SdkVersionOverride);
		}
		if (options.InjectPfsVersionDat && !list.Any((ProsperoInnerImageAssembler.InnerFile f) => f.Path == "/sce_sys/pfs-version.dat"))
		{
			list.Add(new ProsperoInnerImageAssembler.InnerFile
			{
				Path = "/sce_sys/pfs-version.dat",
				Data = Encoding.ASCII.GetBytes(prosperoParam.ContentVersion ?? "01.000.000"),
				PlayGoChunkId = 0
			});
		}
		if (options.InjectKeystone && !list.Any((ProsperoInnerImageAssembler.InnerFile f) => f.Path == "/sce_sys/keystone"))
		{
			list.Add(new ProsperoInnerImageAssembler.InnerFile
			{
				Path = "/sce_sys/keystone",
				Data = Keystone.Create(options.Passcode),
				PlayGoChunkId = 0
			});
		}
		if (options.InjectRightSprx && !list.Any((ProsperoInnerImageAssembler.InnerFile f) => f.Path == "/sce_sys/about/right.sprx"))
		{
			byte[] array2 = ProsperoRightSprx.Get();
			if (array2 != null && array2.Length > 0)
			{
				list.Add(new ProsperoInnerImageAssembler.InnerFile
				{
					Path = "/sce_sys/about/right.sprx",
					Data = array2,
					PlayGoChunkId = 0
				});
				debugPackageBuildLog.Log($"  Injected sce_sys/about/right.sprx ({array2.Length} bytes)");
			}
			else
			{
				debugPackageBuildLog.Log("  warning: sce_sys/about/right.sprx is absent and the embedded module is unavailable.");
			}
		}
		if (list.Count == 0)
		{
			throw new ArgumentException("No inner files were supplied.", "files");
		}
		IReadOnlyDictionary<string, byte[]> sceSysFiles = options.SceSysFiles ?? new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		PlayGoProject playGoProject = options.PlayGo ?? ResolvePlayGoProject(sceSysFiles, options.ContentId, options.PlayGoChunkCount, debugPackageBuildLog);
		long num2 = 0L;
		foreach (ProsperoInnerImageAssembler.InnerFile item in list)
		{
			cancellationToken2.ThrowIfCancellationRequested();
			num2 += item.PayloadLength;
		}
		if (num2 <= options.FileBackedInnerImageThreshold)
		{
			return RunInMemory(list, options, prosperoParam, array, sceSysFiles, playGoProject, timestampSeconds, timestampNanoseconds, debugPackageBuildLog, cancellationToken2);
		}
		return RunFileBacked(list, options, prosperoParam, array, sceSysFiles, playGoProject, timestampSeconds, timestampNanoseconds, debugPackageBuildLog, cancellationToken2);
	}

	private static PlayGoProject ResolvePlayGoProject(IReadOnlyDictionary<string, byte[]> sceSysFiles, string contentId, int chunkCount, DebugPackageBuildLog console)
	{
		if (!sceSysFiles.TryGetValue("playgo-chunk.dat", out byte[] value))
		{
			return ProsperoPlayGoChunkBuilder.CreateProject(contentId, chunkCount);
		}
		try
		{
			return PlayGoChunkReader.Read(value);
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is IOException) ? true : false)
		{
			console.Log("  warning: sce_sys/playgo-chunk.dat could not be parsed (" + ex.Message + "); using the generated PlayGo project.");
			return ProsperoPlayGoChunkBuilder.CreateProject(contentId, chunkCount);
		}
	}

	private static string? EffectiveDrmType(DebugPackageBuildOptions options, ProsperoParam param)
	{
		if (!string.IsNullOrWhiteSpace(options.DrmTypeOverride))
		{
			return options.DrmTypeOverride;
		}
		return param.ApplicationDrmType;
	}

	private static void ReportStage(DebugPackageBuildLog console, ProsperoBuildStage stage)
	{
		console.Progress?.Report(new ProsperoBuildProgress(ProsperoBuildStages.Name(stage), 1L, 1L)
		{
			StageId = stage
		});
	}

	private static void BeginStage(Stopwatch clock, DebugPackageBuildLog console, ProsperoBuildStage stage)
	{
		int num = ProsperoBuildStages.IndexOf(stage);
		console.Log($"[stage {num + 1}/{ProsperoBuildStages.Build.Count}] {StageAction(stage)}");
		clock.Restart();
	}

	private static void LogStage(Stopwatch clock, DebugPackageBuildLog console, ProsperoBuildStage stage, string detail = "")
	{
		int num = ProsperoBuildStages.IndexOf(stage);
		string value = ((detail.Length > 0) ? (": " + detail) : "");
		console.Log($"[stage {num + 1}/{ProsperoBuildStages.Build.Count}] {ProsperoBuildStages.Name(stage)} complete{value} in {clock.Elapsed:hh\\:mm\\:ss\\.fff}.");
	}

	private static string StageAction(ProsperoBuildStage stage)
	{
		return stage switch
		{
			ProsperoBuildStage.InnerImage => "Building and compressing the inner pfs_image.dat...",
			ProsperoBuildStage.Naps => "Generating the NAPS file, block and integrity tables...",
			ProsperoBuildStage.OuterPfs => "Building and AES-XTS encrypting the outer PFS...",
			ProsperoBuildStage.Cnt => "Writing the CNT bodies and outer image...",
			ProsperoBuildStage.Finalize => "Finalizing the CNT into a debug (FIH) image...",
			_ => ProsperoBuildStages.Name(stage) + "...",
		};
	}

	private static uint ContentVersionHigh(string? contentVersion)
	{
		if (string.IsNullOrWhiteSpace(contentVersion))
		{
			return 0u;
		}
		string text = contentVersion.Split('.')[0].Trim();
		int length = text.Length;
		bool flag = ((length > 2 || length == 0) ? true : false);
		if (flag || !byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
		{
			return 0u;
		}
		return (uint)(((result / 10 << 4) | (result % 10)) << 24);
	}

	private static (ProsperoPlayGoChunkBuildResult Chunk, byte[] HashTable, byte[] Ficm) BuildPlayGoFamily(string contentId, PlayGoProject project, ProsperoInnerImageAssembler.InnerImageResult inner, ulong fihPfsOffset, ulong fihCntOffset, DebugPackageBuildLog console)
	{
		try
		{
			return Build(project);
		}
		catch (InvalidDataException ex)
		{
			console.Log("  warning: PlayGo project is not usable (" + ex.Message + "); using the default single-chunk project.");
			return Build(ProsperoPlayGoChunkBuilder.CreateDefaultProject(contentId));
		}
		(ProsperoPlayGoChunkBuildResult, byte[], byte[]) Build(PlayGoProject p)
		{
			ProsperoPlayGoChunkBuildResult item = ProsperoPlayGoChunkBuilder.Build(contentId, p, inner, fihPfsOffset, fihCntOffset);
			IReadOnlyList<ProsperoPlayGoFileRecord> records = ProsperoPlayGoRecordBuilder.Build(inner, p.FileChunkAssignments);
			return (item, ProsperoPlayGoHashTableBuilder.Build(records), ProsperoPlayGoFicmBuilder.Build(records));
		}
	}

	private static DebugPackageBuildResult RunInMemory(List<ProsperoInnerImageAssembler.InnerFile> innerFiles, DebugPackageBuildOptions buildOptions, ProsperoParam param, byte[] paramJson, IReadOnlyDictionary<string, byte[]> sceSysFiles, PlayGoProject playGoProject, long tsSec, uint tsNsec, DebugPackageBuildLog console, CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		string outputPath = buildOptions.OutputPath;
		string contentId = buildOptions.ContentId;
		string passcode = buildOptions.Passcode;
		ProsperoInnerCompressionMode compression = buildOptions.Compression;
		int krakenLevel = buildOptions.KrakenLevel;
		int krakenThreads = buildOptions.KrakenThreads;
		uint contentVersionHi = ContentVersionHigh(param.ContentVersion);
		Stopwatch clock = Stopwatch.StartNew();
		BeginStage(clock, console, ProsperoBuildStage.InnerImage);
		ProsperoInnerImageAssembler.InnerImageResult innerImageResult = new ProsperoInnerImageAssembler(tsSec, tsNsec).Build(innerFiles, compression, console.Progress, krakenLevel, krakenThreads, cancellationToken);
		console.Finish();
		console.Log($" [inner] image {innerImageResult.Image.Length:N0} bytes, {innerImageResult.Ndblock:N0} mount blocks, {innerImageResult.InodeCount:N0} inodes");
		ProsperoCompressionMetrics compressionMetrics = innerImageResult.CompressionMetrics;
		console.Log($" [inner] payload {compressionMetrics.RawBytes:N0} -> {compressionMetrics.EncodedBytes:N0} bytes; Kraken {compressionMetrics.KrakenBlocks}, stored {compressionMetrics.StoredBlocks}, ratio {compressionMetrics.Ratio:P2}");
		LogStage(clock, console, ProsperoBuildStage.InnerImage);
		BeginStage(clock, console, ProsperoBuildStage.Naps);
		ProsperoNapsPlanGenerator.PlanResult planResult = ProsperoNapsPlanGenerator.Generate(innerImageResult);
		byte[] array = ProsperoNapsLayoutBuilder.Build(planResult.ToRequest((byte)((compression != ProsperoInnerCompressionMode.Stored) ? 2 : 0), 64));
		console.Log($"    layout {array.Length:N0} bytes");
		ReportStage(console, ProsperoBuildStage.Naps);
		LogStage(clock, console, ProsperoBuildStage.Naps);
		BeginStage(clock, console, ProsperoBuildStage.OuterPfs);
		byte[] ekpfs = ProsperoKeys.ComputeEkpfs(contentId, passcode);
		byte[] array2 = buildOptions.OuterSeed ?? RandomNumberGenerator.GetBytes(16);
		_ = ((long)innerImageResult.Image.Length + 65536L - 1) / 65536;
		List<OuterPfsFile> list = new List<OuterPfsFile>
		{
			new OuterPfsFile
			{
				Name = "pfs_image.dat",
				Data = innerImageResult.Image,
				SizeCompressed = innerImageResult.Ndblock * 65536,
				Signed = false
			},
			new OuterPfsFile
			{
				Name = "naps_pkg_layout.dat",
				Data = array,
				Signed = true
			}
		};
		OuterPfsBuildParameters parameters = new OuterPfsBuildParameters
		{
			TimestampSeconds = tsSec,
			TimestampNanoseconds = tsNsec,
			Seed = array2
		};
		OuterPfsBuildResult outerPfsBuildResult = ProsperoOuterPfsBuilder.BuildPlaintext(list, parameters, console.Progress, cancellationToken);
		var (digests, superblockIcv) = ProsperoOuterPfsBuilder.ComputeImageDigests(outerPfsBuildResult);
		ProsperoOuterPfsBuilder.Encrypt(outerPfsBuildResult, ProsperoKeys.DeriveOuterPfsXtsKeys(ekpfs, array2).TweakKey, ProsperoKeys.DeriveOuterPfsXtsKeys(ekpfs, array2).DataKey, encrypt: true, console.Progress, cancellationToken);
		console.Finish();
		byte[] plaintext = outerPfsBuildResult.Plaintext;
		byte[] pfsImageDigest = Sha3.Sha3_256(outerPfsBuildResult.Plaintext.AsSpan(outerPfsBuildResult.SuperblockIndex * 65536, 65536));
		console.Log($"    {plaintext.Length:N0} bytes, {outerPfsBuildResult.BlockCount:N0} blocks, superblock at block {outerPfsBuildResult.SuperblockIndex:N0}");
		LogStage(clock, console, ProsperoBuildStage.OuterPfs);
		BeginStage(clock, console, ProsperoBuildStage.Cnt);
		CntBuildParameters parameters2 = new CntBuildParameters
		{
			ContentId = contentId,
			Passcode = passcode,
			ContentType = 32u,
			ContentFlags = 33685504u
		};
		List<ProsperoCntBuilder.Entry> list2 = ProsperoCntBuilder.BuildStandardEntries(contentId, passcode, ekpfs, buildOptions.DeterministicEntryKeys);
		list2.Add(new ProsperoCntBuilder.Entry
		{
			Id = CntEntryId.ParamJson,
			Name = "param.json",
			Data = paramJson,
			Flags1 = ProsperoCntBuilder.Flags1For(CntEntryId.ParamJson)
		});
		list2.Add(new ProsperoCntBuilder.Entry
		{
			Id = CntEntryId.Imagedigs,
			Name = null,
			Data = ReverseDigests(digests),
			Flags1 = ProsperoCntBuilder.Flags1For(CntEntryId.Imagedigs)
		});
		ulong num = 65536uL;
		ulong fihCntOffset = checked(num + (ulong)plaintext.Length);
		var (prosperoPlayGoChunkBuildResult, data, data2) = BuildPlayGoFamily(contentId, playGoProject, innerImageResult, num, fihCntOffset, console);
		list2.Add(new ProsperoCntBuilder.Entry
		{
			Id = CntEntryId.PlaygoChunkDat,
			Name = "playgo-chunk.dat",
			Data = prosperoPlayGoChunkBuildResult.Data,
			Flags1 = ProsperoCntBuilder.Flags1For(CntEntryId.PlaygoChunkDat)
		});
		foreach (ProsperoSceSysMedia.Entry media in ProsperoSceSysMedia.Collect(sceSysFiles, EffectiveDrmType(buildOptions, param)))
		{
			if (!list2.Any((ProsperoCntBuilder.Entry e) => e.Id == media.Id))
			{
				list2.Add(new ProsperoCntBuilder.Entry
				{
					Id = media.Id,
					Name = media.Name,
					Data = media.Data,
					Flags1 = media.Flags1,
					Flags2 = media.Flags2
				});
			}
		}
		list2.Add(new ProsperoCntBuilder.Entry
		{
			Id = CntEntryId.PlaygoHashTable,
			Name = "playgo-hash-table.dat",
			Data = data,
			Flags1 = ProsperoCntBuilder.Flags1For(CntEntryId.PlaygoHashTable)
		});
		list2.Add(new ProsperoCntBuilder.Entry
		{
			Id = CntEntryId.PlaygoFicm,
			Name = "playgo-ficm.dat",
			Data = data2,
			Flags1 = ProsperoCntBuilder.Flags1For(CntEntryId.PlaygoFicm)
		});
		byte[] nestedImageDigest = Sha3.Sha3_256(array);
		long nestedMetaBaseBlocks = innerImageResult.MetaBaseLogical / 65536;
		int num2 = innerFiles.Count((ProsperoInnerImageAssembler.InnerFile f) => f.PayloadLength == 0);
		int num3 = planResult.FileLogicalOffsets.Count - 1;
		int metaBlockCountMirror = num3 + num2;
		byte[] pfsSignedDigest = Sha3.Sha3_256(ProsperoFihBuilder.BuildHeaderBlock(FihVariant.Debug, (ulong)plaintext.Length, (ulong)(65536 + plaintext.Length), plaintext, nestedImageDigest, array.Length, nestedMetaBaseBlocks, contentVersionHi, innerImageResult.InnerContentInodes, innerImageResult.AppFileCount, num3, metaBlockCountMirror, num2));
		byte[] array3 = ProsperoCntBuilder.Build(list2, parameters2, plaintext, array2, pfsImageDigest, nestedImageDigest, array.Length, pfsSignedDigest);
		console.Log($"    {array3.Length:N0} bytes");
		ReportStage(console, ProsperoBuildStage.Cnt);
		LogStage(clock, console, ProsperoBuildStage.Cnt);
		BeginStage(clock, console, ProsperoBuildStage.Finalize);
		byte[] fihNoSi = ProsperoFihBuilder.BuildFromCnt(array3, FihVariant.Debug, nestedImageDigest, array.Length, nestedMetaBaseBlocks, contentVersionHi, innerImageResult.InnerContentInodes, innerImageResult.AppFileCount, num3, metaBlockCountMirror, num2);
		byte[] playGoChunkDat = list2.FirstOrDefault((ProsperoCntBuilder.Entry e) => e.Id == CntEntryId.PlaygoChunkDat)?.Data;
		byte[] siSegment = BuildSiSegment(contentId, EffectiveDrmType(buildOptions, param) ?? "free", param, array3, list2, list, outerPfsBuildResult, array2, superblockIcv, innerImageResult, fihNoSi, paramJson, playGoChunkDat);
		byte[] array4 = ProsperoFihBuilder.BuildFromCnt(array3, FihVariant.Debug, nestedImageDigest, array.Length, nestedMetaBaseBlocks, contentVersionHi, innerImageResult.InnerContentInodes, innerImageResult.AppFileCount, num3, metaBlockCountMirror, num2, 0, siSegment);
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
		cancellationToken.ThrowIfCancellationRequested();
		File.WriteAllBytes(outputPath, array4);
		console.Log($"    wrote {outputPath} ({array4.Length:N0} bytes)");
		ReportStage(console, ProsperoBuildStage.Finalize);
		LogStage(clock, console, ProsperoBuildStage.Finalize);
		return new DebugPackageBuildResult(Path.GetFullPath(outputPath), array4.Length, contentId, innerFiles.Count, FileBacked: false);
	}

	private static DebugPackageBuildResult RunFileBacked(List<ProsperoInnerImageAssembler.InnerFile> innerFiles, DebugPackageBuildOptions buildOptions, ProsperoParam param, byte[] paramJson, IReadOnlyDictionary<string, byte[]> sceSysFiles, PlayGoProject playGoProject, long tsSec, uint tsNsec, DebugPackageBuildLog console, CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		string outputPath = buildOptions.OutputPath;
		string contentId = buildOptions.ContentId;
		string passcode = buildOptions.Passcode;
		ProsperoInnerCompressionMode compression = buildOptions.Compression;
		int krakenLevel = buildOptions.KrakenLevel;
		int krakenThreads = buildOptions.KrakenThreads;
		uint contentVersionHi = ContentVersionHigh(param.ContentVersion);
		Stopwatch clock = Stopwatch.StartNew();
		string text = TempWorkspace.Create(buildOptions.TempDirectory ?? Path.GetTempPath());
		try
		{
			string text2 = Path.Combine(text, "pfs_image.dat");
			string path = Path.Combine(text, "naps_pkg_layout.dat");
			BeginStage(clock, console, ProsperoBuildStage.InnerImage);
			ProsperoInnerImageAssembler prosperoInnerImageAssembler = new ProsperoInnerImageAssembler(tsSec, tsNsec);
			ProsperoInnerImageAssembler.InnerImageResult innerResult = prosperoInnerImageAssembler.BuildToFile(innerFiles, text2, compression, console.Progress, krakenLevel, krakenThreads, cancellationToken);
			console.Finish();
			console.Log($" [inner] image {innerResult.ImageSize:N0} bytes, {innerResult.Ndblock:N0} mount blocks, {innerResult.InodeCount:N0} inodes");
			ProsperoCompressionMetrics compressionMetrics = innerResult.CompressionMetrics;
			console.Log($"  Inner payload: {compressionMetrics.RawBytes} raw -> {compressionMetrics.EncodedBytes} encoded; Kraken {compressionMetrics.KrakenBlocks}, stored {compressionMetrics.StoredBlocks}, ratio {compressionMetrics.Ratio:P2}");
			LogStage(clock, console, ProsperoBuildStage.InnerImage);
			BeginStage(clock, console, ProsperoBuildStage.Naps);
			ProsperoNapsPlanGenerator.PlanResult planResult = ProsperoNapsPlanGenerator.Generate(innerResult);
			byte[] naps = ProsperoNapsLayoutBuilder.Build(planResult.ToRequest((byte)((compression != ProsperoInnerCompressionMode.Stored) ? 2 : 0), 64));
			File.WriteAllBytes(path, naps);
			console.Log($"    layout {naps.Length:N0} bytes");
			ReportStage(console, ProsperoBuildStage.Naps);
			LogStage(clock, console, ProsperoBuildStage.Naps);
			BeginStage(clock, console, ProsperoBuildStage.OuterPfs);
			byte[] ekpfs = ProsperoKeys.ComputeEkpfs(contentId, passcode);
			byte[] outerSeed = buildOptions.OuterSeed ?? RandomNumberGenerator.GetBytes(16);
			List<OuterPfsFileSource> outerSources = new List<OuterPfsFileSource>
			{
				new OuterPfsFileSource
				{
					Name = "pfs_image.dat",
					Path = text2,
					SizeCompressed = innerResult.Ndblock * 65536,
					Signed = false
				},
				new OuterPfsFileSource
				{
					Name = "naps_pkg_layout.dat",
					Path = path,
					Signed = true
				}
			};
			OuterPfsBuildParameters parameters = new OuterPfsBuildParameters
			{
				TimestampSeconds = tsSec,
				TimestampNanoseconds = tsNsec,
				Seed = outerSeed,
				BlockParallelism = krakenThreads
			};
			(byte[] TweakKey, byte[] DataKey) tuple = ProsperoKeys.DeriveOuterPfsXtsKeys(ekpfs, outerSeed);
			byte[] item = tuple.TweakKey;
			byte[] item2 = tuple.DataKey;
			OuterPfsFileBuildResult outerBuild = ProsperoOuterPfsBuilder.BuildForPackageToFile(outerSources, parameters, outputPath, encryptOutput: true, item, item2, console.Progress, 65536L, cancellationToken);
			console.Finish();
			byte[] pfsImageDigest = Sha3.Sha3_256(outerBuild.SuperblockPlaintext);
			long outerLength = outerBuild.ImageLength;
			console.Log($"    {outerLength:N0} bytes, {outerBuild.BlockCount:N0} blocks, superblock at block {outerBuild.SuperblockIndex:N0}");
			LogStage(clock, console, ProsperoBuildStage.OuterPfs);
			BeginStage(clock, console, ProsperoBuildStage.Cnt);
			CntBuildParameters parameters2 = new CntBuildParameters
			{
				ContentId = contentId,
				Passcode = passcode,
				ContentType = 32u,
				ContentFlags = 33685504u
			};
			List<ProsperoCntBuilder.Entry> entries = ProsperoCntBuilder.BuildStandardEntries(contentId, passcode, ekpfs, buildOptions.DeterministicEntryKeys);
			entries.Add(new ProsperoCntBuilder.Entry
			{
				Id = CntEntryId.ParamJson,
				Name = "param.json",
				Data = paramJson,
				Flags1 = ProsperoCntBuilder.Flags1For(CntEntryId.ParamJson)
			});
			entries.Add(new ProsperoCntBuilder.Entry
			{
				Id = CntEntryId.Imagedigs,
				Name = null,
				Data = ReverseDigests(outerBuild.ImageDigests),
				Flags1 = ProsperoCntBuilder.Flags1For(CntEntryId.Imagedigs)
			});
			ulong num = 65536uL;
			ulong fihCntOffset = checked(num + (ulong)outerLength);
			var (prosperoPlayGoChunkBuildResult, data, data2) = BuildPlayGoFamily(contentId, playGoProject, innerResult, num, fihCntOffset, console);
			entries.Add(new ProsperoCntBuilder.Entry
			{
				Id = CntEntryId.PlaygoChunkDat,
				Name = "playgo-chunk.dat",
				Data = prosperoPlayGoChunkBuildResult.Data,
				Flags1 = ProsperoCntBuilder.Flags1For(CntEntryId.PlaygoChunkDat)
			});
			foreach (ProsperoSceSysMedia.Entry media in ProsperoSceSysMedia.Collect(sceSysFiles, EffectiveDrmType(buildOptions, param)))
			{
				if (!entries.Any((ProsperoCntBuilder.Entry e) => e.Id == media.Id))
				{
					entries.Add(new ProsperoCntBuilder.Entry
					{
						Id = media.Id,
						Name = media.Name,
						Data = media.Data,
						Flags1 = media.Flags1,
						Flags2 = media.Flags2
					});
				}
			}
			entries.Add(new ProsperoCntBuilder.Entry
			{
				Id = CntEntryId.PlaygoHashTable,
				Name = "playgo-hash-table.dat",
				Data = data,
				Flags1 = ProsperoCntBuilder.Flags1For(CntEntryId.PlaygoHashTable)
			});
			entries.Add(new ProsperoCntBuilder.Entry
			{
				Id = CntEntryId.PlaygoFicm,
				Name = "playgo-ficm.dat",
				Data = data2,
				Flags1 = ProsperoCntBuilder.Flags1For(CntEntryId.PlaygoFicm)
			});
			byte[] nestedImageDigest = Sha3.Sha3_256(naps);
			long nestedMetaBaseBlocks = innerResult.MetaBaseLogical / 65536;
			int num2 = innerFiles.Count((ProsperoInnerImageAssembler.InnerFile f) => f.PayloadLength == 0);
			int num3 = planResult.FileLogicalOffsets.Count - 1;
			int metaBlockCountMirror = num3 + num2;
			byte[] pfsSignedDigest = Sha3.Sha3_256(ProsperoFihBuilder.BuildHeaderBlockFromSuperblock(FihVariant.Debug, (ulong)outerLength, (ulong)(65536 + outerLength), outerBuild.SuperblockPlaintext, (long)outerBuild.SuperblockIndex * 65536L, nestedImageDigest, naps.Length, nestedMetaBaseBlocks, contentVersionHi, innerResult.InnerContentInodes, innerResult.AppFileCount, num3, metaBlockCountMirror, num2));
			byte[] array = ProsperoCntBuilder.BuildMetadata(entries, parameters2, outerLength, outerSeed, pfsImageDigest, pfsSignedDigest);
			console.Log($"    {array.Length:N0} bytes");
			ReportStage(console, ProsperoBuildStage.Cnt);
			LogStage(clock, console, ProsperoBuildStage.Cnt);
			byte[] playGoChunkDat = entries.First((ProsperoCntBuilder.Entry e) => e.Id == CntEntryId.PlaygoChunkDat).Data;
			Func<Stream, long, byte[]> siFactory = (Stream stream, long mountLength) =>
			{
				long num4 = outerLength;
				long num5 = 65536 + num4;
				long num6 = mountLength - num5;
				byte[] array2 = ReadStreamRange(stream, num5, 1440);
				long num7 = (long)BinaryPrimitives.ReadUInt64BigEndian(array2.AsSpan(32));
				long num8 = (long)BinaryPrimitives.ReadUInt64BigEndian(array2.AsSpan(40));
				long mandatorySize = (long)BinaryPrimitives.ReadUInt64BigEndian(array2.AsSpan(48));
				long packageSize = 65536 + num4 + num6;
				byte[] array3 = ReadStreamRange(stream, 0L, 65536);
				ulong innerImageSize = BinaryPrimitives.ReadUInt64LittleEndian(array3.AsSpan(160));
				long num9 = 65536 + num4;
				long num10 = (long)outerBuild.FileBlockCount[0] * 65536L;
				byte[] packageDigest = ReadStreamRange(stream, num5 + 4064, 32);
				byte[] bodyDigest = Sha3.Sha3_256(ReadStreamRange(stream, num5 + num7, checked((int)num8)));
				byte[] fixedInfoDigest = Sha3.Sha3_256(array3);
				byte[] generalDigests = entries.FirstOrDefault((ProsperoCntBuilder.Entry e) => e.Id == CntEntryId.GeneralDigests)?.Data;
				byte[] contentDigest = Dig(32);
				byte[] headerDigest = Dig(96);
				byte[] systemDigest = Dig(128);
				byte[] array4 = Sha3.Sha3_256(outerBuild.SuperblockPlaintext);
				byte[] paramDigest = Sha3.Sha3_256(paramJson);
				byte[] pfsImageSeed = outerBuild.SuperblockPlaintext.AsSpan(880, 16).ToArray();
				List<ProsperoPfsImageEntry> entries2 = (from e in entries
					where e.Id >= CntEntryId.LicenseDat
					select new ProsperoPfsImageEntry((e.Id == CntEntryId.Imagedigs) ? "imagedigs.dat" : (e.Name ?? $"0x{(uint)e.Id:x4}.bin"), e.DataOffset, e.DataSize) into e
					orderby e.Offset
					select e).ToList();
				List<(string, long)> contentFiles = (from n in innerResult.Nodes
					where !n.IsDirectory && n.ParentInode >= 0
					orderby n.Afid
					select (n.FullPath.TrimStart('/'), Size: n.Size)).ToList();
				string text3 = EffectiveDrmType(buildOptions, param) ?? "free";
				return ProsperoSiArchive.BuildDebugSiSegmentFromStream(new ProsperoPfsImageXmlOptions
				{
					ContentId = contentId,
					TitleName = (param.TitleName ?? ""),
					ContentVersion = (param.ContentVersion ?? "01.000.000"),
					DrmType = "none",
					ApplicationDrmType = text3,
					ContentType = "PS5GD",
					ApplicationType = text3,
					MasterVersion = "01.00",
					PackageSize = packageSize,
					PfsImageOffset = 65536L,
					PfsImageSize = num4,
					PfsImageSeed = pfsImageSeed,
					ContainerSize = num6,
					MandatorySize = mandatorySize,
					BodyOffset = num7,
					SupplementalOffset = num6,
					Entries = entries2,
					ContentDigest = contentDigest,
					GameDigest = array4,
					HeaderDigest = headerDigest,
					SystemDigest = systemDigest,
					ParamDigest = paramDigest,
					PackageDigest = packageDigest,
					BodyDigest = bodyDigest,
					SblockDigest = array4,
					FixedInfoDigest = fixedInfoDigest,
					NestedInner = innerResult,
					OuterPfsTree = new OuterPfsTreeInfo
					{
						BlockSize = 65536,
						ImageBlocks = outerBuild.BlockCount,
						InodeCount = 3 + outerSources.Count,
						DinodeBlockCount = 1,
						DinodeBlock = outerBuild.InodeTableIndex,
						DinodeSize = 65536L,
						DinodeFlags = 0u,
						RootInode = 0u,
						Seed = outerSeed,
						SuperblockIcv = outerBuild.SuperblockIcv,
						Signed = true,
						Encrypted = true,
						Root = BuildOuterPfsTreeRoot(outerBuild.SuperRootDirentIndex, outerBuild.FltIndex, outerBuild.UrootDirentIndex, outerBuild.FileFirstBlock, new List<(string, long, long)>
						{
							("pfs_image.dat", innerResult.ImageLength, innerResult.Ndblock * 65536),
							("naps_pkg_layout.dat", naps.Length, naps.Length)
						})
					},
					ChunkInfo = new ProsperoChunkInfoModel
					{
						PlayGoChunkDatSize = (playGoChunkDat?.Length ?? 0),
						TotalSize = num9,
						Outer0Size = num10,
						Outer1Size = num9 - num10
					}
				}, playGoChunkDat, stream, mountLength, innerImageSize, contentFiles, outerBuild.SuperblockPlaintext, null, includePfsImageXml: false);
				byte[]? Dig(int slotOff)
				{
					if (generalDigests == null || generalDigests.Length < 480)
					{
						return null;
					}
					return generalDigests.AsSpan(slotOff, 32).ToArray();
				}
			};
			cancellationToken.ThrowIfCancellationRequested();
			BeginStage(clock, console, ProsperoBuildStage.Finalize);
			ProsperoFihBuilder.FinalizePrepositionedToFile(array, outputPath, outerLength, FihVariant.Debug, outerBuild.SuperblockIndex, outerBuild.SuperblockPlaintext, nestedImageDigest, naps.Length, nestedMetaBaseBlocks, contentVersionHi, innerResult.InnerContentInodes, innerResult.AppFileCount, num3, metaBlockCountMirror, num2, 0, siFactory);
			ReportStage(console, ProsperoBuildStage.Finalize);
			LogStage(clock, console, ProsperoBuildStage.Finalize);
			long length = new FileInfo(outputPath).Length;
			console.Log($"    wrote {outputPath} ({length:N0} bytes)");
			return new DebugPackageBuildResult(Path.GetFullPath(outputPath), length, contentId, innerFiles.Count, FileBacked: true);
		}
		catch (OperationCanceledException)
		{
			try
			{
				if (File.Exists(outputPath))
				{
					File.Delete(outputPath);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			throw;
		}
		finally
		{
			TempWorkspace.Delete(text);
		}
	}

	private static byte[] ReverseDigests(byte[] digests)
	{
		byte[] array = (byte[])digests.Clone();
		for (int i = 0; i + 32 <= array.Length; i += 32)
		{
			Array.Reverse(array, i, 32);
		}
		return array;
	}

	private static byte[] ReadStreamRange(Stream stream, long offset, int length)
	{
		byte[] array = new byte[length];
		stream.Position = offset;
		int num;
		for (int i = 0; i < length; i += num)
		{
			num = stream.Read(array, i, length - i);
			if (num <= 0)
			{
				throw new EndOfStreamException("Unexpected end of the mount stream.");
			}
		}
		return array;
	}

	private static byte[] BuildSiSegment(string contentId, string applicationDrmType, ProsperoParam param, byte[] cnt, IReadOnlyList<ProsperoCntBuilder.Entry> entries, IReadOnlyList<OuterPfsFile> outerFiles, OuterPfsBuildResult outerBuild, byte[] outerSeed, byte[] superblockIcv, ProsperoInnerImageAssembler.InnerImageResult innerResult, byte[] fihNoSi, byte[] paramJson, byte[]? playGoChunkDat)
	{
		long num = (long)BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(1048));
		long num2 = cnt.Length - num;
		long num3 = (long)BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(32));
		ulong num4 = BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(40));
		long mandatorySize = (long)BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(48));
		long packageSize = 65536 + num + num2;
		ulong innerImageSize = BinaryPrimitives.ReadUInt64LittleEndian(fihNoSi.AsSpan(160));
		long num5 = 65536 + num;
		long num6 = (long)outerBuild.FileBlockCount[0] * 65536L;
		byte[] packageDigest = cnt.AsSpan(4064, 32).ToArray();
		byte[] bodyDigest = Sha3.Sha3_256(cnt.AsSpan((int)num3, (int)num4));
		byte[] fixedInfoDigest = Sha3.Sha3_256(fihNoSi.AsSpan(0, 65536));
		byte[] generalDigests = entries.FirstOrDefault((ProsperoCntBuilder.Entry e) => e.Id == CntEntryId.GeneralDigests)?.Data;
		byte[] contentDigest = Dig(32);
		byte[] headerDigest = Dig(96);
		byte[] systemDigest = Dig(128);
		int num7 = outerBuild.SuperblockIndex * 65536;
		byte[] array = Sha3.Sha3_256(outerBuild.Plaintext.AsSpan(num7, 65536));
		byte[] paramDigest = Sha3.Sha3_256(paramJson);
		byte[] pfsImageSeed = outerBuild.Plaintext.AsSpan(num7 + 880, 16).ToArray();
		List<ProsperoPfsImageEntry> entries2 = (from e in entries
			where e.Id >= CntEntryId.LicenseDat
			select new ProsperoPfsImageEntry((e.Id == CntEntryId.Imagedigs) ? "imagedigs.dat" : (e.Name ?? $"0x{(uint)e.Id:x4}.bin"), e.DataOffset, e.DataSize) into e
			orderby e.Offset
			select e).ToList();
		List<(string, long)> contentFiles = (from n in innerResult.Nodes
			where !n.IsDirectory && n.ParentInode >= 0
			orderby n.Afid
			select (n.FullPath.TrimStart('/'), Size: n.Size)).ToList();
		string contentVersion = param.ContentVersion ?? "01.000.000";
		string titleName = param.TitleName ?? "";
		string masterVersion = "01.00";
		return ProsperoSiArchive.BuildDebugSiSegment(new ProsperoPfsImageXmlOptions
		{
			ContentId = contentId,
			TitleName = titleName,
			ContentVersion = contentVersion,
			DrmType = "none",
			ApplicationDrmType = applicationDrmType,
			ContentType = "PS5GD",
			ApplicationType = applicationDrmType,
			MasterVersion = masterVersion,
			PackageSize = packageSize,
			PfsImageOffset = 65536L,
			PfsImageSize = num,
			PfsImageSeed = pfsImageSeed,
			ContainerSize = num2,
			MandatorySize = mandatorySize,
			BodyOffset = num3,
			SupplementalOffset = num2,
			Entries = entries2,
			ContentDigest = contentDigest,
			GameDigest = array,
			HeaderDigest = headerDigest,
			SystemDigest = systemDigest,
			ParamDigest = paramDigest,
			PackageDigest = packageDigest,
			BodyDigest = bodyDigest,
			SblockDigest = array,
			FixedInfoDigest = fixedInfoDigest,
			NestedInner = innerResult,
			OuterPfsTree = new OuterPfsTreeInfo
			{
				BlockSize = 65536,
				ImageBlocks = outerBuild.BlockCount,
				InodeCount = 3 + outerFiles.Count,
				DinodeBlockCount = 1,
				DinodeBlock = outerBuild.InodeTableIndex,
				DinodeSize = 65536L,
				DinodeFlags = 0u,
				RootInode = 0u,
				Seed = outerSeed,
				SuperblockIcv = superblockIcv,
				Signed = true,
				Encrypted = true,
				Root = BuildOuterPfsTreeRoot(outerBuild, outerFiles)
			},
			ChunkInfo = new ProsperoChunkInfoModel
			{
				PlayGoChunkDatSize = (playGoChunkDat?.Length ?? 0),
				TotalSize = num5,
				Outer0Size = num6,
				Outer1Size = num5 - num6
			}
		}, playGoChunkDat, fihNoSi, innerImageSize, contentFiles, null, includePfsImageXml: false);
		byte[]? Dig(int slotOff)
		{
			if (generalDigests == null || generalDigests.Length < 480)
			{
				return null;
			}
			return generalDigests.AsSpan(slotOff, 32).ToArray();
		}
	}

	private static OuterPfsTreeNode BuildOuterPfsTreeRoot(OuterPfsBuildResult outerBuild, IReadOnlyList<OuterPfsFile> outerFiles)
	{
		List<(string, long, long)> list = new List<(string, long, long)>(outerFiles.Count);
		foreach (OuterPfsFile outerFile in outerFiles)
		{
			list.Add((outerFile.Name, outerFile.Data.Length, outerFile.SizeCompressed ?? outerFile.Data.Length));
		}
		return BuildOuterPfsTreeRoot(outerBuild.SuperRootDirentIndex, outerBuild.FltIndex, outerBuild.UrootDirentIndex, outerBuild.FileFirstBlock, list);
	}

	private static OuterPfsTreeNode BuildOuterPfsTreeRoot(int superRootDirentIndex, int fltIndex, int urootDirentIndex, int[] fileFirstBlock, IReadOnlyList<(string Name, long StoredSize, long PlainSize)> files)
	{
		long num = 64 + (long)files.Count * 16L;
		OuterPfsTreeNode outerPfsTreeNode = new OuterPfsTreeNode
		{
			Name = "",
			IsDirectory = true,
			Inode = 0u,
			StartBlock = superRootDirentIndex,
			StoredSize = 65536L,
			PlainSize = 65536L,
			Flags = 131084u,
			Mode = 16749,
			Nlink = 1
		};
		outerPfsTreeNode.Children.Add(new OuterPfsTreeNode
		{
			Name = "inode_flat_path_table",
			IsDirectory = false,
			Inode = 1u,
			StartBlock = fltIndex,
			StoredSize = num,
			PlainSize = num,
			Flags = 131084u,
			Mode = 33133,
			Nlink = 1,
			Internal = true
		});
		OuterPfsTreeNode outerPfsTreeNode2 = new OuterPfsTreeNode
		{
			Name = "uroot",
			IsDirectory = true,
			Inode = 2u,
			StartBlock = urootDirentIndex,
			StoredSize = 65536L,
			PlainSize = 65536L,
			Flags = 12u,
			Mode = 16749,
			Nlink = 3
		};
		for (int i = 0; i < files.Count; i++)
		{
			var (name, storedSize, plainSize) = files[i];
			outerPfsTreeNode2.Children.Add(new OuterPfsTreeNode
			{
				Name = name,
				IsDirectory = false,
				Inode = (uint)(3 + i),
				StartBlock = fileFirstBlock[i],
				StoredSize = storedSize,
				PlainSize = plainSize,
				Flags = 13u,
				Mode = 33133,
				Nlink = 1
			});
		}
		outerPfsTreeNode.Children.Add(outerPfsTreeNode2);
		return outerPfsTreeNode;
	}

	private static void FakeSignInnerModules(List<ProsperoInnerImageAssembler.InnerFile> innerFiles, DebugPackageBuildLog console, ulong? sdkVersionOverride)
	{
		byte[] array = null;
		int num = innerFiles.FindIndex((ProsperoInnerImageAssembler.InnerFile file) => string.Equals(file.Path, "/eboot.bin", StringComparison.Ordinal));
		if (num >= 0)
		{
			try
			{
				if (ProsperoSelfBuilder.TryGetSceVersionRecord(ReadInnerFileBytes(innerFiles[num]), out byte[] record))
				{
					array = record;
				}
			}
			catch (Exception ex) when ((ex is InvalidDataException || ex is IOException) ? true : false)
			{
				console.Log("  warning: could not read the eboot sceversion record (" + ex.Message + ").");
			}
		}
		if (sdkVersionOverride.HasValue)
		{
			ulong valueOrDefault = sdkVersionOverride.GetValueOrDefault();
			array = ProsperoSelfBuilder.BuildSdkVersionTuple(valueOrDefault);
		}
		for (int num2 = 0; num2 < innerFiles.Count; num2++)
		{
			ProsperoInnerImageAssembler.InnerFile innerFile = innerFiles[num2];
			if (!IsFakeSignTarget(innerFile.Path))
			{
				continue;
			}
			try
			{
				byte[] array2 = ReadInnerFileBytes(innerFile);
				if (ProsperoSelfBuilder.IsElf(array2))
				{
					FselfOptions options = null;
					byte[] record2;
					if (sdkVersionOverride.HasValue)
					{
						ulong valueOrDefault2 = sdkVersionOverride.GetValueOrDefault();
						options = new FselfOptions
						{
							SdkVersionOverride = valueOrDefault2,
							SceVersionName = Path.GetFileNameWithoutExtension(innerFile.Path),
							SceVersionRecord = array
						};
					}
					else if (!string.Equals(innerFile.Path, "/eboot.bin", StringComparison.Ordinal) && array != null && !ProsperoSelfBuilder.TryGetSceVersionRecord(array2, out record2))
					{
						options = new FselfOptions
						{
							SceVersionName = Path.GetFileNameWithoutExtension(innerFile.Path),
							SceVersionRecord = array
						};
					}
					byte[] array3 = ProsperoSelfBuilder.MakeFself(array2, options);
					innerFiles[num2] = new ProsperoInnerImageAssembler.InnerFile
					{
						Path = innerFile.Path,
						Data = array3,
						PlayGoChunkId = innerFile.PlayGoChunkId
					};
					console.Log($"  Fake-signed module: {innerFile.Path} ({array2.Length} -> {array3.Length} bytes)");
				}
			}
			catch (Exception ex2) when ((ex2 is InvalidDataException || ex2 is IOException || ex2 is ArgumentException || ex2 is NotSupportedException) ? true : false)
			{
				console.Log($"  warning: could not fake-sign {innerFile.Path}: {ex2.Message}; packing it unchanged.");
			}
		}
	}

	private static byte[] ReadInnerFileBytes(ProsperoInnerImageAssembler.InnerFile file)
	{
		if (file.Source != null)
		{
			using (Stream stream = file.Source.Open())
			{
				using MemoryStream memoryStream = new MemoryStream();
				stream.CopyTo(memoryStream);
				return memoryStream.ToArray();
			}
		}
		if (file.SourcePath == null)
		{
			return file.Data;
		}
		return File.ReadAllBytes(file.SourcePath);
	}

	private static bool IsFakeSignTarget(string path)
	{
		string fileName = Path.GetFileName(path);
		if (!fileName.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".elf", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".prx", StringComparison.OrdinalIgnoreCase))
		{
			return fileName.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}
}
