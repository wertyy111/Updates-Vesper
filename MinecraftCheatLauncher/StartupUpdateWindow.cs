using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;

namespace VesperLauncher;

public sealed class StartupUpdateWindow : Window
{
    private const string WindowTitle = "Vesper Launcher";
    private const string DefaultMessage = "Проверяем обновления...";
    private const string DefaultDetail = "Подключаемся к серверу обновлений...";

    private readonly TextBlock _messageText;
    private readonly TextBlock _detailText;
    private readonly ProgressBar _progressBar;

    public StartupUpdateWindow()
    {
        var appIcon = LoadIconFrame(16);

        Title = WindowTitle;
        Width = 380;
        Height = 150;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        Topmost = true;

        if (appIcon is not null)
        {
            Icon = appIcon;
        }

        var mainGrid = new Grid
        {
            Margin = new Thickness(16, 12, 16, 16)
        };
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Message
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Detail
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Progress Bar

        _messageText = new TextBlock
        {
            Text = DefaultMessage,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Black,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 4)
        };
        mainGrid.Children.Add(_messageText);
        Grid.SetRow(_messageText, 0);

        _detailText = new TextBlock
        {
            Text = DefaultDetail,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 12)
        };
        mainGrid.Children.Add(_detailText);
        Grid.SetRow(_detailText, 1);

        _progressBar = new ProgressBar
        {
            Height = 16,
            IsIndeterminate = true,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        mainGrid.Children.Add(_progressBar);
        Grid.SetRow(_progressBar, 2);

        Content = mainGrid;

        // Force layout rendering mode
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    public void SetStatus(string message)
    {
        UpdateState(new LauncherUpdateUiState
        {
            Message = message,
            DetailMessage = DefaultDetail,
            IsIndeterminate = true
        });
    }

    public void UpdateState(LauncherUpdateUiState state)
    {
        var message = NormalizeText(state.Message, DefaultMessage);
        var detail = NormalizeText(state.DetailMessage, DefaultDetail);

        if (state.IsIndeterminate)
        {
            _progressBar.IsIndeterminate = true;
        }
        else
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = state.ProgressPercent ?? 0;
        }

        _messageText.Text = FormatStatusLine(message, appendDots: true);
        _detailText.Text = FormatStatusLine(detail, appendDots: ShouldAppendDots(detail));
    }

    private static bool ShouldAppendDots(string text)
    {
        return !text.Contains('/', StringComparison.Ordinal) && !text.Contains('%', StringComparison.Ordinal);
    }

    private static string FormatStatusLine(string text, bool appendDots)
    {
        var normalized = NormalizeText(text, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (!appendDots || normalized.EndsWith("...", StringComparison.Ordinal))
        {
            return normalized;
        }

        normalized = normalized.TrimEnd('.');
        return $"{normalized}...";
    }

    private static string NormalizeText(string? value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!LooksLikeUtf8Mojibake(text))
        {
            return text;
        }

        try
        {
            return Encoding.UTF8.GetString(Encoding.GetEncoding(1251).GetBytes(text));
        }
        catch
        {
            return fallback;
        }
    }

    private static bool LooksLikeUtf8Mojibake(string text)
    {
        return text.Contains("Рџ", StringComparison.Ordinal)
            || text.Contains("РЎ", StringComparison.Ordinal)
            || text.Contains("Р‘", StringComparison.Ordinal)
            || text.Contains("Рљ", StringComparison.Ordinal)
            || text.Contains("Рњ", StringComparison.Ordinal)
            || text.Contains("Р“", StringComparison.Ordinal)
            || text.Contains("СЃ", StringComparison.Ordinal)
            || text.Contains("СЂ", StringComparison.Ordinal)
            || text.Contains("СЏ", StringComparison.Ordinal)
            || text.Contains("С‡", StringComparison.Ordinal)
            || text.Contains("СЋ", StringComparison.Ordinal)
            || text.Contains("Ð", StringComparison.Ordinal)
            || text.Contains("Ñ", StringComparison.Ordinal);
    }

    private static ImageSource? LoadIconFrame(int preferredSize)
    {
        foreach (var path in GetAssetCandidates("vesper-app.ico"))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(path);
                var decoder = new IconBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var frame = decoder.Frames
                    .OrderBy(candidate => Math.Abs(candidate.PixelWidth - preferredSize))
                    .ThenBy(candidate => candidate.PixelWidth)
                    .FirstOrDefault();
                frame?.Freeze();
                if (frame is not null)
                {
                    return frame;
                }
            }
            catch
            {
                // The splash must never block startup if the icon file is missing or corrupted.
            }
        }

        return null;
    }

    private static string[] GetAssetCandidates(string fileName)
    {
        return
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", fileName))
        ];
    }
}

