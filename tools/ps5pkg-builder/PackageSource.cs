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

using System.Buffers.Binary;
using System.Text.Json;
using ProsperoPkgTool.Containers;
using ProsperoPkgTool.Crypto;

internal static class PackageSource
{

    private sealed class OpenPackage : IDisposable
    {
        public required string ContentId { get; init; }
        public required IReadOnlyList<ProsperoInnerPfsReader.Entry> Entries { get; init; }
        public required Func<Stream> OpenMount { get; init; }
        public IDisposable? Owner { get; init; }

        public IReadOnlyList<ContentFile> Content { get; init; } = [];

        public void Dispose() => Owner?.Dispose();
    }

    private sealed record ContentFile(string Path, long Size, Func<byte[]> Read);

    private static readonly (uint Id, string Path)[] ContentEntries =
    [
        (0x0402, "sce_sys/nptitle.dat"),
        (0x0403, "sce_sys/npbind.dat"),
        (0x040A, "sce_sys/imagedigs.dat"),
        (0x1000, "sce_sys/param.sfo"),
        (0x1001, "sce_sys/playgo-chunk.dat"),
        (0x1200, "sce_sys/icon0.png"),
        (0x1220, "sce_sys/pic0.png"),
        (0x1240, "sce_sys/snd0.at9"),
        (0x1280, "sce_sys/icon0.dds"),
        (0x12A0, "sce_sys/pic0.dds"),
        (0x12C0, "sce_sys/pic1.dds"),
        (0x1480, "sce_sys/trophy2/trophy00.ucp"),
        (0x14A0, "sce_sys/uds/uds00.ucp"),
        (0x2000, "sce_sys/param.json"),
        (0x2010, "sce_sys/playgo-hash-table.dat"),
        (0x2011, "sce_sys/playgo-ficm.dat"),
        (0x2020, "sce_sys/uds/npbind.dat"),
        (0x2021, "sce_sys/trophy2/npbind.dat"),
        (0x2060, "sce_sys/pic2.dds")
    ];

