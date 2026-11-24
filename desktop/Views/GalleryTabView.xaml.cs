using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalAIHub.Views;

public partial class GalleryTabView : System.Windows.Controls.UserControl
{
    public GalleryTabView()
    {
        InitializeComponent();
    }

    private MainWindow? Host => IsLoaded ? Window.GetWindow(this) as MainWindow : null;

    private void GalleryCleanTempBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryCleanTempBtn_Click(sender, e);
    private void GalleryClearSelBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryClearSelBtn_Click(sender, e);
    private void GalleryCopyImageBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryCopyImageBtn_Click(sender, e);
    private void GalleryDeleteCurrentBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryDeleteCurrentBtn_Click(sender, e);
    private void GalleryDeleteSelectedBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryDeleteSelectedBtn_Click(sender, e);
    private void GalleryFilterBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryFilterBtn_Click(sender, e);
    private void GalleryGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Host?.GalleryGrid_PreviewKeyDown(sender, e);
    private void GalleryLoadMoreBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryLoadMoreBtn_Click(sender, e);
    private void GalleryNextBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryNextBtn_Click(sender, e);
    private void GalleryOpenExternalBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryOpenExternalBtn_Click(sender, e);
    private void GalleryPlayPauseBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryPlayPauseBtn_Click(sender, e);
    private void GalleryPrevBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryPrevBtn_Click(sender, e);
    private void GalleryPreviewImage_Click(object sender, MouseButtonEventArgs e) => Host?.GalleryPreviewImage_Click(sender, e);
    private void GalleryRefreshBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryRefreshBtn_Click(sender, e);
    private void GalleryRestoreStudioBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryRestoreStudioBtn_Click(sender, e);
    private void GalleryRevealBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryRevealBtn_Click(sender, e);
    private void GallerySearchBox_TextChanged(object sender, TextChangedEventArgs e) => Host?.GallerySearchBox_TextChanged(sender, e);
    private void GallerySelectAllBtn_Click(object sender, RoutedEventArgs e) => Host?.GallerySelectAllBtn_Click(sender, e);
    private void GallerySelectTrashBtn_Click(object sender, RoutedEventArgs e) => Host?.GallerySelectTrashBtn_Click(sender, e);
    private void GallerySendToPhotoRefBtn_Click(object sender, RoutedEventArgs e) => Host?.GallerySendToPhotoRefBtn_Click(sender, e);
    private void GallerySendToUpscaleBtn_Click(object sender, RoutedEventArgs e) => Host?.GallerySendToUpscaleBtn_Click(sender, e);
    private void GallerySendToVideoBtn_Click(object sender, RoutedEventArgs e) => Host?.GallerySendToVideoBtn_Click(sender, e);
    private void GallerySortCombo_Changed(object sender, SelectionChangedEventArgs e) => Host?.GallerySortCombo_Changed(sender, e);
    private void GalleryToggleFavBtn_Click(object sender, RoutedEventArgs e) => Host?.GalleryToggleFavBtn_Click(sender, e);
    private void GalleryVideoPlayer_MediaEnded(object sender, RoutedEventArgs e) => Host?.GalleryVideoPlayer_MediaEnded(sender, e);
    private void StudioOpenOutputFolderBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioOpenOutputFolderBtn_Click(sender, e);
}
