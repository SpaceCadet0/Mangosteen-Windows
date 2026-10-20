using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mangosteen.Tools;

/// <summary>A row of colour swatches plus a hex field, since WPF has no colour picker.</summary>
internal sealed class Swatches : StackPanel
{
    public static readonly uint[] Palette =
    [
        0xFFFFFFFF, 0xFFF5F2ED, 0xFFE8E8E8, 0xFF9A9AA0, 0xFF1C1C1E, 0xFF000000,
        0xFF671E38, 0xFFE5484D, 0xFFFA9E5C, 0xFFF5C542, 0xFF46A758, 0xFF3E8ED0, 0xFF8C45AD,
    ];

    private readonly TextBox _hex = new() { Width = 76, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private uint _value;

    public event Action<uint>? Changed;

    public Swatches(uint value)
    {
        Orientation = Orientation.Horizontal;
        foreach (var color in Palette)
        {
            var swatch = new Button
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 3, 0), Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
                Background = new SolidColorBrush(ToColor(color)), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                ToolTip = Hex(color),
            };
            swatch.Click += (_, _) => Value = color;
            Children.Add(swatch);
        }
        Children.Add(_hex);
        _hex.LostFocus += (_, _) => ParseHex();
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ParseHex();
            }
        };
        Value = value;
    }

    public uint Value
    {
        get => _value;
        set
        {
            _value = value;
            _hex.Text = Hex(value);
            Changed?.Invoke(value);
        }
    }

    private void ParseHex()
    {
        var text = _hex.Text.Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            Value = 0xFF000000 | rgb;
        }
        else
        {
            _hex.Text = Hex(_value);
        }
    }

    public static string Hex(uint argb) => $"#{argb & 0xFFFFFF:X6}";

    public static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