    private static readonly HashSet<string> PfsInternalFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "inode_flat_path_table", "apr_flat_path_table", "afid_to_ino_table"
    };

    private static OpenPackage Open(string packagePath, string passcode)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("The package was not found.", packagePath);
        string full = Path.GetFullPath(packagePath);
        ProsperoPackageInspection inspection = ProsperoPackageReader.Read(full);
        bool isDebug = inspection.Kind is ProsperoPackageKind.FinalizedDebug or ProsperoPackageKind.FinalizedPatchDebug;
        if (inspection.Fih is null || inspection.Fih.PfsSize == 0 || !isDebug)
            throw new InvalidDataException("Only debug/FPKG packages can be opened (this one is " + inspection.Kind + ").");

        string? key = passcode.Length == 0 ? null : passcode;
        IReadOnlyList<ContentFile> content = ContentOf(full, inspection, passcode);
        try
        {
            if (ProsperoPackageContent.RequiresFileBacked(full, key))
            {
                ProsperoFileBackedPackage fileBacked = ProsperoPackageContent.ReadFileBacked(full, key);
                return new OpenPackage
                {
                    ContentId = inspection.Cnt.ContentId,
                    Entries = fileBacked.Entries,
                    OpenMount = fileBacked.OpenMount,
                    Owner = fileBacked,
                    Content = content
                };
            }
            ProsperoDecodedPackage decoded = ProsperoPackageContent.Read(full, key);
            return new OpenPackage
            {
                ContentId = inspection.Cnt.ContentId,
                Entries = ProsperoInnerPfsReader.Enumerate(decoded.Mount),
                OpenMount = () => new MemoryStream(decoded.Mount, writable: false),
                Content = content
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException((ProsperoErrorInfo.IsUnsupported(ex)
                ? "This package format is not supported yet: "
                : "The package could not be decoded (wrong passcode?): ") + ex.Message, ex);
        }
    }

    private static IReadOnlyList<ContentFile> ContentOf(string packagePath, ProsperoPackageInspection inspection, string passcode)
    {
        var list = new List<ContentFile>();
        foreach ((uint id, string path) in ContentEntries)
        {
            ProsperoCntEntry? entry = inspection.Entries.FirstOrDefault(candidate => candidate.Id == id);
            if (entry is null || entry.DataSize == 0) continue;
            ProsperoCntEntry captured = entry;
            list.Add(new ContentFile(path, entry.DataSize, () => captured.IsEncrypted
                ? ReadProtected(packagePath, inspection, captured, passcode)
                : ProsperoPackageContent.ReadCntEntry(packagePath, inspection, captured, captured.DataSize)));
        }
        return list;
    }

    private static byte[] ReadProtected(string packagePath, ProsperoPackageInspection inspection, ProsperoCntEntry entry, string passcode)
    {
        string pass = passcode.Length == 0 ? "00000000000000000000000000000000" : passcode;
        string contentId = inspection.Cnt.ContentId;
        if (pass.Length != 32 || string.IsNullOrWhiteSpace(contentId))
            throw new InvalidDataException($"CNT entry 0x{entry.Id:X4} is encrypted and needs the passcode and the content id.");
        long padded = ProsperoEntryCipher.PaddedLength(checked((int)entry.DataSize));
        byte[] ciphertext = ProsperoPackageContent.ReadCntEntry(packagePath, inspection, entry, padded);
        byte[] metaRow = new byte[ProsperoEntryCipher.MetaRowSize];
        BinaryPrimitives.WriteUInt32BigEndian(metaRow.AsSpan(0), entry.Id);
        BinaryPrimitives.WriteUInt32BigEndian(metaRow.AsSpan(4), entry.NameOffset);
        BinaryPrimitives.WriteUInt32BigEndian(metaRow.AsSpan(8), entry.Flags1);
        BinaryPrimitives.WriteUInt32BigEndian(metaRow.AsSpan(12), entry.Flags2);
        BinaryPrimitives.WriteUInt32BigEndian(metaRow.AsSpan(16), entry.DataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(metaRow.AsSpan(20), entry.DataSize);
        uint keyIndex = (entry.Flags2 >> 12) & 0xF;
        foreach (bool publisherProfile in new[] { true, false })
        {
            byte[] plain;
            try { plain = ProsperoEntryCipher.Decrypt(ciphertext, metaRow, contentId, pass, keyIndex, publisherProfile); }
            catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException) { continue; }
            if (plain.Length >= entry.DataSize && IsRecognized(entry.Id, plain)) return plain[..(int)entry.DataSize];
        }
        throw new InvalidDataException($"CNT entry 0x{entry.Id:X4} could not be decrypted with this passcode.");
    }

    private static bool IsRecognized(uint id, byte[] plain) => id switch
    {
        0x0402 => plain.Length >= 4 && plain[0] == (byte)'N' && plain[1] == (byte)'P' && plain[2] == (byte)'T' && plain[3] == (byte)'D',
        0x2020 or 0x2021 or 0x0403 => plain.Length >= 4 && plain[0] == 0xD2 && plain[1] == 0x94 && plain[2] == 0xA0 && plain[3] == 0x18,
        _ => true
    };

    private static string Relative(ProsperoInnerPfsReader.Entry entry)
    {
        string value = entry.Path.Replace('\\', '/').TrimStart('/');
        if (value.StartsWith("uroot/", StringComparison.OrdinalIgnoreCase)) return value[6..];
        return string.Equals(value, "uroot", StringComparison.OrdinalIgnoreCase) ? string.Empty : value;
    }

    public static int List(string packagePath, string passcode, string? metaDirectory, Action<string, long, long, string, bool> progress, CancellationToken token)
    {
        using OpenPackage package = Open(packagePath, passcode);
        long total = 0;
        int count = 0;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProsperoInnerPfsReader.Entry file in package.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (file.IsDirectory) continue;
            string relative = Relative(file);
            if (relative.Length == 0 || PfsInternalFiles.Contains(relative)) continue;
            present.Add(relative);
            Console.WriteLine("ENTRY " + JsonSerializer.Serialize(new { path = relative, size = file.Size }));
            total += file.Size;
            count++;
        }
        foreach (ContentFile item in package.Content)
        {
            if (!present.Add(item.Path)) continue;
            Console.WriteLine("ENTRY " + JsonSerializer.Serialize(new { path = item.Path, size = item.Size }));
            total += item.Size;
            count++;
        }
        if (count == 0) throw new InvalidDataException("The package has no readable files.");

        if (!string.IsNullOrWhiteSpace(metaDirectory)) ExtractCore(package, packagePath, metaDirectory, "sce_sys/", progress, token);
        Console.WriteLine("RESULT " + JsonSerializer.Serialize(new { files = count, bytes = total, contentId = package.ContentId }));
        return 0;
    }

    public static Task<int> ExtractAsync(string packagePath, string destination, string passcode, string? only,
        Action<string, long, long, string, bool> progress, CancellationToken token)
    {
        using OpenPackage package = Open(packagePath, passcode);
        return Task.FromResult(ExtractCore(package, packagePath, destination, only, progress, token));
    }

    private static int ExtractCore(OpenPackage package, string packagePath, string destination, string? only,
        Action<string, long, long, string, bool> progress, CancellationToken token)
    {
        string root = Path.GetFullPath(destination);
        string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        var selected = new List<(ProsperoInnerPfsReader.Entry File, string Relative)>();
        foreach (ProsperoInnerPfsReader.Entry file in package.Entries)
        {
            if (file.IsDirectory) continue;
            string relative = Relative(file);
            if (relative.Length == 0 || PfsInternalFiles.Contains(relative)) continue;
            if (!string.IsNullOrEmpty(only) && !relative.StartsWith(only, StringComparison.OrdinalIgnoreCase)) continue;
            selected.Add((file, relative));
        }
        var extra = package.Content
            .Where(item => string.IsNullOrEmpty(only) || item.Path.StartsWith(only, StringComparison.OrdinalIgnoreCase))
            .Where(item => !selected.Any(chosen => string.Equals(chosen.Relative, item.Path, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        long total = selected.Sum(item => item.File.Size) + extra.Sum(item => item.Size);
        long done = 0;
        progress("Extracting package", 0, total, Path.GetFileName(packagePath), true);
        using Stream mount = package.OpenMount();
        foreach ((ProsperoInnerPfsReader.Entry file, string relative) in selected)
        {
            token.ThrowIfCancellationRequested();
            if (Path.IsPathRooted(relative)) throw new InvalidDataException("Package path is unsafe: " + relative);
            string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package path escapes the extraction folder: " + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            long fileStart = done;
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
            using var counting = new CountingStream(output, written =>
            {
                token.ThrowIfCancellationRequested();
                done = fileStart + written;
                progress("Extracting package", done, total, relative, false);
            });
            ProsperoInnerPfsReader.ExtractFile(mount, file, counting);
            done = fileStart + file.Size;
        }
        foreach (ContentFile item in extra)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path escapes the extraction folder: " + item.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, item.Read());
            done += item.Size;
            progress("Extracting package", done, total, item.Path, false);
        }
        progress("Extracting package", total, total, string.Empty, true);
        Console.WriteLine("RESULT " + JsonSerializer.Serialize(new { output = root, files = selected.Count + extra.Count, bytes = done }));
        return 0;
    }

    private sealed class CountingStream(Stream inner, Action<long> onWrite) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _written += buffer.Length;
            onWrite(_written);
        }
    }
}
