using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExcelSearch.Views;

/// <summary>A count tile for the preview. The four properties are DependencyProperties so XAML can bind or set them.</summary>
public partial class SummaryCard : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(int), typeof(SummaryCard), new PropertyMetadata(0));
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SummaryCard), new PropertyMetadata(""));
    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(SummaryCard), new PropertyMetadata(""));
    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(SummaryCard), new PropertyMetadata(Brushes.Gray));

    public SummaryCard() => InitializeComponent();

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
}
