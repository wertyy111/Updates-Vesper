using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

internal static class InstallerAssets
{
    public static Image? LoadHeroImage()
    {
        return LoadImageResource("vesper-launcher-wordmark.png", "vesper-launcher-wordmark.jpg", "vesper-launcher-wordmark.jpeg");
    }

    public static Image? LoadLogoImage()
    {
        var image = LoadImageResource("vesper-logo.png", "vesper-logo.jpg", "vesper-logo.jpeg");
        if (image is not Bitmap bitmap)
        {
            return image;
        }

        if (bitmap.PixelFormat == PixelFormat.Format32bppArgb || bitmap.PixelFormat == PixelFormat.Format64bppArgb)
        {
            return bitmap;
        }

        return RemoveBackgroundByCornerColor(bitmap);
    }

    public static string LoadLicenseText()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name =>
                    name.EndsWith("installer-license.txt", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("license.txt", StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                return GetBundledFallbackLicenseText();
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return GetBundledFallbackLicenseText();
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return GetBundledFallbackLicenseText();
        }
    }

    public static void WriteLicenseFile(string installDir)
    {
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "LICENSE.txt"), LoadLicenseText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static Image? LoadImageResource(params string[] endings)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => endings.Any(ending => name.EndsWith(ending, StringComparison.OrdinalIgnoreCase)));

            if (resourceName is null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            return stream is null ? null : new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap RemoveBackgroundByCornerColor(Bitmap source)
    {
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var corner = source.GetPixel(0, 0);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                var distance = Math.Abs(pixel.R - corner.R) + Math.Abs(pixel.G - corner.G) + Math.Abs(pixel.B - corner.B);
                var alpha = distance <= 95 ? 0 : 255;
                bitmap.SetPixel(x, y, Color.FromArgb(alpha, pixel.R, pixel.G, pixel.B));
            }
        }

        source.Dispose();
        return bitmap;
    }

    private static string GetBundledFallbackLicenseText()
    {
        return "Лицензионное соглашение Vesper Launcher" +
            Environment.NewLine +
            Environment.NewLine +
            "Устанавливая или используя Vesper Launcher, ты принимаешь условия лицензии, приложенной к этому приложению.";
    }
}
