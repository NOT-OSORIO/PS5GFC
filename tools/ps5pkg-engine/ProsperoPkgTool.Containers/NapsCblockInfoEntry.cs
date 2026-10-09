namespace ProsperoPkgTool.Containers;

public readonly record struct NapsCblockInfoEntry
{
	public byte[] Raw { get; init; }

	public bool IsRunBase { get; init; }

	public uint CoffsetStartMod256K { get; init; }

	public uint UoffsetStart { get; init; }

	public uint ClenEvenMinus1 { get; init; }

	public byte Even { get; init; }

	public byte Odd { get; init; }

	public byte KdePredictor { get; init; }

	public byte ShuffleIdx { get; init; }

	public uint CoffsetEndMod256K { get; init; }

	public uint TweakIdxStart { get; init; }

	public byte KeyTableIdx { get; init; }

	public uint CoffsetStart256K { get; init; }
}
