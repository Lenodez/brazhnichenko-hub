using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalAIHub.Views;

public partial class CatalogTabView : System.Windows.Controls.UserControl
{
    public CatalogTabView()
    {
        InitializeComponent();
    }

    private MainWindow? Host => IsLoaded ? Window.GetWindow(this) as MainWindow : null;

    private void CatalogSim_Changed(object sender, SelectionChangedEventArgs e) => Host?.CatalogSim_Changed(sender, e);
    private void CatalogSortCombo_Changed(object sender, SelectionChangedEventArgs e) => Host?.CatalogSortCombo_Changed(sender, e);
    private void CatalogTextFilterBtn_Click(object sender, RoutedEventArgs e) => Host?.CatalogTextFilterBtn_Click(sender, e);
    private void CatTabDiskBtn_Click(object sender, RoutedEventArgs e) => Host?.CatTabDiskBtn_Click(sender, e);
    private void CatTabHfSearchBtn_Click(object sender, RoutedEventArgs e) => Host?.CatTabHfSearchBtn_Click(sender, e);
    private void CatTabPhotoBtn_Click(object sender, RoutedEventArgs e) => Host?.CatTabPhotoBtn_Click(sender, e);
    private void CatTabTextBtn_Click(object sender, RoutedEventArgs e) => Host?.CatTabTextBtn_Click(sender, e);
    private void CatTabVideoBtn_Click(object sender, RoutedEventArgs e) => Host?.CatTabVideoBtn_Click(sender, e);
    private void CatTabWikiBtn_Click(object sender, RoutedEventArgs e) => Host?.CatTabWikiBtn_Click(sender, e);
    private void DeleteSelectedModelBtn_Click(object sender, RoutedEventArgs e) => Host?.DeleteSelectedModelBtn_Click(sender, e);
    private void HfDownloadSelectedBtn_Click(object sender, RoutedEventArgs e) => Host?.HfDownloadSelectedBtn_Click(sender, e);
    private void HfFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.HfFilesList_SelectionChanged(sender, e);
    private void HfOnlyFitCheck_Changed(object sender, RoutedEventArgs e) => Host?.HfOnlyFitCheck_Changed(sender, e);
    private void HfOpenRepoWebBtn_Click(object sender, RoutedEventArgs e) => Host?.HfOpenRepoWebBtn_Click(sender, e);
    private void HfQuickChip_Click(object sender, RoutedEventArgs e) => Host?.HfQuickChip_Click(sender, e);
    private void HfReposList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.HfReposList_SelectionChanged(sender, e);
    private void HfTopPopularBtn_Click(object sender, RoutedEventArgs e) => Host?.HfTopPopularBtn_Click(sender, e);
    private void HfSearchBtn_Click(object sender, RoutedEventArgs e) => Host?.HfSearchBtn_Click(sender, e);
    private void HfSearchModeBtn_Click(object sender, RoutedEventArgs e) => Host?.HfSearchModeBtn_Click(sender, e);
    private void HfSearchQueryBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Host?.HfSearchQueryBox_PreviewKeyDown(sender, e);
    private void OpenSelectedModelFolderBtn_Click(object sender, RoutedEventArgs e) => Host?.OpenSelectedModelFolderBtn_Click(sender, e);
    private void RefreshModelsBtn_Click(object sender, RoutedEventArgs e) => Host?.RefreshModelsBtn_Click(sender, e);
    private void WikiSeriesCombo_Changed(object sender, SelectionChangedEventArgs e) => Host?.WikiSeriesCombo_Changed(sender, e);
}
