using System.Windows;

namespace DiskSweep;

public partial class DetailsWindow : Window
{
    public DetailsWindow(CleanupCategory category)
    {
        InitializeComponent();
        Title = $"{category.Name} - 清理明细";
        TitleText.Text = category.Name;
        SummaryText.Text = $"{category.RiskLabel} · {category.SizeText} · {category.ItemCountText} · {category.Recommendation}";
        DetailsGrid.ItemsSource = category.Entries;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
