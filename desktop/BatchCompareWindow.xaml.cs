using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class BatchCompareWindow : Window
{
    public int? SelectedIndex { get; private set; }

    public BatchCompareWindow(IReadOnlyList<(string Path, long Seed)> items, int currentIndex)
    {
        InitializeComponent();
        GridTitleText.Text = $"Сравнение батч-вариаций ({items.Count} шт.)";
        CellsGrid.Rows = items.Count <= 2 ? 1 : 2;
        CellsGrid.Columns = 2;

        for (int i = 0; i < items.Count; i++)
        {
            int idx = i;
            var (path, seed) = items[i];
            bool isSelected = i == currentIndex;

            var card = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("Surface1Brush"),
                BorderBrush = isSelected
                    ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                    : (System.Windows.Media.Brush)FindResource("BorderDefaultBrush"),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Margin = new Thickness(6)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var imgHost = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("WellBrush"),
                CornerRadius = new CornerRadius(7),
                ClipToBounds = true,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                imgHost.Child = new System.Windows.Controls.Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform
                };
            }
            imgHost.MouseLeftButtonUp += (_, _) =>
            {
                SelectedIndex = idx;
                DialogResult = true;
            };
            Grid.SetRow(imgHost, 0);
            grid.Children.Add(imgHost);

            var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            DockPanel.SetDock(btns, Dock.Right);

            bool isFav = GalleryFavoritesStore.IsFavorite(path);
            var favBtn = new System.Windows.Controls.Button
            {
                Content = isFav ? "★ В избранном" : "☆",
                Padding = new Thickness(9, 4, 9, 4),
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource("DarkButton")
            };
            favBtn.Click += (_, _) =>
            {
                bool now = GalleryFavoritesStore.Toggle(path);
                favBtn.Content = now ? "★ В избранном" : "☆";
            };
            btns.Children.Add(favBtn);

            var openBtn = new System.Windows.Controls.Button
            {
                Content = "Открыть",
                Padding = new Thickness(9, 4, 9, 4),
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource("DarkButton")
            };
            openBtn.Click += (_, _) => AiServiceManager.OpenExternal(path);
            btns.Children.Add(openBtn);

            var pickBtn = new System.Windows.Controls.Button
            {
                Content = isSelected ? "Активный" : "Выбрать",
                Padding = new Thickness(11, 4, 11, 4),
                FontSize = 12,
                Style = (Style)FindResource(isSelected ? "SuccessButton" : "PrimaryButton")
            };
            pickBtn.Click += (_, _) =>
            {
                SelectedIndex = idx;
                DialogResult = true;
            };
            btns.Children.Add(pickBtn);

            footer.Children.Add(btns);
            footer.Children.Add(new TextBlock
            {
                Text = $"#{idx + 1} • Seed: {seed}",
                FontSize = 12,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });

            Grid.SetRow(footer, 1);
            grid.Children.Add(footer);
            card.Child = grid;
            CellsGrid.Children.Add(card);
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
