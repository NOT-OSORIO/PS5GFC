using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ProsperoPkgTool.Content;

public static class ProsperoLaunchReadiness
{
	private const uint ElfMagic = 1179403647u;

	private const uint SelfMagic = 4009038932u;

	private const ulong AuthorityMask = 18374686479671623680uL;

	private const ulong FakeAuthorityPrefix = 3530822107858468864uL;

	private const ulong GenuineAuthorityPrefix = 4971973988617027584uL;

	private const int HeaderWindow = 2097152;

	private static readonly string[] ModuleExtensions = new string[2] { ".prx", ".sprx" };

	public static ModuleLaunchReadiness InspectModule(string path, ReadOnlySpan<byte> data)
	{
		if (data.Length < 4)
		{
			return new ModuleLaunchReadiness(path, ModuleAuthorityKind.NotExecutable, 0uL, WillRunOnDebugConsole: false, "File is too short to classify.");
		}
		bool flag;
		switch (BinaryPrimitives.ReadUInt32LittleEndian(data))
		{
		case 1179403647u:
			return new ModuleLaunchReadiness(path, ModuleAuthorityKind.RawElf, 0uL, WillRunOnDebugConsole: true, "Raw ELF module; the build fake-signs it, so it starts on a debug-mode console.");
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
			return ClassifySelf(path, data);
		}
		return new ModuleLaunchReadiness(path, ModuleAuthorityKind.NotExecutable, 0uL, WillRunOnDebugConsole: false, "Not an ELF or SELF module.");
	}

	private static ModuleLaunchReadiness ClassifySelf(string path, ReadOnlySpan<byte> data)
	{
		ProsperoSelfImage prosperoSelfImage;
		try
		{
			prosperoSelfImage = ProsperoSelfReader.Parse(data);
		}
		catch (InvalidDataException ex)
		{
			return new ModuleLaunchReadiness(path, ModuleAuthorityKind.UnknownAuthoritySelf, 0uL, WillRunOnDebugConsole: false, "SELF header could not be parsed: " + ex.Message);
		}
		if ((object)prosperoSelfImage.ExtInfo == null)
		{
			return new ModuleLaunchReadiness(path, ModuleAuthorityKind.UnknownAuthoritySelf, 0uL, WillRunOnDebugConsole: false, "SELF has no extended info; authority id is unavailable.");
		}
		ulong authorityId = prosperoSelfImage.ExtInfo.AuthorityId;
		return (authorityId & 0xFF00000000000000uL) switch
		{
			3530822107858468864uL => new ModuleLaunchReadiness(path, ModuleAuthorityKind.FakeAuthoritySelf, authorityId, WillRunOnDebugConsole: true, "Fake-authority SELF; starts on a debug-mode console."),
			4971973988617027584uL => new ModuleLaunchReadiness(path, ModuleAuthorityKind.GenuineAuthoritySelf, authorityId, WillRunOnDebugConsole: false, "Genuine-authority SELF; relies on the console-provided module and does not start from this package on a debug-mode console."),
			_ => new ModuleLaunchReadiness(path, ModuleAuthorityKind.UnknownAuthoritySelf, authorityId, WillRunOnDebugConsole: false, "SELF authority prefix is neither fake nor a known genuine value."),
		};
	}

	public static ProsperoLaunchReadinessReport InspectAppRoot(string appRoot)
	{
		if (string.IsNullOrWhiteSpace(appRoot) || !Directory.Exists(appRoot))
		{
			throw new ArgumentException("Application root does not exist.", "appRoot");
		}
		string fullPath = Path.GetFullPath(appRoot);
		List<ModuleLaunchReadiness> list = new List<ModuleLaunchReadiness>();
		List<string> list2 = new List<string>();
		string text = Path.Combine(fullPath, "eboot.bin");
		bool flag = File.Exists(text);
		List<string> list3 = new List<string>();
		if (flag)
		{
			list3.Add(text);
		}
		list3.AddRange((from f in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories)
			where ModuleExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())
			select f).OrderBy((string f) => f, StringComparer.OrdinalIgnoreCase));
		list3 = list3.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		foreach (string item in list3)
		{
			ModuleLaunchReadiness moduleLaunchReadiness = InspectModule(Path.GetRelativePath(fullPath, item).Replace('\\', '/'), ReadHeaderWindow(item));
			if (moduleLaunchReadiness.Kind != ModuleAuthorityKind.NotExecutable)
			{
				list.Add(moduleLaunchReadiness);
			}
		}
		bool flag2 = File.Exists(Path.Combine(fullPath, "sce_sys", "param.json"));
		bool flag3 = File.Exists(Path.Combine(fullPath, "sce_sys", "param.sfo"));
		bool requiresDebugConsole = list.Any((ModuleLaunchReadiness m) =>
		{
			ModuleAuthorityKind kind = m.Kind;
			return (uint)(kind - 1) <= 1u;
		});
		if (!flag)
		{
			list2.Add("No eboot.bin at the application root.");
		}
		ModuleLaunchReadiness moduleLaunchReadiness2 = list.FirstOrDefault((ModuleLaunchReadiness m) => m.Path == "eboot.bin");
		if (flag && (object)moduleLaunchReadiness2 == null)
		{
			list2.Add("eboot.bin is present but is not a loadable ELF or SELF module; it will not start.");
		}
		else if (flag && (object)moduleLaunchReadiness2 != null && !moduleLaunchReadiness2.WillRunOnDebugConsole)
		{
			list2.Add("eboot.bin will not start on a debug-mode console: " + moduleLaunchReadiness2.Note);
		}
		foreach (ModuleLaunchReadiness item2 in list.Where((ModuleLaunchReadiness m) => m.Kind == ModuleAuthorityKind.SignedEncrypted))
		{
			list2.Add("Module '" + item2.Path + "' is signed and encrypted; it will not start on a debug-mode console.");
		}
		if (!flag2)
		{
			list2.Add("No sce_sys/param.json; the launch service needs the param.json metadata form.");
		}
		if (flag3)
		{
			list2.Add("A sce_sys/param.sfo is present; the launch service refuses the param.sfo metadata form.");
		}
		return new ProsperoLaunchReadinessReport
		{
			AppRoot = fullPath,
			Modules = list,
			HasEboot = flag,
			HasParamJson = flag2,
			HasParamSfo = flag3,
			RequiresDebugConsole = requiresDebugConsole,
			Issues = list2
		};
	}

	private static byte[] ReadHeaderWindow(string path)
	{
		using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		int num = (int)Math.Min(fileStream.Length, 2097152L);
		byte[] array = new byte[num];
		int i;
		int num2;
		for (i = 0; i < num; i += num2)
		{
			num2 = fileStream.Read(array, i, num - i);
			if (num2 == 0)
			{
				break;
			}
		}
		return (i == num) ? array : array[..i];
	}
}
