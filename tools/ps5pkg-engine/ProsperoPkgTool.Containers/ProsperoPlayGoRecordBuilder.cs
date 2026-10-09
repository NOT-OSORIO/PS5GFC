using System;
using System.Collections.Generic;
using System.Linq;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPlayGoRecordBuilder
{
	public static IReadOnlyList<ProsperoPlayGoFileRecord> Build(ProsperoInnerImageAssembler.InnerImageResult inner, IReadOnlyDictionary<string, byte>? chunkAssignments = null)
	{
		ArgumentNullException.ThrowIfNull(inner, "inner");
		List<ProsperoInnerImageAssembler.MetaNodeInfo> list = inner.Nodes.Where((ProsperoInnerImageAssembler.MetaNodeInfo n) => !n.IsDirectory && n.FullPath.Length != 0).ToList();
		if (list.Count == 0)
		{
			return Array.Empty<ProsperoPlayGoFileRecord>();
		}
		Dictionary<string, byte> dictionary = new Dictionary<string, byte>(StringComparer.Ordinal);
		if (chunkAssignments != null)
		{
			foreach (var (path, value) in chunkAssignments)
			{
				dictionary[NormalizePath(path)] = value;
			}
		}
		new HashSet<string>(list.Select((ProsperoInnerImageAssembler.MetaNodeInfo f) => NormalizePath(f.FullPath)), StringComparer.Ordinal);
		List<ProsperoPlayGoFileRecord> list2 = new List<ProsperoPlayGoFileRecord>(list.Count);
		foreach (ProsperoInnerImageAssembler.MetaNodeInfo item in list)
		{
			string text2 = NormalizePath(item.FullPath);
			byte chunkId = (byte)(dictionary.TryGetValue(text2, out var value2) ? value2 : 0);
			list2.Add(new ProsperoPlayGoFileRecord(text2, chunkId, ProsperoOuterPfsBuilder.FltPathHash(text2)));
		}
		list2.Sort((ProsperoPlayGoFileRecord a, ProsperoPlayGoFileRecord prosperoPlayGoFileRecord) => a.PathHash.CompareTo(prosperoPlayGoFileRecord.PathHash));
		List<ProsperoPlayGoFileRecord> list3 = new List<ProsperoPlayGoFileRecord>(list2.Count);
		foreach (ProsperoPlayGoFileRecord item2 in list2)
		{
			if (list3.Count > 0)
			{
				if (list3[list3.Count - 1].PathHash == item2.PathHash)
				{
					continue;
				}
			}
			list3.Add(item2);
		}
		return list3;
	}

	public static string NormalizePath(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		string text = path.Replace('\\', '/').TrimStart('/');
		List<char> list = new List<char>(text.Length);
		string text2 = text;
		foreach (char c in text2)
		{
			int num;
			switch (c)
			{
			case '/':
				if (list.Count > 0)
				{
					if (list[list.Count - 1] != '/')
					{
						list.Add('/');
					}
				}
				continue;
			default:
				num = c;
				break;
			case 'a':
			case 'b':
			case 'c':
			case 'd':
			case 'e':
			case 'f':
			case 'g':
			case 'h':
			case 'i':
			case 'j':
			case 'k':
			case 'l':
			case 'm':
			case 'n':
			case 'o':
			case 'p':
			case 'q':
			case 'r':
			case 's':
			case 't':
			case 'u':
			case 'v':
			case 'w':
			case 'x':
			case 'y':
			case 'z':
				num = (ushort)(c - 32);
				break;
			}
			char c2 = (char)num;
			list.Add((c2 <= '\u007f') ? c2 : '?');
		}
		while (list.Count > 0)
		{
			if (list[list.Count - 1] != '/')
			{
				break;
			}
			list.RemoveAt(list.Count - 1);
		}
		return new string(list.ToArray());
	}
}
