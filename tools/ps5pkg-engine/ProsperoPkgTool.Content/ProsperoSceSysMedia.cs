using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProsperoPkgTool.Containers;

namespace ProsperoPkgTool.Content;

public static class ProsperoSceSysMedia
{
	public sealed record Entry(CntEntryId Id, string? Name, byte[] Data, uint Flags1, uint Flags2);

	private static readonly (string Name, CntEntryId Id)[] MediaFiles = new (string, CntEntryId)[6]
	{
		("icon0.png", CntEntryId.Icon0Png),
		("pic0.png", CntEntryId.Pic0Png),
		("pic1.png", CntEntryId.Pic1Png),
		("pic2.png", CntEntryId.Pic2Png),
		("snd0.at9", CntEntryId.Snd0At9),
		("save_data.png", CntEntryId.SaveDataPng)
	};

	private static readonly (string Png, string Dds, CntEntryId Id)[] DdsMedia = new (string, string, CntEntryId)[4]
	{
		("icon0.png", "icon0.dds", CntEntryId.Icon0Dds),
		("pic0.png", "pic0.dds", CntEntryId.Pic0Dds),
		("pic1.png", "pic1.dds", CntEntryId.Pic1Dds),
		("pic2.png", "pic2.dds", CntEntryId.Pic2Dds)
	};

	private static readonly IReadOnlyDictionary<string, CntEntryId> NameToId = BuildNameMap();

	public static bool TryGetId(string sceSysRelativePath, out CntEntryId id)
	{
		return NameToId.TryGetValue(sceSysRelativePath, out id);
	}

	public static bool IsOuterCntFile(string sceSysRelativePath)
	{
		if (!(sceSysRelativePath == "param.json") && !(sceSysRelativePath == "pic2.png"))
		{
			return NameToId.ContainsKey(sceSysRelativePath);
		}
		return true;
	}

	public static IReadOnlyList<Entry> Collect(IReadOnlyDictionary<string, byte[]> sceSysFiles, string? applicationDrmType = null, bool generateDds = true)
	{
		ArgumentNullException.ThrowIfNull(sceSysFiles, "sceSysFiles");
		List<Entry> result = new List<Entry>();
		HashSet<uint> hashSet = new HashSet<uint>();
		(string, CntEntryId)[] mediaFiles = MediaFiles;
		for (int i = 0; i < mediaFiles.Length; i++)
		{
			var (text, cntEntryId) = mediaFiles[i];
			if (sceSysFiles.TryGetValue(text, out byte[] value) && hashSet.Add((uint)cntEntryId))
			{
				Add(cntEntryId, text, value);
			}
		}
		if (generateDds)
		{
			(string, string, CntEntryId)[] ddsMedia = DdsMedia;
			for (int i = 0; i < ddsMedia.Length; i++)
			{
				var (text2, text3, cntEntryId2) = ddsMedia[i];
				if (!hashSet.Add((uint)cntEntryId2))
				{
					continue;
				}
				byte[] value3;
				if (sceSysFiles.TryGetValue(text3, out byte[] value2))
				{
					Add(cntEntryId2, text3, value2);
				}
				else if (sceSysFiles.TryGetValue(text2, out value3))
				{
					try
					{
						Add(cntEntryId2, text3, ProsperoDdsEncoder.EncodePngToDds(value3));
					}
					catch (Exception ex) when ((ex is InvalidDataException || ex is NotSupportedException || ex is ArgumentException) ? true : false)
					{
						Console.Error.WriteLine($"  warning: skipped {text3} (could not encode {text2}: {ex.Message})");
					}
				}
			}
		}
		foreach (string item in sceSysFiles.Keys.OrderBy((string k) => k, StringComparer.OrdinalIgnoreCase))
		{
			if (!item.EndsWith(".dds", StringComparison.Ordinal) && !(item == "param.sfo") && NameToId.TryGetValue(item, out var value4) && hashSet.Add((uint)value4))
			{
				Add(value4, item, sceSysFiles[item]);
			}
		}
		return result;
		void Add(CntEntryId id, string? name, byte[] data)
		{
			ProsperoCntEntryProfile prosperoCntEntryProfile = ProsperoCntEntryPolicy.Resolve((uint)id, name, applicationDrmType);
			result.Add(new Entry(id, prosperoCntEntryProfile.IncludeName ? name : null, data, prosperoCntEntryProfile.Flags1, prosperoCntEntryProfile.Flags2));
		}
	}

