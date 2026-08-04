using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameLauncher.Desktop.Helpers;

/// <summary>
/// Converts a value to <see cref="Visibility"/>, treating "has content" as
/// visible.
/// </summary>
/// <remarks>
/// Handles null, empty strings, zero and empty collections uniformly, which
/// removes the need for a separate converter per type. Pass <c>Invert</c> as the
/// parameter to reverse the result.
/// </remarks>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            bool flag => flag,
            int number => number != 0,
            long number => number != 0,
            double number => number != 0,
            System.Collections.ICollection collection => collection.Count > 0,
            System.Collections.IEnumerable sequence => sequence.GetEnumerator().MoveNext(),
            _ => true
        };

        if (IsInverted(parameter))
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: this conversion is one-way.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(HasValueToVisibilityConverter)} only supports one-way binding.");

    internal static bool IsInverted(object? parameter) =>
        string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Converts a <see cref="bool"/> to <see cref="Visibility"/>. Pass
/// <c>Invert</c> as the parameter to reverse the result.
/// </summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (HasValueToVisibilityConverter.IsInverted(parameter))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ HasValueToVisibilityConverter.IsInverted(parameter);
}

/// <summary>
/// Negates a <see cref="bool"/>, for binding "enabled" to a "busy" flag.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>
/// Maps a <see cref="bool"/> to an opacity, used to dim inactive content.
/// </summary>
/// <remarks>
/// Locked achievements are dimmed rather than hidden, so the page still shows
/// what there is left to earn. Opacity is used instead of a separate greyed
/// brush so that icon artwork dims along with the text.
/// </remarks>
public sealed class BooleanToOpacityConverter : IValueConverter
{
    /// <summary>Gets or sets the opacity used when the value is <see langword="true"/>.</summary>
    public double TrueOpacity { get; set; } = 1.0;

    /// <summary>Gets or sets the opacity used when the value is <see langword="false"/>.</summary>
    public double FalseOpacity { get; set; } = 0.45;

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TrueOpacity : FalseOpacity;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: this conversion is one-way.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(BooleanToOpacityConverter)} only supports one-way binding.");
}

/// <summary>
/// Returns <see langword="true"/> when the bound value equals the enum member
/// named by the converter parameter.
/// </summary>
/// <remarks>
/// Drives mutually exclusive selection — sidebar entries, view-mode toggles —
/// from a single enum property rather than one boolean per option.
/// </remarks>
public sealed class EnumToBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string name)
        {
            return false;
        }

        return Enum.TryParse(value.GetType(), name, ignoreCase: true, out var expected)
               && value.Equals(expected);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only a transition to <see langword="true"/> selects a value. Returning
    /// <see cref="Binding.DoNothing"/> for the deselect half stops the
    /// outgoing radio button from racing the incoming one and clearing the
    /// property.
    /// </remarks>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string name)
        {
            return Binding.DoNothing;
        }

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return Enum.TryParse(enumType, name, ignoreCase: true, out var parsed)
            ? parsed
            : Binding.DoNothing;
    }
}

/// <summary>
/// Formats a duration given in seconds as compact playtime text, for example
/// <c>12 min</c> or <c>4.5 hours</c>.
/// </summary>
public sealed class PlaytimeConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var seconds = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0L
        };

        return Format(seconds, culture);
    }

    /// <summary>
    /// Formats a second count as human-readable playtime.
    /// </summary>
    /// <param name="seconds">Total seconds played. Negative values are treated as zero.</param>
    /// <param name="culture">Culture used for number formatting.</param>
    /// <returns>Playtime text such as <c>Never played</c>, <c>48 min</c> or <c>12.3 hours</c>.</returns>
    public static string Format(long seconds, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;

        if (seconds <= 0)
        {
            return "Never played";
        }

        if (seconds < 60)
        {
            return "Less than a minute";
        }

        if (seconds < 3600)
        {
            var minutes = seconds / 60;
            return string.Create(culture, $"{minutes} min");
        }

        var hours = seconds / 3600d;

        // Below ten hours a single decimal is meaningful; above it, it is noise.
        return hours < 10
            ? string.Create(culture, $"{hours:0.#} hours")
            : string.Create(culture, $"{Math.Round(hours):0} hours");
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: this conversion is one-way.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(PlaytimeConverter)} only supports one-way binding.");
}

/// <summary>
/// Formats a byte count using binary units, for example <c>1.4 GB</c>.
/// </summary>
public sealed class ByteSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bytes = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0L
        };

        return Format(bytes, culture);
    }

    /// <summary>
    /// Formats a byte count using binary (1024-based) units.
    /// </summary>
    /// <param name="bytes">Number of bytes. Negative values are treated as unknown.</param>
    /// <param name="culture">Culture used for number formatting.</param>
    /// <returns>A size string such as <c>Unknown</c>, <c>948 MB</c> or <c>1.4 GB</c>.</returns>
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;

        if (bytes < 0)
        {
            return "Unknown";
        }

        if (bytes == 0)
        {
            return "0 B";
        }

        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Bytes and kilobytes never warrant a decimal place.
        return unit <= 1
            ? string.Create(culture, $"{Math.Round(size):0} {Units[unit]}")
            : string.Create(culture, $"{size:0.#} {Units[unit]}");
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: this conversion is one-way.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(ByteSizeConverter)} only supports one-way binding.");
}

/// <summary>
/// Formats a <see cref="DateTimeOffset"/> as a relative description such as
/// <c>3 hours ago</c>.
/// </summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DateTimeOffset stamp => Format(stamp),
            DateTime stamp => Format(new DateTimeOffset(stamp)),
            _ => "Never"
        };

    /// <summary>
    /// Formats a timestamp relative to now.
    /// </summary>
    /// <param name="value">The timestamp to describe.</param>
    /// <param name="now">Reference point; defaults to the current time.</param>
    /// <returns>A phrase such as <c>Just now</c>, <c>5 min ago</c> or <c>12 Mar 2026</c>.</returns>
    public static string Format(DateTimeOffset value, DateTimeOffset? now = null)
    {
        var reference = now ?? DateTimeOffset.Now;
        var elapsed = reference - value;

        if (elapsed < TimeSpan.Zero)
        {
            // Clock skew, or a timestamp written by a machine running ahead.
            return "Just now";
        }

        return elapsed switch
        {
            { TotalSeconds: < 60 } => "Just now",
            { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes} min ago",
            { TotalHours: < 24 } => $"{(int)elapsed.TotalHours} hr ago",
            { TotalDays: < 7 } => $"{(int)elapsed.TotalDays} d ago",
            _ => value.LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
        };
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: this conversion is one-way.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(RelativeTimeConverter)} only supports one-way binding.");
}

/// <summary>
/// Loads an image from an absolute path into an <see cref="ImageSource"/>,
/// returning <see langword="null"/> when the file is missing or unreadable.
/// </summary>
/// <remarks>
/// Images are decoded fully on load and frozen, so the source file is not left
/// locked — artwork can be replaced while the app is running — and the resulting
/// bitmap is safe to share across threads.
/// </remarks>
public sealed class PathToImageConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UriFormatException or ArgumentException)
        {
            // A corrupt or unsupported image should fall back to placeholder
            // artwork, not tear down the item template it is bound inside.
            return null;
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: this conversion is one-way.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(PathToImageConverter)} only supports one-way binding.");
}
