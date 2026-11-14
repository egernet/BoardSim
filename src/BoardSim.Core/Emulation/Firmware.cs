using System.Globalization;

namespace BoardSim.Core.Emulation;

/// <summary>Finds QEMU and turns an ESP-IDF build into a flash image for it.</summary>
public static class Firmware
{
    public const int FlashSize = 4 * 1024 * 1024;

    /// <summary>
    /// Builds a flash image from an ESP-IDF build folder (its flash_args file),
    /// or accepts an already merged image. Returns the path QEMU should use.
    /// </summary>
    public static string PrepareImage(string path, string workDir)
    {
        if (File.Exists(path) && path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            var len = new FileInfo(path).Length;
            if (len is 2 << 20 or 4 << 20 or 8 << 20 or 16 << 20) return CopyTo(path, workDir);
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} is {len} bytes. Pick the ESP-IDF build folder instead, or a merged image of 2, 4, 8 or 16 MB.");
        }
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var args = Path.Combine(dir, "flash_args");
        if (!File.Exists(args) && Directory.Exists(Path.Combine(dir, "build"))) { dir = Path.Combine(dir, "build"); args = Path.Combine(dir, "flash_args"); }
        if (!File.Exists(args))
            throw new InvalidOperationException($"No flash_args in {dir}. Pick the build folder of an ESP-IDF project (after idf.py build).");

        var image = new byte[FlashSize];
        Array.Fill(image, (byte)0xff);
        foreach (var line in File.ReadAllLines(args))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !parts[0].StartsWith("0x")) continue;
            var offset = int.Parse(parts[0][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var data = File.ReadAllBytes(Path.Combine(dir, parts[1]));
            if (offset + data.Length > image.Length) throw new InvalidOperationException($"{parts[1]} does not fit in 4 MB of flash");
            Buffer.BlockCopy(data, 0, image, offset, data.Length);
        }
        Directory.CreateDirectory(workDir);
        var output = Path.Combine(workDir, "flash.bin");
        File.WriteAllBytes(output, image);
        return output;
    }

    private static string CopyTo(string file, string workDir)
    {
        // QEMU writes to the image (NVS, FAT); keep the user's file untouched.
        Directory.CreateDirectory(workDir);
        var output = Path.Combine(workDir, "flash.bin");
        File.Copy(file, output, overwrite: true);
        return output;
    }

    /// <summary>
    /// Looks for BoardSim's QEMU fork: $BOARDSIM_QEMU, a qemu folder next to
    /// the app, ~/.boardsim/qemu, or a development checkout next to the repo.
    /// </summary>
    public static (string Qemu, string? Rom)? FindQemu()
    {
        var exe = OperatingSystem.IsWindows() ? "qemu-system-riscv32.exe" : "qemu-system-riscv32";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("BOARDSIM_QEMU"),
            Path.Combine(AppContext.BaseDirectory, "qemu", exe),
            Path.Combine(AppContext.BaseDirectory, "qemu", "bin", exe),
            Path.Combine(home, ".boardsim", "qemu", exe),
            Path.Combine(home, ".boardsim", "qemu", "bin", exe),
        };
        // Development: BoardSim/../qemu-esp/build
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            candidates.Add(Path.Combine(d.FullName, "qemu-esp", "build", exe));

        foreach (var c in candidates.Where(c => c is not null && File.Exists(c)))
        {
            var dir = Path.GetDirectoryName(c)!;
            var rom = new[]
            {
                dir, Path.Combine(dir, "..", "share", "qemu"), Path.Combine(dir, "pc-bios"),
                Path.Combine(dir, "..", "src", "pc-bios"),
            }.FirstOrDefault(r => File.Exists(Path.Combine(r, "esp32c3-rom.bin")));
            return (c!, rom is null ? null : Path.GetFullPath(rom));
        }
        return null;
    }
}
