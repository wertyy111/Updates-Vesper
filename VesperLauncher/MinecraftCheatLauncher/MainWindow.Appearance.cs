using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace VesperLauncher;

public partial class MainWindow : Window
{
    private void TryApplyCustomLogoImage()
    {
        string[] assetDirectories =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets")),
            Path.Combine(AppContext.BaseDirectory, "Assets")
        ];
        var uiLogoPath = ResolveBestLogoPath(assetDirectories);
        var leftMenuLogoPath = ResolveBestWordmarkPath(assetDirectories) ?? uiLogoPath;

        if (string.IsNullOrWhiteSpace(uiLogoPath) && string.IsNullOrWhiteSpace(leftMenuLogoPath))
        {
            return;
        }

        try
        {
            BitmapImage? uiBitmap = null;
            if (!string.IsNullOrWhiteSpace(uiLogoPath))
            {
                uiBitmap = LoadBitmapFromFile(uiLogoPath, decodePixelWidth: null);
                TopLeftLogoImage.Source = uiBitmap;
            }

            if (LeftMenuLogoImage is not null && !string.IsNullOrWhiteSpace(leftMenuLogoPath))
            {
                LeftMenuLogoImage.Source = LoadBitmapFromFile(leftMenuLogoPath, decodePixelWidth: null);
            }
            else if (LeftMenuLogoImage is not null && uiBitmap is not null)
            {
                LeftMenuLogoImage.Source = uiBitmap;
            }
        }
        catch (Exception ex)
        {
            ShowError(ex, "Ошибка логотипа");
        }
    }

    private static string? ResolveBestLogoPath(IEnumerable<string> assetDirectories)
    {
        string[] preferredNames =
        [
            "vesper-logo-3k.png",
            "vesper-logo.png",
            "logo.png",
            "icon.png",
            "vesper-logo-3k.jpg",
            "vesper-logo-3k.jpeg",
            "vesper-logo.jpg",
            "vesper-logo.jpeg",
            "logo.jpg",
            "logo.jpeg",
            "icon.jpg",
            "icon.jpeg"
        ];

        var existingDirectories = assetDirectories
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var directory in existingDirectories)
        {
            foreach (var fileName in preferredNames)
            {
                var candidatePath = Path.Combine(directory, fileName);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        var logoFiles = existingDirectories
            .SelectMany(Directory.EnumerateFiles)
            .Where(IsSupportedLogoFile)
            .Select(path => new FileInfo(path))
            .ToArray();

        if (logoFiles.Length == 0)
        {
            return null;
        }

        var themedCandidate = logoFiles
            .Where(file => HasLogoLikeName(file.Name))
            .OrderByDescending(file => file.Length)
            .FirstOrDefault();

        return themedCandidate?.FullName
               ?? logoFiles.OrderByDescending(file => file.Length).First().FullName;
    }

    private static string? ResolveBestWordmarkPath(IEnumerable<string> assetDirectories)
    {
        string[] preferredNames =
        [
            "vesper-launcher-wordmark.png",
            "vesper-launcher-wordmark.jpg",
            "vesper-launcher-wordmark.jpeg",
            "vesper-wordmark.png",
            "vesper-wordmark.jpg",
            "vesper-wordmark.jpeg"
        ];

        var existingDirectories = assetDirectories
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var directory in existingDirectories)
        {
            foreach (var fileName in preferredNames)
            {
                var candidatePath = Path.Combine(directory, fileName);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        var wordmarkFiles = existingDirectories
            .SelectMany(Directory.EnumerateFiles)
            .Where(IsSupportedLogoFile)
            .Select(path => new FileInfo(path))
            .Where(file => HasWordmarkLikeName(file.Name))
            .OrderByDescending(file => file.Length)
            .ToArray();

        return wordmarkFiles.Length > 0 ? wordmarkFiles[0].FullName : null;
    }

    private static bool IsSupportedLogoFile(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        IsJpgFile(path);

    private static BitmapImage LoadBitmapFromFile(string path, int? decodePixelWidth)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (decodePixelWidth.HasValue && decodePixelWidth.Value > 0)
        {
            bitmap.DecodePixelWidth = decodePixelWidth.Value;
        }
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateSquareIconBitmap(BitmapSource source, int targetSize)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return source;
        }

        var cropSide = Math.Min(source.PixelWidth, source.PixelHeight);
        var cropX = (source.PixelWidth - cropSide) / 2;
        var cropY = (source.PixelHeight - cropSide) / 2;

        BitmapSource squareSource = source;
        if (cropX > 0 || cropY > 0)
        {
            var cropped = new CroppedBitmap(source, new Int32Rect(cropX, cropY, cropSide, cropSide));
            cropped.Freeze();
            squareSource = cropped;
        }

        if (cropSide == targetSize || targetSize <= 0)
        {
            return squareSource;
        }

        var scale = (double)targetSize / cropSide;
        var scaled = new TransformedBitmap(squareSource, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static BitmapSource CreateRecommendedModIconBitmap(BitmapSource source, int targetSize)
    {
        var trimmedSource = TrimTransparentEdges(source);
        var widthRatio = source.PixelWidth > 0
            ? (double)trimmedSource.PixelWidth / source.PixelWidth
            : 1d;
        var heightRatio = source.PixelHeight > 0
            ? (double)trimmedSource.PixelHeight / source.PixelHeight
            : 1d;
        var removedAreaRatio = 1d - (widthRatio * heightRatio);
        var aspectRatio = trimmedSource.PixelHeight > 0
            ? (double)trimmedSource.PixelWidth / trimmedSource.PixelHeight
            : 1d;
        var useCoverCrop = aspectRatio is >= 0.78 and <= 1.28 && removedAreaRatio <= 0.52;
        if (useCoverCrop)
        {
            return CreateCoverSquareIconBitmap(trimmedSource, targetSize, zoomMultiplier: 1.12);
        }

        var useTrimmedSource = removedAreaRatio >= 0.35 || widthRatio <= 0.78 || heightRatio <= 0.78;
        var preferredSource = useTrimmedSource ? trimmedSource : source;
        var paddingRatio = useTrimmedSource ? 0.08 : 0.04;
        return CreateContainedSquareIconBitmap(preferredSource, targetSize, paddingRatio);
    }

    private static BitmapSource CreateCoverSquareIconBitmap(BitmapSource source, int targetSize, double zoomMultiplier)
    {
        if (targetSize <= 0 || source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return source;
        }

        var cropSide = Math.Min(source.PixelWidth, source.PixelHeight);
        var cropX = Math.Max(0, (source.PixelWidth - cropSide) / 2);
        var cropY = Math.Max(0, (source.PixelHeight - cropSide) / 2);

        BitmapSource squareSource = source;
        if (cropX > 0 || cropY > 0 || source.PixelWidth != source.PixelHeight)
        {
            var cropped = new CroppedBitmap(source, new Int32Rect(cropX, cropY, cropSide, cropSide));
            cropped.Freeze();
            squareSource = cropped;
        }

        var safeZoomMultiplier = Math.Clamp(zoomMultiplier, 1d, 1.35d);
        var drawSize = Math.Max(1d, Math.Round(targetSize * safeZoomMultiplier));
        var offset = Math.Round((targetSize - drawSize) / 2d);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, targetSize, targetSize));
            context.DrawImage(squareSource, new Rect(offset, offset, drawSize, drawSize));
        }

        var bitmap = new RenderTargetBitmap(
            targetSize,
            targetSize,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateContainedSquareIconBitmap(BitmapSource source, int targetSize, double paddingRatio)
    {
        if (targetSize <= 0 || source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return source;
        }

        var safePaddingRatio = Math.Clamp(paddingRatio, 0d, 0.45d);
        var maxContentSize = Math.Max(1d, targetSize * (1d - safePaddingRatio * 2d));
        var scale = Math.Min(maxContentSize / source.PixelWidth, maxContentSize / source.PixelHeight);
        scale = Math.Min(scale, 1.5d);

        var drawWidth = Math.Max(1d, Math.Round(source.PixelWidth * scale));
        var drawHeight = Math.Max(1d, Math.Round(source.PixelHeight * scale));
        var offsetX = Math.Round((targetSize - drawWidth) / 2d);
        var offsetY = Math.Round((targetSize - drawHeight) / 2d);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, targetSize, targetSize));
            context.DrawImage(source, new Rect(offsetX, offsetY, drawWidth, drawHeight));
        }

        var bitmap = new RenderTargetBitmap(
            targetSize,
            targetSize,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource TrimTransparentEdges(BitmapSource source)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return source;
        }

        BitmapSource workingSource = source;
        if (workingSource.Format != PixelFormats.Bgra32 &&
            workingSource.Format != PixelFormats.Pbgra32)
        {
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = source;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            converted.Freeze();
            workingSource = converted;
        }

        var width = workingSource.PixelWidth;
        var height = workingSource.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        workingSource.CopyPixels(pixels, stride, 0);

        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var alpha = pixels[(y * stride) + (x * 4) + 3];
                if (alpha <= 10)
                {
                    continue;
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return source;
        }

        var cropWidth = maxX - minX + 1;
        var cropHeight = maxY - minY + 1;
        if (cropWidth == width && cropHeight == height)
        {
            return workingSource;
        }

        var cropped = new CroppedBitmap(workingSource, new Int32Rect(minX, minY, cropWidth, cropHeight));
        cropped.Freeze();
        return cropped;
    }

    private static bool IsJpgFile(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLogoLikeName(string fileName)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return nameWithoutExtension.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0 ||
               nameWithoutExtension.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0 ||
               nameWithoutExtension.IndexOf("vesper", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasWordmarkLikeName(string fileName)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return nameWithoutExtension.IndexOf("wordmark", StringComparison.OrdinalIgnoreCase) >= 0 ||
               nameWithoutExtension.IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void TryLoadClickSound()
    {
        string[] assetDirectories =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets"))
        ];

        string[] preferredNames =
        [
            "click.wav",
            "click.mp3",
            "soft-click-button.wav",
            "soft-click-button.mp3"
        ];

        var soundPath = assetDirectories
            .Where(Directory.Exists)
            .SelectMany(directory => preferredNames.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);

        if (string.IsNullOrWhiteSpace(soundPath))
        {
            return;
        }

        try
        {
            _clickSoundPlayer.Open(new Uri(soundPath, UriKind.Absolute));
            _clickSoundPlayer.Volume = 0.55;
            _clickSoundLoaded = true;
        }
        catch (Exception ex)
        {
            _clickSoundLoaded = false;
            ShowError(ex, "Ошибка звука");
        }
    }

    private void PlayClickSound(object sender, RoutedEventArgs e)
    {
        if (!_clickSoundLoaded || !IsLauncherClickSoundEnabled())
        {
            return;
        }

        try
        {
            _clickSoundPlayer.Stop();
            _clickSoundPlayer.Position = TimeSpan.Zero;
            _clickSoundPlayer.Play();
        }
        catch
        {
            // ignore
        }
    }

    private void TryApplyCustomBackgroundImage()
    {
        var candidates = GetBackgroundImageCandidates();
        var imagePath = ResolveBackgroundImagePath(candidates, DateTime.Now);
        var sceneKind = ResolveBackgroundSceneKind(imagePath);

        ApplyGlassTheme(sceneKind);

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            _appliedBackgroundImagePath = null;
            BackgroundPhoto.Source = null;
            BackgroundPhoto.Visibility = Visibility.Collapsed;
            ProceduralBackground.Visibility = Visibility.Visible;
            return;
        }

        if (string.Equals(_appliedBackgroundImagePath, imagePath, StringComparison.OrdinalIgnoreCase))
        {
            BackgroundPhoto.Visibility = Visibility.Visible;
            ProceduralBackground.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            BackgroundPhoto.Source = bitmap;
            BackgroundPhoto.Visibility = Visibility.Visible;
            ProceduralBackground.Visibility = Visibility.Collapsed;
            _appliedBackgroundImagePath = imagePath;
        }
        catch (Exception ex)
        {
            ShowError(ex, "Ошибка фона");
        }
    }

    private static FileInfo[] GetBackgroundImageCandidates()
    {
        string[] assetRoots =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets"))
        ];

        var assetDirectories = assetRoots
            .Where(Directory.Exists)
            .SelectMany(directory => new[]
            {
                directory,
                Path.Combine(directory, "Backgrounds")
            })
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] allowedExtensions = [".jpg", ".jpeg", ".png", ".bmp"];
        return assetDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory))
            .Where(path => allowedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path =>
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                return fileName.IndexOf("logo", StringComparison.OrdinalIgnoreCase) < 0 &&
                       fileName.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0;
            })
            .Select(path => new FileInfo(path))
            .GroupBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string? ResolveBackgroundImagePath(IReadOnlyList<FileInfo> candidates, DateTime nowLocalTime)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var timedCandidates = BuildTimedBackgroundCandidates(candidates);
        if (timedCandidates.Count > 0)
        {
            var currentMinuteOfDay = (nowLocalTime.Hour * 60) + nowLocalTime.Minute;
            var activeTimedCandidate = timedCandidates
                .Where(candidate => IsMinuteInsideRange(currentMinuteOfDay, candidate.StartMinuteOfDay, candidate.EndMinuteOfDay))
                .OrderByDescending(candidate => candidate.Priority)
                .ThenBy(candidate => GetRangeDurationMinutes(candidate.StartMinuteOfDay, candidate.EndMinuteOfDay))
                .ThenByDescending(candidate => candidate.File.Length)
                .Select(candidate => candidate.File.FullName)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(activeTimedCandidate))
            {
                return activeTimedCandidate;
            }
        }

        string[] preferredNames =
        [
            "background.jpg",
            "background.png",
            "background.jpeg",
            "background.bmp"
        ];

        var preferredCandidate = candidates
            .FirstOrDefault(file => preferredNames.Contains(file.Name, StringComparer.OrdinalIgnoreCase))
            ?.FullName;
        if (!string.IsNullOrWhiteSpace(preferredCandidate))
        {
            return preferredCandidate;
        }

        return candidates
            .OrderByDescending(file => file.Length)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private BackgroundSceneKind ResolveBackgroundSceneKind(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return BackgroundSceneKind.Night;
        }

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(imagePath);
        if (TryResolveNamedBackgroundSceneKind(fileNameWithoutExtension, out var namedSceneKind))
        {
            return namedSceneKind;
        }

        return TryParseBackgroundTimeRange(fileNameWithoutExtension, out var startMinuteOfDay, out var endMinuteOfDay)
            ? InferBackgroundSceneKind(startMinuteOfDay, endMinuteOfDay)
            : BackgroundSceneKind.Unknown;
    }

    private void ApplyGlassTheme(BackgroundSceneKind sceneKind)
    {
        // Keep controls readable and consistent regardless of the selected background image.
        // Backgrounds may rotate by time of day, but the launcher chrome should stay in the
        // daytime palette so buttons and small details do not suddenly become blue/night-toned.
        var tone = GlassThemeTone.Light;

        if (_appliedGlassThemeTone == tone)
        {
            return;
        }

        if (tone == GlassThemeTone.Light)
        {
            ApplyLightGlassTheme();
        }
        else
        {
            ApplyDarkGlassTheme();
        }

        _appliedGlassThemeTone = tone;
    }

    private void ApplyDarkGlassTheme()
    {
        SetGradientBrushPalette("GlassButtonBackgroundBrush", "#68161616", "#52121212", "#440F0F0F");
        SetGradientBrushPalette("GlassButtonHoverBrush", "#74202020", "#5B1A1A1A", "#4C151515");
        SetGradientBrushPalette("GlassButtonPressedBrush", "#55121212", "#440F0F0F", "#380C0C0C");
        SetGradientBrushPalette("GlassButtonBorderBrush", "#36FFFFFF", "#12FFFFFF", "#03FFFFFF");
        SetGradientBrushPalette("GlassButtonBorderHoverBrush", "#4AFFFFFF", "#1CFFFFFF", "#05FFFFFF");
        SetGradientBrushPalette("GlassButtonBorderPressedBrush", "#2CFFFFFF", "#10FFFFFF", "#03FFFFFF");
        SetGradientBrushPalette("GlassButtonShineBrush", "#20FFFFFF", "#08FFFFFF", "#00FFFFFF");
        SetGradientBrushPalette("GlassButtonLensBrush", "#10FFFFFF", "#06FFFFFF", "#03000000", "#0E000000", "#01FFFFFF");
        SetGradientBrushPalette("GlassButtonRefractionBrush", "#0EFFFFFF", "#03FFFFFF", "#0C000000", "#00000000");
        SetGradientBrushPalette("GlassButtonInnerShadeBrush", "#0A000000", "#18000000", "#2A000000");
        SetGradientBrushPalette("GlassButtonTopEdgeBrush", "#26FFFFFF", "#0AFFFFFF", "#00FFFFFF");
        SetGradientBrushPalette("GlassButtonReflectionBrush", "#10FFFFFF", "#04FFFFFF", "#00FFFFFF");
        SetGradientBrushPalette("GlassButtonBottomRimBrush", "#0EFFFFFF", "#03FFFFFF", "#00FFFFFF");

        SetDropShadowPalette("GlassButtonShadow", 40, 0, 0.22, "#76000000");
        SetDropShadowPalette("GlassButtonShadowHover", 52, 0, 0.26, "#82000000");
        SetDropShadowPalette("GlassButtonShadowPressed", 28, 0, 0.16, "#62000000");
        SetSolidBrushPalette("GlassScrollBarTrackBrush", "#1E141414");
        SetSolidBrushPalette("GlassScrollBarTrackBorderBrush", "#20FFFFFF");
        SetSolidBrushPalette("GlassScrollBarThumbBrush", "#E8FFFFFF");
        SetSolidBrushPalette("GlassScrollBarThumbHoverBrush", "#FFFFFFFF");
        SetSolidBrushPalette("GlassScrollBarThumbPressedBrush", "#F2FFFFFF");
        SetSolidBrushPalette("GlassScrollBarThumbBorderBrush", "#7FFFFFFF");
        SetSolidBrushPalette("SidePanelBackgroundBrush", "#30101010");
        SetSolidBrushPalette("SidePanelBorderBrush", "#24FFFFFF");

        ApplyChromeSurfacePalette(
            rootBorderHex: "#D0000000",
            titleBarBackgroundHex: "#07070E14",
            titleBarBorderHex: "#FF000000",
            logoBorderHex: "#30FFFFFF",
            leftPanelBackgroundHex: "#32101010",
            leftPanelBorderHex: "#24FFFFFF",
            popupBackgroundHex: "#D2181818",
            popupBorderHex: "#2AFFFFFF",
            listBackgroundHex: "#30181818",
            listBorderHex: "#18FFFFFF",
            bottomBarBackgroundHex: "#32101010",
            bottomBarBorderHex: "#24FFFFFF",
            nicknameForegroundHex: "#EEF2F4");
    }

    private void ApplyLightGlassTheme()
    {
        SetGradientBrushPalette("GlassButtonBackgroundBrush", "#2A060606", "#1E030303", "#12000000");
        SetGradientBrushPalette("GlassButtonHoverBrush", "#34101010", "#260C0C0C", "#18060606");
        SetGradientBrushPalette("GlassButtonPressedBrush", "#30030303", "#24000000", "#1A000000");
        SetGradientBrushPalette("GlassButtonBorderBrush", "#3AFFFFFF", "#12FFFFFF", "#02FFFFFF");
        SetGradientBrushPalette("GlassButtonBorderHoverBrush", "#4AFFFFFF", "#18FFFFFF", "#04FFFFFF");
        SetGradientBrushPalette("GlassButtonBorderPressedBrush", "#30FFFFFF", "#10FFFFFF", "#02FFFFFF");
        SetGradientBrushPalette("GlassButtonShineBrush", "#20FFFFFF", "#08FFFFFF", "#00FFFFFF");
        SetGradientBrushPalette("GlassButtonLensBrush", "#10FFFFFF", "#06FFFFFF", "#05000000", "#14000000", "#01FFFFFF");
        SetGradientBrushPalette("GlassButtonRefractionBrush", "#0EFFFFFF", "#04FFFFFF", "#12000000", "#00000000");
        SetGradientBrushPalette("GlassButtonInnerShadeBrush", "#10000000", "#20000000", "#32000000");
        SetGradientBrushPalette("GlassButtonTopEdgeBrush", "#24FFFFFF", "#0AFFFFFF", "#00FFFFFF");
        SetGradientBrushPalette("GlassButtonReflectionBrush", "#10FFFFFF", "#04FFFFFF", "#00FFFFFF");
        SetGradientBrushPalette("GlassButtonBottomRimBrush", "#14FFFFFF", "#05FFFFFF", "#00FFFFFF");

        SetDropShadowPalette("GlassButtonShadow", 58, 0, 0.10, "#62000000");
        SetDropShadowPalette("GlassButtonShadowHover", 70, 0, 0.14, "#70000000");
        SetDropShadowPalette("GlassButtonShadowPressed", 44, 0, 0.08, "#4A000000");
        SetSolidBrushPalette("GlassScrollBarTrackBrush", "#0E000000");
        SetSolidBrushPalette("GlassScrollBarTrackBorderBrush", "#2AFFFFFF");
        SetSolidBrushPalette("GlassScrollBarThumbBrush", "#E8FFFFFF");
        SetSolidBrushPalette("GlassScrollBarThumbHoverBrush", "#FFFFFFFF");
        SetSolidBrushPalette("GlassScrollBarThumbPressedBrush", "#F2FFFFFF");
        SetSolidBrushPalette("GlassScrollBarThumbBorderBrush", "#7FFFFFFF");
        SetSolidBrushPalette("SidePanelBackgroundBrush", "#10000000");
        SetSolidBrushPalette("SidePanelBorderBrush", "#2AFFFFFF");

        ApplyChromeSurfacePalette(
            rootBorderHex: "#D0000000",
            titleBarBackgroundHex: "#07070E14",
            titleBarBorderHex: "#FF000000",
            logoBorderHex: "#36FFFFFF",
            leftPanelBackgroundHex: "#10000000",
            leftPanelBorderHex: "#2AFFFFFF",
            popupBackgroundHex: "#E4161915",
            popupBorderHex: "#36FFFFFF",
            listBackgroundHex: "#10000000",
            listBorderHex: "#20FFFFFF",
            bottomBarBackgroundHex: "#10000000",
            bottomBarBorderHex: "#2AFFFFFF",
            nicknameForegroundHex: "#EEF2F4");
    }

    private void ApplyChromeSurfacePalette(
        string rootBorderHex,
        string titleBarBackgroundHex,
        string titleBarBorderHex,
        string logoBorderHex,
        string leftPanelBackgroundHex,
        string leftPanelBorderHex,
        string popupBackgroundHex,
        string popupBorderHex,
        string listBackgroundHex,
        string listBorderHex,
        string bottomBarBackgroundHex,
        string bottomBarBorderHex,
        string nicknameForegroundHex)
    {
        RootChromeBorder.BorderBrush = CreateBrush(rootBorderHex);
        TitleBarBorder.Background = CreateBrush(titleBarBackgroundHex);
        TitleBarBorder.BorderBrush = CreateBrush(titleBarBorderHex);
        TopLeftLogoBorder.BorderBrush = CreateBrush(logoBorderHex);
        LeftControlSurfaceBorder.Background = CreateBrush(leftPanelBackgroundHex);
        LeftControlSurfaceBorder.BorderBrush = CreateBrush(leftPanelBorderHex);
        VersionPickerPopupBorder.Background = CreateBrush(popupBackgroundHex);
        VersionPickerPopupBorder.BorderBrush = CreateBrush(popupBorderHex);
        FriendNotificationsPopupBorder.Background = CreateBrush(popupBackgroundHex);
        FriendNotificationsPopupBorder.BorderBrush = CreateBrush(popupBorderHex);
        QuickVersionListBorder.Background = CreateBrush(listBackgroundHex);
        QuickVersionListBorder.BorderBrush = CreateBrush(listBorderHex);
        FriendNotificationsPopupItemsHostBorder.Background = CreateBrush(listBackgroundHex);
        FriendNotificationsPopupItemsHostBorder.BorderBrush = CreateBrush(listBorderHex);
        BottomActionBarBorder.Background = CreateBrush(bottomBarBackgroundHex);
        BottomActionBarBorder.BorderBrush = CreateBrush(bottomBarBorderHex);
        CurrentNicknameDisplay.Foreground = CreateBrush(nicknameForegroundHex);
        ApplySidePanelStyle(_activeSidePanelSection);
    }

    private void RefreshFriendNotificationsPopup()
    {
        if (FriendNotificationsPopupAccountTextBlock is null ||
            FriendNotificationsPopupStatusTextBlock is null ||
            FriendNotificationsPopupEmptyTextBlock is null ||
            FriendNotificationsPopupItemsHostBorder is null ||
            FriendNotificationsPopupItemsControl is null ||
            FriendNotificationsPopupActionButton is null)
        {
            return;
        }

        var hasStoredAccount = HasRegisteredAccount();
        var hasAuthenticatedAccount = HasAuthenticatedCloudSession();
        var hasGuestIdentity = HasIncognitoIdentity();
        var accountName = hasAuthenticatedAccount
            ? _accountState!.Username
            : hasGuestIdentity
                ? _guestIdentityState!.Username
                : hasStoredAccount
                    ? _accountState!.Username
                    : "Нет активного аккаунта";

        FriendNotificationsPopupAccountTextBlock.Text = accountName;
        FriendNotificationsPopupItemsControl.ItemsSource = null;
        FriendNotificationsPopupItemsControl.ItemsSource = _incomingFriendRequests.ToList();

        if (hasGuestIdentity && !hasAuthenticatedAccount)
        {
            FriendNotificationsPopupStatusTextBlock.Text = "Инкогнито режим";
            FriendNotificationsPopupEmptyTextBlock.Text = "В режиме инкогнито личные уведомления и друзья Vesper недоступны. Открой аккаунт, если нужен облачный профиль.";
            FriendNotificationsPopupItemsHostBorder.Visibility = Visibility.Collapsed;
            FriendNotificationsPopupEmptyTextBlock.Visibility = Visibility.Visible;
            FriendNotificationsPopupActionButton.Content = "Открыть аккаунт";
            return;
        }

        if (!hasStoredAccount)
        {
            FriendNotificationsPopupStatusTextBlock.Text = "Войдите в аккаунт, чтобы видеть личные уведомления.";
            FriendNotificationsPopupEmptyTextBlock.Text = "У этой учётной записи пока нет активной сессии. Открой аккаунт, чтобы войти или зарегистрироваться.";
            FriendNotificationsPopupItemsHostBorder.Visibility = Visibility.Collapsed;
            FriendNotificationsPopupEmptyTextBlock.Visibility = Visibility.Visible;
            FriendNotificationsPopupActionButton.Content = "Открыть аккаунт";
            return;
        }

        if (!hasAuthenticatedAccount)
        {
            FriendNotificationsPopupStatusTextBlock.Text = "Сессия истекла";
            FriendNotificationsPopupEmptyTextBlock.Text = $"Для аккаунта {accountName} нужно войти снова, чтобы загрузить уведомления.";
            FriendNotificationsPopupItemsHostBorder.Visibility = Visibility.Collapsed;
            FriendNotificationsPopupEmptyTextBlock.Visibility = Visibility.Visible;
            FriendNotificationsPopupActionButton.Content = "Открыть аккаунт";
            return;
        }

        FriendNotificationsPopupStatusTextBlock.Text = _incomingFriendRequests.Count > 0
            ? $"Входящих заявок: {_incomingFriendRequests.Count}"
            : "Новых уведомлений нет";
        FriendNotificationsPopupEmptyTextBlock.Text = $"Для аккаунта {accountName} пока нет новых уведомлений.";
        FriendNotificationsPopupItemsHostBorder.Visibility = _incomingFriendRequests.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        FriendNotificationsPopupEmptyTextBlock.Visibility = _incomingFriendRequests.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        FriendNotificationsPopupActionButton.Content = "Открыть друзей";
    }

    private void SetGradientBrushPalette(string resourceKey, params string[] colors)
    {
        if (FindThemeResource<GradientBrush>(resourceKey) is not { } brush || brush.IsFrozen)
        {
            return;
        }

        var count = Math.Min(brush.GradientStops.Count, colors.Length);
        for (var index = 0; index < count; index++)
        {
            brush.GradientStops[index].Color = ParseColor(colors[index]);
        }
    }

    private void SetDropShadowPalette(
        string resourceKey,
        double blurRadius,
        double shadowDepth,
        double opacity,
        string colorHex)
    {
        if (FindThemeResource<DropShadowEffect>(resourceKey) is not { } effect || effect.IsFrozen)
        {
            return;
        }

        effect.BlurRadius = blurRadius;
        effect.ShadowDepth = shadowDepth;
        effect.Opacity = opacity;
        effect.Color = ParseColor(colorHex);
    }

    private void SetSolidBrushPalette(string resourceKey, string colorHex)
    {
        if (FindThemeResource<SolidColorBrush>(resourceKey) is not { } brush || brush.IsFrozen)
        {
            return;
        }

        brush.Color = ParseColor(colorHex);
    }

    private T? FindThemeResource<T>(string resourceKey) where T : class
    {
        if (Resources.Contains(resourceKey) && Resources[resourceKey] is T localResource)
        {
            return localResource;
        }

        foreach (var dictionary in Resources.MergedDictionaries)
        {
            if (dictionary.Contains(resourceKey) && dictionary[resourceKey] is T mergedResource)
            {
                return mergedResource;
            }
        }

        return TryFindResource(resourceKey) as T;
    }

    private static SolidColorBrush CreateBrush(string colorHex)
    {
        var brush = new SolidColorBrush(ParseColor(colorHex));
        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string colorHex)
    {
        return (Color)ColorConverter.ConvertFromString(colorHex)!;
    }

    private static List<BackgroundSlotCandidate> BuildTimedBackgroundCandidates(IEnumerable<FileInfo> files)
    {
        var result = new List<BackgroundSlotCandidate>();
        foreach (var file in files)
        {
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file.Name);
            if (TryParseBackgroundTimeRange(fileNameWithoutExtension, out var startMinute, out var endMinute))
            {
                result.Add(new BackgroundSlotCandidate(file, startMinute, endMinute, Priority: 2));
                continue;
            }

            if (TryResolveNamedBackgroundTimeRange(fileNameWithoutExtension, out startMinute, out endMinute))
            {
                result.Add(new BackgroundSlotCandidate(file, startMinute, endMinute, Priority: 1));
            }
        }

        return result;
    }

    private static bool TryParseBackgroundTimeRange(
        string fileNameWithoutExtension,
        out int startMinuteOfDay,
        out int endMinuteOfDay)
    {
        startMinuteOfDay = 0;
        endMinuteOfDay = 0;

        var match = BackgroundTimeRangeRegex.Match(fileNameWithoutExtension);
        if (!match.Success)
        {
            return false;
        }

        if (!TryParseHourAndMinute(
                match.Groups["startHour"].Value,
                match.Groups["startMinute"].Value,
                out startMinuteOfDay))
        {
            return false;
        }

        if (!TryParseHourAndMinute(
                match.Groups["endHour"].Value,
                match.Groups["endMinute"].Value,
                out endMinuteOfDay))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseHourAndMinute(string hourText, string minuteText, out int minuteOfDay)
    {
        minuteOfDay = 0;

        if (!int.TryParse(hourText, out var hour) || hour is < 0 or > 23)
        {
            return false;
        }

        var minute = 0;
        if (!string.IsNullOrWhiteSpace(minuteText))
        {
            if (!int.TryParse(minuteText, out minute) || minute is < 0 or > 59)
            {
                return false;
            }
        }

        minuteOfDay = (hour * 60) + minute;
        return true;
    }

    private static bool TryResolveNamedBackgroundTimeRange(
        string fileNameWithoutExtension,
        out int startMinuteOfDay,
        out int endMinuteOfDay)
    {
        startMinuteOfDay = 0;
        endMinuteOfDay = 0;

        if (!TryResolveNamedBackgroundSceneKind(fileNameWithoutExtension, out var sceneKind))
        {
            return false;
        }

        GetSceneTimeRange(sceneKind, out startMinuteOfDay, out endMinuteOfDay);
        return true;
    }

    private static bool TryResolveNamedBackgroundSceneKind(
        string fileNameWithoutExtension,
        out BackgroundSceneKind sceneKind)
    {
        if (ContainsAnyToken(fileNameWithoutExtension, "morning", "утро", "утр", "sunrise", "dawn", "рассвет"))
        {
            sceneKind = BackgroundSceneKind.Morning;
            return true;
        }

        if (ContainsAnyToken(fileNameWithoutExtension, "day", "день", "дня", "днев", "noon", "daytime", "полд"))
        {
            sceneKind = BackgroundSceneKind.Day;
            return true;
        }

        if (ContainsAnyToken(fileNameWithoutExtension, "evening", "вечер", "веч", "sunset", "dusk", "закат", "сумер"))
        {
            sceneKind = BackgroundSceneKind.Evening;
            return true;
        }

        if (ContainsAnyToken(fileNameWithoutExtension, "night", "ночь", "ноч", "midnight", "ночн"))
        {
            sceneKind = BackgroundSceneKind.Night;
            return true;
        }

        sceneKind = BackgroundSceneKind.Unknown;
        return false;
    }

    private static void GetSceneTimeRange(
        BackgroundSceneKind sceneKind,
        out int startMinuteOfDay,
        out int endMinuteOfDay)
    {
        switch (sceneKind)
        {
            case BackgroundSceneKind.Morning:
                startMinuteOfDay = 6 * 60;
                endMinuteOfDay = 12 * 60;
                break;
            case BackgroundSceneKind.Day:
                startMinuteOfDay = 12 * 60;
                endMinuteOfDay = 18 * 60;
                break;
            case BackgroundSceneKind.Evening:
                startMinuteOfDay = 18 * 60;
                endMinuteOfDay = 22 * 60;
                break;
            default:
                startMinuteOfDay = 22 * 60;
                endMinuteOfDay = 6 * 60;
                break;
        }
    }

    private static BackgroundSceneKind InferBackgroundSceneKind(int startMinuteOfDay, int endMinuteOfDay)
    {
        var midpoint = (startMinuteOfDay + GetRangeDurationMinutes(startMinuteOfDay, endMinuteOfDay) / 2) % (24 * 60);
        if (midpoint is >= 6 * 60 and < 12 * 60)
        {
            return BackgroundSceneKind.Morning;
        }

        if (midpoint is >= 12 * 60 and < 18 * 60)
        {
            return BackgroundSceneKind.Day;
        }

        if (midpoint is >= 18 * 60 and < 22 * 60)
        {
            return BackgroundSceneKind.Evening;
        }

        return BackgroundSceneKind.Night;
    }

    private static bool ContainsAnyToken(string input, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (input.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMinuteInsideRange(int minuteOfDay, int startMinuteOfDay, int endMinuteOfDay)
    {
        if (startMinuteOfDay == endMinuteOfDay)
        {
            return true;
        }

        if (startMinuteOfDay < endMinuteOfDay)
        {
            return minuteOfDay >= startMinuteOfDay && minuteOfDay < endMinuteOfDay;
        }

        return minuteOfDay >= startMinuteOfDay || minuteOfDay < endMinuteOfDay;
    }

    private static int GetRangeDurationMinutes(int startMinuteOfDay, int endMinuteOfDay)
    {
        if (endMinuteOfDay >= startMinuteOfDay)
        {
            return endMinuteOfDay - startMinuteOfDay;
        }

        return (24 * 60 - startMinuteOfDay) + endMinuteOfDay;
    }
}