	private static IReadOnlyDictionary<string, CntEntryId> BuildNameMap()
	{
		Dictionary<string, CntEntryId> dictionary = new Dictionary<string, CntEntryId>(StringComparer.Ordinal)
		{
			[".digests"] = CntEntryId.Digests,
			[".entry_keys"] = CntEntryId.EntryKeys,
			[".image_key"] = CntEntryId.ImageKey,
			[".general_digests"] = CntEntryId.GeneralDigests,
			[".metas"] = CntEntryId.Metas,
			[".entry_names"] = CntEntryId.EntryNames,
			["license.dat"] = CntEntryId.LicenseDat,
			["license.info"] = CntEntryId.LicenseInfo,
			["nptitle.dat"] = CntEntryId.NptitleDat,
			["npbind.dat"] = CntEntryId.NpbindDat,
			["selfinfo.dat"] = CntEntryId.SelfinfoDat,
			["imageinfo.dat"] = CntEntryId.ImageinfoDat,
			["target-deltainfo.dat"] = CntEntryId.TargetDeltainfoDat,
			["origin-deltainfo.dat"] = CntEntryId.OriginDeltainfoDat,
			["psreserved.dat"] = CntEntryId.PsreservedDat,
			["param.sfo"] = CntEntryId.ParamSfo,
			["playgo-chunk.dat"] = CntEntryId.PlaygoChunkDat,
			["playgo-chunk.sha"] = CntEntryId.PlaygoChunkSha,
			["playgo-manifest.xml"] = CntEntryId.PlaygoManifestXml,
			["pronunciation.sig"] = CntEntryId.PronunciationSig,
			["pronunciation.xml"] = CntEntryId.PronunciationXml,
			["pic1.png"] = CntEntryId.Pic1Png,
			["pubtoolinfo.dat"] = CntEntryId.PubtoolinfoDat,
			["app/playgo-chunk.dat"] = CntEntryId.AppPlaygoChunkDat,
			["app/playgo-chunk.sha"] = CntEntryId.AppPlaygoChunkSha,
			["app/playgo-manifest.xml"] = CntEntryId.AppPlaygoManifestXml,
			["shareparam.json"] = CntEntryId.ShareparamJson,
			["shareoverlayimage.png"] = CntEntryId.ShareoverlayimagePng,
			["save_data.png"] = CntEntryId.SaveDataPng,
			["shareprivacyguardimage.png"] = CntEntryId.ShareprivacyguardimagePng,
			["icon0.png"] = CntEntryId.Icon0Png,
			["pic0.png"] = CntEntryId.Pic0Png,
			["snd0.at9"] = CntEntryId.Snd0At9,
			["changeinfo/changeinfo.xml"] = CntEntryId.ChangeinfoXml,
			["icon0.dds"] = CntEntryId.Icon0Dds,
			["pic0.dds"] = CntEntryId.Pic0Dds,
			["pic1.dds"] = CntEntryId.Pic1Dds,
			["trophy2/trophy00.ucp"] = CntEntryId.Trophy2Ucp,
			["uds/uds00.ucp"] = CntEntryId.UdsUcp,
			["uds/npbind.dat"] = CntEntryId.UdsNpbind,
			["trophy2/npbind.dat"] = CntEntryId.Trophy2Npbind,
			["pic2.png"] = CntEntryId.Pic2Png,
			["pic2.dds"] = CntEntryId.Pic2Dds
		};
		for (int i = 0; i < 31; i++)
		{
			dictionary[$"icon0_{i:d2}.png"] = (CntEntryId)(4609 + i);
			dictionary[$"icon0_{i:d2}.dds"] = (CntEntryId)(4737 + i);
			dictionary[$"pic1_{i:d2}.png"] = (CntEntryId)(4673 + i);
			dictionary[$"pic1_{i:d2}.dds"] = (CntEntryId)(4801 + i);
			dictionary[$"changeinfo/changeinfo_{i:d2}.xml"] = (CntEntryId)(4705 + i);
			if (i < 10)
			{
				dictionary[$"keymap_rp/{i + 1:d2}.png"] = (CntEntryId)(5632 + i);
			}
			for (int j = 0; j < 10; j++)
			{
				dictionary[$"keymap_rp/{i:d2}/{j + 1:d2}.png"] = (CntEntryId)(5648 + 16 * i + j);
			}
		}
		for (int k = 0; k < 100; k++)
		{
			dictionary[$"trophy/trophy{k:d2}.trp"] = (CntEntryId)(5120 + k);
		}
		return dictionary;
	}
}
