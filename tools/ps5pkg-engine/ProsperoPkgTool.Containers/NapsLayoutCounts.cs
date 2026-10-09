namespace ProsperoPkgTool.Containers;

public readonly record struct NapsLayoutCounts(int NumFiles, byte CompressionType, int NumKeys, int NumShufflePatterns, int NumUBlocks, int NumOuterBlocks, int NumCblockInfo)
{
	public int NumU2cEntries => NumUBlocks + 8 >> 3;

	public int NumFileOffsetEntries => NumFiles;
}
