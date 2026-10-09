using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProsperoPkgTool.Content;

public static class ProsperoBackupConverter
{
	private readonly record struct ParamMeta(string ContentId, string Version, string TitleId);

	private const uint ElfMagic = 1179403647u;

	private const uint SelfMagic = 4009038932u;

	private const ulong AuthorityMask = 18374686479671623680uL;

	private const ulong FakeAuthorityPrefix = 3530822107858468864uL;

	private const int HeaderWindow = 2097152;

	public static ProsperoBackupStagingResult Stage(ProsperoBackupConversionOptions options, Action<string>? logger = null, IProgress<ProsperoBuildProgress>? progress = null)
	{
		ArgumentNullException.ThrowIfNull(options, "options");
		Action<string> action = logger ?? ((Action<string>)((string _) =>
		{
		}));
		List<string> list = new List<string>();
		if (string.IsNullOrWhiteSpace(options.BackupFolder) || !Directory.Exists(options.BackupFolder))
		{
			throw new ArgumentException("Backup folder does not exist.", "options");
		}
		if (string.IsNullOrWhiteSpace(options.OutputFolder))
		{
			throw new ArgumentException("Output folder was not specified.", "options");
		}
		string fullPath = Path.GetFullPath(options.BackupFolder);
		if (!Directory.Exists(Path.Combine(fullPath, "sce_sys")))
		{
			throw new ArgumentException("Backup folder does not contain a sce_sys directory.", "options");
		}
		string text = Path.Combine(fullPath, options.DecryptedSubfolder);
		bool hasDecrypted = Directory.Exists(text);
		ParamMeta paramMeta = ReadParamMeta(Path.Combine(fullPath, "sce_sys", "param.json"));
		string text2 = ((!string.IsNullOrWhiteSpace(options.ContentId)) ? options.ContentId.Trim() : paramMeta.ContentId);
		if (string.IsNullOrWhiteSpace(text2))
		{
			throw new ArgumentException("Content id was not supplied and could not be read from param.json.", "options");
		}
		string passcode = (string.IsNullOrEmpty(options.Passcode) ? ProsperoDebugLicense.DefaultPasscode : options.Passcode);
		string version;
		if (string.IsNullOrWhiteSpace(options.Version))
		{
			version = ((!string.IsNullOrWhiteSpace(paramMeta.Version)) ? paramMeta.Version : "01.00");
		}
		else
		{
			version = options.Version;
		}
		if (!string.IsNullOrWhiteSpace(options.Version) && !string.Equals(options.Version, paramMeta.Version, StringComparison.Ordinal))
		{
			list.Add("Version override '" + options.Version + "' is not applied; the package uses the sce_sys/param.json version.");
		}
		ProsperoDebugLicense debugLicense = ProsperoDebugLicense.Create(text2, passcode);
		string text3 = (string.IsNullOrWhiteSpace(options.StagingFolder) ? Path.Combine(options.OutputFolder, "." + SafeName(text2) + ".convert.tmp") : Path.GetFullPath(options.StagingFolder));
		string fullPath2 = Path.GetFullPath(string.IsNullOrWhiteSpace(options.OutputFolder) ? "." : options.OutputFolder);
		if (IsUnder(fullPath, text3) || IsUnder(fullPath2, text3))
		{
			throw new ArgumentException("Staging folder must not be the backup/output folder or one of their parent directories.", "options");
		}
		List<string> list2 = new List<string>();
		List<string> list3 = new List<string>();
		List<string> list4 = new List<string>();
		if (Directory.Exists(text3))
		{
			Directory.Delete(text3, recursive: true);
		}
		Directory.CreateDirectory(text3);
		try
		{
			AssembleTree(fullPath, text, hasDecrypted, text3, options, action, list, list2, list3, list4, progress);
		}
		catch
		{
			try
			{
				if (Directory.Exists(text3))
				{
					Directory.Delete(text3, recursive: true);
				}
			}
			catch (IOException)
			{
				action("Warning: could not remove the temporary tree '" + text3 + "'.");
			}
			throw;
		}
		foreach (string item in list4)
		{
			list.Add("Module '" + item + "' is signed with no decrypted copy; it is packed unchanged and will not run.");
		}
		RepairParamIdentity(text3, text2, action, list);
		ProsperoLaunchReadinessReport launchReadiness = ProsperoLaunchReadiness.InspectAppRoot(text3);
		return new ProsperoBackupStagingResult
		{
			StagingFolder = text3,
			ContentId = text2,
			Passcode = passcode,
			Version = version,
			DebugLicense = debugLicense,
			SubstitutedModules = list2,
			PlaintextModules = list3,
			UnresolvedModules = list4,
			Warnings = list,
			LaunchReadiness = launchReadiness
		};
	}

