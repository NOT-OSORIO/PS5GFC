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

using System.Diagnostics;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PS5PKGTool.Core.Builders;
using PS5PKGTool.Core.Services;

const int ExitUsage = 2;
const int ExitFailed = 1;
const int ExitCancelled = 130;

if (Environment.GetEnvironmentVariable("PS5GFC_ECOQOS") != "1") PowerThrottling.OptOut();

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    if (name.Name != "ProsperoPkgTool.Data") return null;
    string? path = Environment.GetEnvironmentVariable("PS5GFC_DATA_DLL");
    return !string.IsNullOrEmpty(path) && File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return ExitUsage;
}

// Defensive check: the console data (RSA moduli, keystone keys, right.sprx) must be reachable through
// ProsperoPkgTool.Data before anything touches it. Kept generic so the message stays stable whether the
// data is linked in at build time or supplied at run time as an external ProsperoPkgTool.Data.dll.
// NoInlining + a try/catch at both this method and its call site: when the DLL is missing, the runtime
// can throw while resolving the type the first time this method is JIT-compiled, before the try block
// inside it ever runs.
[MethodImpl(MethodImplOptions.NoInlining)]
static bool ConsoleDataReady()
{
    try
    {
        return ProsperoPkgTool.Data.Blobs.Get("passcode.bin") is { Length: > 0 };
    }
    catch (Exception)
    {
        return false;
    }
}

static bool TryConsoleDataReady()
{
    try
    {
        return ConsoleDataReady();
    }
    catch (Exception)
    {
        return false;
    }
}

