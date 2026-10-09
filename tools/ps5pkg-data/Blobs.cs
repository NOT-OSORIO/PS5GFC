// PS5GFC — PS5 Game Format Converter
// Copyright (C) 2026 OSØRIO
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Reflection;

namespace ProsperoPkgTool.Data;

public static class Blobs
{
	public static byte[]? Get(string name)
	{
		using Stream? stream = typeof(Blobs).GetTypeInfo().Assembly.GetManifestResourceStream(name);
		if (stream == null)
		{
			return null;
		}
		byte[] buffer = new byte[stream.Length];
		stream.ReadExactly(buffer);
		return buffer;
	}

	public static byte[] Require(string name)
	{
		return Get(name) ?? Array.Empty<byte>();
	}
}
