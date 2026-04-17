using System.Globalization;
using System.Windows.Data;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.App.Converters;

public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            IntuneDetectionRuleType.None => "None (let app suggest)",
            IntuneDetectionRuleType.MsiProductCode => "MSI Product Code (best for MSI)",
            IntuneDetectionRuleType.File => "File / Folder",
            IntuneDetectionRuleType.Registry => "Registry (deterministic for EXE)",
            IntuneDetectionRuleType.Script => "Script (last resort)",

            IntuneDetectionOperator.Exists => "Exists",
            IntuneDetectionOperator.Equals => "Equals (exact value)",
            IntuneDetectionOperator.NotEquals => "Not equals",
            IntuneDetectionOperator.GreaterThan => "Greater than",
            IntuneDetectionOperator.GreaterThanOrEqual => "Greater or equal",
            IntuneDetectionOperator.LessThan => "Less than",
            IntuneDetectionOperator.LessThanOrEqual => "Less or equal",

            _ => value?.ToString() ?? string.Empty
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