static string? Value(IReadOnlyList<string> args, string name)
{
    for (int i = 0; i < args.Count - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}

static bool Has(IReadOnlyList<string> args, string name) =>
    args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

static byte[] DeterministicSeed(string contentId, string passcode) =>
    SHA256.HashData(Encoding.UTF8.GetBytes(contentId + "\0" + passcode))[..16];

string[] argv = args;
if (argv.Length == 0 || Has(argv, "--help"))
{
    Console.WriteLine("ps5pkg-builder (--source <dump-folder> | --image <exfat|ffpkg|ffpfsc>) --output <file.pkg> --content-id <content-id>");
    Console.WriteLine("               [--passcode <32-chars>] [--overwrite] [--threads N] [--temp <dir>] [--work <dir>]");
    Console.WriteLine("               [--deterministic] [--verify] [--stdin-control]");
    Console.WriteLine("ps5pkg-builder --list <file.pkg> [--meta-dir <dir>] [--passcode <32-chars>]");
    Console.WriteLine("ps5pkg-builder --extract <file.pkg> --output <dir> [--only <prefix/>] [--passcode <32-chars>]");
    return 0;
}

if (!TryConsoleDataReady())
    return Fail("PKG support needs ProsperoPkgTool.Data.dll, which is not included. Put it in the \"dll's\" folder next to PS5GFC.exe (see README).");

using var cts = new CancellationTokenSource();
if (Has(argv, "--stdin-control"))
{
    var watcher = new Thread(() =>
    {
        try
        {
            while (Console.In.ReadLine() is { } line)
                if (line.Trim().Equals("CANCEL", StringComparison.OrdinalIgnoreCase)) break;
        }
        catch (IOException) { }
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }) { IsBackground = true, Name = "stdin-control" };
    watcher.Start();
}

var gate = new object();
string lastStage = string.Empty;
long lastTick = 0;
void Emit(string stage, long done, long total, string current, bool force = false)
{
    lock (gate)
    {
        bool changed = stage != lastStage;
        bool finished = total > 0 && done >= total;
        if (!force && !changed && !finished && Stopwatch.GetElapsedTime(lastTick).TotalMilliseconds < 100) return;
        lastStage = stage;
        lastTick = Stopwatch.GetTimestamp();
        Console.WriteLine("PROGRESS " + JsonSerializer.Serialize(new { stage, done, total, current }));
    }
}

string? listPkg = Value(argv, "--list");
string? extractPkg = Value(argv, "--extract");
if (listPkg != null || extractPkg != null)
{
    string packagePasscode = Value(argv, "--passcode") ?? SonyDebugPackageCredentials.DefaultPasscode;
    try
    {
        if (listPkg != null) return PackageSource.List(listPkg, packagePasscode, Value(argv, "--meta-dir"), Emit, cts.Token);
        string? extractTo = Value(argv, "--output");
        if (string.IsNullOrWhiteSpace(extractTo)) return Fail("Missing --output.");
        return await PackageSource.ExtractAsync(extractPkg!, extractTo, packagePasscode, Value(argv, "--only"), Emit, cts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return ExitCancelled;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message.ReplaceLineEndings(" "));
        return ExitFailed;
    }
}

string? source = Value(argv, "--source");
string? image = Value(argv, "--image");
string? output = Value(argv, "--output");
string? contentId = Value(argv, "--content-id");
if (string.IsNullOrWhiteSpace(source) == string.IsNullOrWhiteSpace(image)) return Fail("Use exactly one of --source or --image.");
if (string.IsNullOrWhiteSpace(output)) return Fail("Missing --output.");
if (string.IsNullOrWhiteSpace(contentId)) return Fail("Missing --content-id.");
if (!string.IsNullOrWhiteSpace(source) && !Directory.Exists(source)) return Fail("Source folder does not exist: " + source);
if (!string.IsNullOrWhiteSpace(image) && !File.Exists(image)) return Fail("Source image does not exist: " + image);
if (File.Exists(output) && !Has(argv, "--overwrite")) return Fail("The output file already exists: " + output);

string passcode = Value(argv, "--passcode") ?? SonyDebugPackageCredentials.DefaultPasscode;
int.TryParse(Value(argv, "--threads"), out int threads);
string? tempDirectory = Value(argv, "--temp");
bool verify = Has(argv, "--verify");
Ps5InnerCompression compression = (Value(argv, "--compression") ?? "auto").ToLowerInvariant() switch
{
    "stored" or "none" => Ps5InnerCompression.Stored,
    "kraken" => Ps5InnerCompression.Kraken,
    _ => Ps5InnerCompression.Auto
};

string outputFull = Path.GetFullPath(output);
string outputDirectory = Path.GetDirectoryName(outputFull) ?? Directory.GetCurrentDirectory();
Directory.CreateDirectory(outputDirectory);

string? workArg = Value(argv, "--work");
string jobDirectory = string.IsNullOrWhiteSpace(workArg)
    ? Path.Combine(outputDirectory, ".ps5gfc-pkg-" + Guid.NewGuid().ToString("N"))
    : Path.GetFullPath(workArg);
Directory.CreateDirectory(jobDirectory);
string partial = Path.Combine(jobDirectory, Path.GetFileName(outputFull) + ".partial");

try
{
    var options = new SonyDebugPackageBuildOptions
    {
        ContentId = contentId,
        Passcode = passcode,
        TempDirectory = string.IsNullOrWhiteSpace(tempDirectory) ? null : tempDirectory,
        Log = message =>
        {
            lock (gate) Console.WriteLine("LOG " + message.ReplaceLineEndings(" "));
        },
        KrakenThreads = threads,
        Compression = compression,
        PlayGoChunkCount = 1,
        FakeSignModules = true,
        InjectRightSprx = true,
        Seed = Has(argv, "--deterministic") ? DeterministicSeed(contentId.Trim().ToUpperInvariant(), passcode) : null
    };

    var progress = new SyncProgress<SonyDebugPackageProgress>(value =>
        Emit(value.Stage, value.CompletedBytes, value.TotalBytes, value.CurrentPath));

    SonyDebugPackageBuildResult result = image is not null
        ? await VolumeDebugPackageBuilder.CreateFromImageAsync(image, partial, options, progress, cts.Token).ConfigureAwait(false)
        : await SonyDebugPackageBuilder.CreateFromDirectoryAsync(source!, partial, options, progress, cts.Token).ConfigureAwait(false);

    int verifiedFiles = 0;
    if (verify)
    {

        cts.Token.ThrowIfCancellationRequested();
        Emit("Verifying package", 0, 0, Path.GetFileName(outputFull), force: true);
        PackageReaderVerificationResult inspected = PackageReaderVerification.Inspect(partial, options.Passcode);
        if (inspected.FileCount == 0)
            throw new InvalidDataException("The built package failed verification: the reconstructed filesystem is empty.");
        if (!inspected.HasEboot)
            throw new InvalidDataException("The built package failed verification: /eboot.bin is missing.");
        cts.Token.ThrowIfCancellationRequested();
        SonyDebugPackageValidationResult validation = SonyDebugPackageBuilder.Validate(partial, options.Passcode);
        if (!validation.IsValid)
            throw new InvalidDataException("The built package failed verification: " + validation.Message);
        verifiedFiles = inspected.FileCount;
    }

    cts.Token.ThrowIfCancellationRequested();
    if (File.Exists(outputFull)) File.Delete(outputFull);
    File.Move(partial, outputFull);
    Console.WriteLine("RESULT " + JsonSerializer.Serialize(new
    {
        output = outputFull,
        contentId = result.ContentId,
        packageSize = result.PackageSize,
        sourceBytes = result.SourceBytes,
        sourceFiles = result.SourceFiles,
        verified = verify,
        verifiedFiles
    }));
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return ExitCancelled;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message.ReplaceLineEndings(" "));
    return ExitFailed;
}
finally
{
    try { if (Directory.Exists(jobDirectory)) Directory.Delete(jobDirectory, recursive: true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal static class PowerThrottling
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref ProcessPowerThrottlingState info, uint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static void OptOut()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {

            var state = new ProcessPowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 0 };
            SetProcessInformation(GetCurrentProcess(), 4, ref state, (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        catch (Exception) {   }
    }
}
