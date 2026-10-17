using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NovaSonics.Models;

namespace NovaSonics.Converters
{
    public class ChannelToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is AppChannel channel)
            {
                return channel switch
                {
                    AppChannel.Chat => new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)), // Purple/Violet #8B5CF6
                    AppChannel.Game => new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)), // Cyan #00E5FF
                    AppChannel.Media => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), // Amber #F59E0B
                    AppChannel.Mic => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),  // Emerald #10B981
                    _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))                // Slate #94A3B8
                };
            }
            return new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class PeakToHeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is float peak)
            {
                // Max fader height is 200px
                double maxH = 200.0;
                if (parameter != null && double.TryParse(parameter.ToString(), out double customMax))
                {
                    maxH = customMax;
                }
                return Math.Clamp(peak * maxH, 0.0, maxH);
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BooleanToOnlineTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is true) ? "🟢 Online" : "⚪ Offline";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class FloatToPercentageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is float f)
            {
                return $"{(int)(f * 100)}%";
            }
            return "0%";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BooleanToVisibilityConverter : IValueConverter
    {
        public static BooleanToVisibilityConverter Instance { get; } = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is true) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool b) ? !b : false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class DbFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is float f) return $"{f:0.0} dB";
            if (value is double d) return $"{d:0.0} dB";
            return "0.0 dB";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class DecimalFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is float f) return $"{f:0.00}";
            if (value is double d) return $"{d:0.00}";
            return "0.00";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
