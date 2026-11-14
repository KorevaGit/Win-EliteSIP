using System.IO;
using System.Windows.Media.Imaging;
using EliteSIP.App.Theme;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var assets = Path.GetFullPath(args.Length > 0 ? args[0] : "src/EliteSIP.App/Assets/AppIcon");
        var frames = new List<(int Size, byte[] Bytes)>();
        foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512 })
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BrandLogo.Render(size)));
            using var memory = new MemoryStream();
            encoder.Save(memory);
            var png = memory.ToArray();
            File.WriteAllBytes(Path.Combine(assets, $"icon_{size}x{size}.png"), png);
            // ICO width byte 0 means exactly 256, never 512.
            if (size <= 256)
            {
                frames.Add((size, png));
            }
        }
        using var writer = new BinaryWriter(File.Create(Path.Combine(assets, "EliteSIP.ico")));
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)frames.Count);
        var offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(png.Length); writer.Write(offset); offset += png.Length;
        }
        foreach (var (_, png) in frames)
        {
            writer.Write(png);
        }
        Console.WriteLine("Approved gold artwork: PNG 16–512; ICO 16–256.");
    }
}