	public static ProsperoBackupConversionResult Convert(ProsperoBackupConversionOptions options, Func<ProsperoBackupStagingResult, string> build, Action<string>? logger = null, IProgress<ProsperoBuildProgress>? progress = null)
	{
		ArgumentNullException.ThrowIfNull(options, "options");
		ArgumentNullException.ThrowIfNull(build, "build");
		Action<string> action = logger ?? ((Action<string>)((string _) =>
		{
		}));
		List<string> list = new List<string>();
		ProsperoBackupStagingResult prosperoBackupStagingResult = Stage(options, action, progress);
		list.AddRange(prosperoBackupStagingResult.Warnings);
		try
		{
			action("Building debug package for " + prosperoBackupStagingResult.ContentId + " from the assembled tree.");
			string outputPath = build(prosperoBackupStagingResult);
			ProsperoLaunchReadinessReport launchReadiness;
			if (Directory.Exists(prosperoBackupStagingResult.StagingFolder))
			{
				launchReadiness = ProsperoLaunchReadiness.InspectAppRoot(prosperoBackupStagingResult.StagingFolder);
			}
			else
			{
				list.Add("The staging tree '" + prosperoBackupStagingResult.StagingFolder + "' is no longer present after the build; reusing the launch-readiness report captured before the build.");
				launchReadiness = prosperoBackupStagingResult.LaunchReadiness;
			}
			return new ProsperoBackupConversionResult
			{
				OutputPath = outputPath,
				DebugLicense = prosperoBackupStagingResult.DebugLicense,
				SubstitutedModules = prosperoBackupStagingResult.SubstitutedModules,
				PlaintextModules = prosperoBackupStagingResult.PlaintextModules,
				UnresolvedModules = prosperoBackupStagingResult.UnresolvedModules,
				Warnings = list,
				LaunchReadiness = launchReadiness,
				StagingFolder = (options.KeepStaging ? prosperoBackupStagingResult.StagingFolder : "")
			};
		}
		finally
		{
			if (!options.KeepStaging)
			{
				try
				{
					if (Directory.Exists(prosperoBackupStagingResult.StagingFolder))
					{
						Directory.Delete(prosperoBackupStagingResult.StagingFolder, recursive: true);
					}
				}
				catch (IOException)
				{
					action("Warning: could not remove the temporary tree '" + prosperoBackupStagingResult.StagingFolder + "'.");
				}
			}
		}
	}

	private static void AssembleTree(string backup, string decryptedRoot, bool hasDecrypted, string staging, ProsperoBackupConversionOptions options, Action<string> log, List<string> warnings, List<string> substituted, List<string> plaintext, List<string> unresolved, IProgress<ProsperoBuildProgress>? progress)
	{
		string decryptedFull = (hasDecrypted ? Path.GetFullPath(decryptedRoot) : "");
		string[] files = Directory.GetFiles(backup, "*", SearchOption.AllDirectories);
		long[] array = new long[files.Length];
		long num = 0L;
		for (int i = 0; i < files.Length; i++)
		{
			try
			{
				array[i] = new FileInfo(files[i]).Length;
			}
			catch (IOException)
			{
				array[i] = 0L;
			}
			num += array[i];
		}
		long num2 = 0L;
		for (int j = 0; j < files.Length; j++)
		{
			StageFile(files[j]);
			num2 += array[j];
			progress?.Report(new ProsperoBuildProgress("Staging", j + 1, files.Length)
			{
				StageId = ProsperoBuildStage.Staging,
				BytesDone = num2,
				BytesTotal = num,
				CurrentPath = Path.GetRelativePath(backup, files[j])
			});
		}
		log($"Staging complete: {files.Length:N0} files, {(double)num / 1073741824.0:N2} GiB.");
		void StageFile(string file)
		{
			string fullPath = Path.GetFullPath(file);
			if (!hasDecrypted || !IsUnder(fullPath, decryptedFull))
			{
				string relativePath = Path.GetRelativePath(backup, fullPath);
				string text = relativePath.Replace('\\', '/');
				if (options.UseEmbeddedRightSprx && text.Equals("sce_sys/about/right.sprx", StringComparison.OrdinalIgnoreCase))
				{
					log("Dropping the backup right.sprx; the embedded debug module will be used.");
				}
				else
				{
					if (options.Verbose)
					{
						log("  staging " + text);
					}
					string text2 = Path.Combine(staging, relativePath);
					Directory.CreateDirectory(Path.GetDirectoryName(text2));
					if (IsModuleCandidate(Path.GetFileName(relativePath)))
					{
						bool flag;
						switch (ReadMagic(fullPath))
						{
						case 1179403647u:
							File.Copy(fullPath, text2, overwrite: true);
							plaintext.Add(text);
							return;
						case 4009038932u:
						case 490542415u:
							flag = true;
							break;
						default:
							flag = false;
							break;
						}
						if (flag)
						{
							string text3 = (hasDecrypted ? Path.Combine(decryptedRoot, relativePath) : null);
							if (text3 != null && File.Exists(text3) && ReadMagic(text3) == 1179403647)
							{
								File.Copy(text3, text2, overwrite: true);
								substituted.Add(text);
								log("Substituted decrypted module for " + text + ".");
							}
							else
							{
								File.Copy(fullPath, text2, overwrite: true);
								if (IsFakeAuthoritySelf(fullPath))
								{
									plaintext.Add(text);
									log("Kept already fake-signed module " + text + ".");
								}
								else
								{
									unresolved.Add(text);
								}
							}
							return;
						}
					}
					File.Copy(fullPath, text2, overwrite: true);
				}
			}
		}
	}

