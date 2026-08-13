using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace PingBoard.App.Controls;

/// <summary>
/// Resolves a theme-resource key to the brush it names.
/// <para>
/// The row view model deliberately exposes a resource <em>key</em> rather than a
/// <see cref="Brush"/>. Handing out a concrete brush would bake in whichever theme was active when
/// the row was built, so switching Windows between light and dark while the app runs would leave
/// every status the wrong colour until restart. Resolving through the key at bind time means the
/// theme dictionaries stay in charge.
/// </para>
/// </summary>
public sealed partial class BrushKeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string key
            && Application.Current.Resources.TryGetValue(key, out var resource)
            && resource is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
