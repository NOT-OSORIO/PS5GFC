using System;

namespace ProsperoPkgTool.Crypto;

internal sealed class MersenneTwister
{
	private const int N = 624;

	private const int M = 397;

	private const uint MatrixA = 2567483615u;

	private const uint UpperMask = 2147483648u;

	private const uint LowerMask = 2147483647u;

	private const uint DefaultSeed = 19650218u;

	private readonly uint[] _mt = new uint[624];

	private int _mti;

	public MersenneTwister(ReadOnlySpan<uint> seed)
	{
		InitGenrand(19650218u);
		int num = 1;
		int num2 = 0;
		for (int num3 = Math.Max(624, seed.Length); num3 > 0; num3--)
		{
			_mt[num] = (_mt[num] ^ ((_mt[num - 1] ^ (_mt[num - 1] >> 30)) * 1664525)) + seed[num2] + (uint)num2;
			num++;
			num2++;
			if (num >= 624)
			{
				_mt[0] = _mt[623];
				num = 1;
			}
			if (num2 >= seed.Length)
			{
				num2 = 0;
			}
		}
		for (int num4 = 623; num4 > 0; num4--)
		{
			_mt[num] = (_mt[num] ^ ((_mt[num - 1] ^ (_mt[num - 1] >> 30)) * 1566083941)) - (uint)num;
			num++;
			if (num >= 624)
			{
				_mt[0] = _mt[623];
				num = 1;
			}
		}
		_mt[0] = 2147483648u;
	}

	private void InitGenrand(uint seed)
	{
		_mt[0] = seed;
		for (int i = 1; i < 624; i++)
		{
			_mt[i] = 1812433253 * (_mt[i - 1] ^ (_mt[i - 1] >> 30)) + (uint)i;
		}
		_mti = 624;
	}

	public uint NextUInt32()
	{
		if (_mti >= 624)
		{
			int i;
			for (i = 0; i < 227; i++)
			{
				uint num = (_mt[i] & 0x80000000u) | (_mt[i + 1] & 0x7FFFFFFF);
				_mt[i] = _mt[i + 397] ^ (num >> 1) ^ (uint)(((num & 1) != 0) ? (-1727483681) : 0);
			}
			for (; i < 623; i++)
			{
				uint num2 = (_mt[i] & 0x80000000u) | (_mt[i + 1] & 0x7FFFFFFF);
				_mt[i] = _mt[i + -227] ^ (num2 >> 1) ^ (uint)(((num2 & 1) != 0) ? (-1727483681) : 0);
			}
			uint num3 = (_mt[623] & 0x80000000u) | (_mt[0] & 0x7FFFFFFF);
			_mt[623] = _mt[396] ^ (num3 >> 1) ^ (uint)(((num3 & 1) != 0) ? (-1727483681) : 0);
			_mti = 0;
		}
		uint num4 = _mt[_mti++];
		uint num5 = num4 ^ (num4 >> 11);
		uint num6 = num5 ^ ((num5 << 7) & 0x9D2C5680u);
		uint num7 = num6 ^ ((num6 << 15) & 0xEFC60000u);
		return num7 ^ (num7 >> 18);
	}
}