	private static bool IsModuleCandidate(string fileName)
	{
		if (!fileName.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".elf", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".prx", StringComparison.OrdinalIgnoreCase))
		{
			return fileName.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static uint ReadMagic(string path)
	{
		try
		{
			using FileStream fileStream = File.OpenRead(path);
			Span<byte> buffer = stackalloc byte[4];
			if (fileStream.ReadAtLeast(buffer, 4, throwOnEndOfStream: false) < 4)
			{
				return 0u;
			}
			return (uint)(buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24));
		}
		catch (IOException)
		{
			return 0u;
		}
	}

	private static bool IsFakeAuthoritySelf(string path)
	{
		try
		{
			using FileStream fileStream = File.OpenRead(path);
			int num = (int)Math.Min(2097152L, fileStream.Length);
			if (num <= 0)
			{
				return false;
			}
			byte[] array = new byte[num];
			fileStream.ReadAtLeast(array, num, throwOnEndOfStream: false);
			ulong authorityId;
			return ProsperoSelfReader.TryReadAuthorityId(array, out authorityId) && (authorityId & 0xFF00000000000000uL) == 3530822107858468864L;
		}
		catch (IOException)
		{
			return false;
		}
	}

	private static bool IsUnder(string path, string root)
	{
		string text = path.TrimEnd(Path.DirectorySeparatorChar);
		string text2 = root.TrimEnd(Path.DirectorySeparatorChar);
		if (!text.Equals(text2, StringComparison.OrdinalIgnoreCase))
		{
			return text.StartsWith(text2 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static string SafeName(string contentId)
	{
		Span<char> span = stackalloc char[contentId.Length];
		for (int i = 0; i < contentId.Length; i++)
		{
			char c = contentId[i];
			int index = i;
			bool flag = char.IsLetterOrDigit(c);
			if (!flag)
			{
				bool flag2 = ((c == '-' || c == '_') ? true : false);
				flag = flag2;
			}
			span[index] = (flag ? c : '_');
		}
		return new string(span);
	}

	private static void RepairParamIdentity(string staging, string contentId, Action<string> log, List<string> warnings)
	{
		if (contentId.Length != 36)
		{
			return;
		}
		string path = Path.Combine(staging, "sce_sys", "param.json");
		if (!File.Exists(path))
		{
			return;
		}
		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch (IOException)
		{
			return;
		}
		string value = contentId.Substring(7, 9);
		string json = ReplaceStringField(text, "contentId", contentId, "content id", log, warnings);
		json = ReplaceStringField(json, "titleId", value, "title id", log, warnings);
		if (json == text)
		{
			return;
		}
		try
		{
			File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch (IOException ex2)
		{
			warnings.Add("Could not rewrite the staged param.json identity fields: " + ex2.Message);
		}
	}

	private static string ReplaceStringField(string json, string field, string value, string label, Action<string> log, List<string> warnings)
	{
		Match match = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"([^\"]*)\"");
		if (!match.Success || match.Groups[1].Value == value)
		{
			return json;
		}
		log($"Updated staged param.json {label}: '{match.Groups[1].Value}' -> '{value}'.");
		warnings.Add($"param.json {label} '{match.Groups[1].Value}' did not match the package content id; the staged copy was updated to '{value}'.");
		return json.Substring(0, match.Groups[1].Index) + value + json.Substring(match.Groups[1].Index + match.Groups[1].Length);
	}

	private static ParamMeta ReadParamMeta(string paramPath)
	{
		if (!File.Exists(paramPath))
		{
			return new ParamMeta("", "", "");
		}
		try
		{
			if (!(JsonNode.Parse(File.ReadAllText(paramPath)) is JsonObject jsonObject))
			{
				return new ParamMeta("", "", "");
			}
			string contentId = ((jsonObject.TryGetPropertyValue("contentId", out JsonNode jsonNode) && jsonNode is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string value)) ? (value ?? "") : "");
			string version = "";
			JsonNode jsonNode3;
			string value3;
			if (jsonObject.TryGetPropertyValue("contentVersion", out JsonNode jsonNode2) && jsonNode2 is JsonValue jsonValue2 && jsonValue2.TryGetValue<string>(out string value2) && !string.IsNullOrWhiteSpace(value2))
			{
				version = value2;
			}
			else if (jsonObject.TryGetPropertyValue("masterVersion", out jsonNode3) && jsonNode3 is JsonValue jsonValue3 && jsonValue3.TryGetValue<string>(out value3))
			{
				version = value3 ?? "";
			}
			string titleId = ((jsonObject.TryGetPropertyValue("titleId", out JsonNode jsonNode4) && jsonNode4 is JsonValue jsonValue4 && jsonValue4.TryGetValue<string>(out string value4)) ? (value4 ?? "") : "");
			return new ParamMeta(contentId, version, titleId);
		}
		catch (JsonException)
		{
			return new ParamMeta("", "", "");
		}
		catch (IOException)
		{
			return new ParamMeta("", "", "");
		}
	}
}
