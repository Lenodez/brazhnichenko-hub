using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalAIHub.Views;

public partial class PhotoTabView : System.Windows.Controls.UserControl
{
    public PhotoTabView()
    {
        InitializeComponent();
    }

    private MainWindow? Host => IsLoaded ? Window.GetWindow(this) as MainWindow : null;

    private void AddPhotoStageBtn_Click(object sender, RoutedEventArgs e) => Host?.AddPhotoStageBtn_Click(sender, e);
    private void AskAiPhotoSettingsBtn_Click(object sender, RoutedEventArgs e) => Host?.AskAiPhotoSettingsBtn_Click(sender, e);
    private void DirectorModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.DirectorModelCombo_SelectionChanged(sender, e);
    private void ImageDrop_DragOver(object sender, System.Windows.DragEventArgs e) => Host?.ImageDrop_DragOver(sender, e);
    private void LoraSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Host?.LoraSlider_ValueChanged(sender, e);
    private void OpenLorasFolderBtn_Click(object sender, RoutedEventArgs e) => Host?.OpenLorasFolderBtn_Click(sender, e);
    private void OpenStoriesFolderBtn_Click(object sender, RoutedEventArgs e) => Host?.OpenStoriesFolderBtn_Click(sender, e);
    private void PhotoBatchGridBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoBatchGridBtn_Click(sender, e);
    private void PhotoCompareBeforeAfterBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoCompareBeforeAfterBtn_Click(sender, e);
    private void PhotoCopyPromptBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoCopyPromptBtn_Click(sender, e);
    private void PhotoDirectorBuildBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoDirectorBuildBtn_Click(sender, e);
    private void PhotoDirectorClearChatBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoDirectorClearChatBtn_Click(sender, e);
    private void PhotoDirectorDiscussBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoDirectorDiscussBtn_Click(sender, e);
    private void PhotoDirectorHideChatBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoDirectorHideChatBtn_Click(sender, e);
    private void PhotoDirectorReplyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Host?.PhotoDirectorReplyBox_PreviewKeyDown(sender, e);
    private void PhotoDirectorReviseBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoDirectorReviseBtn_Click(sender, e);
    private void PhotoDirectorSendReplyBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoDirectorSendReplyBtn_Click(sender, e);
    private void PhotoEnhanceDirectorBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoEnhanceDirectorBtn_Click(sender, e);
    private void PhotoEnqueueBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoEnqueueBtn_Click(sender, e);
    private void PhotoGenerateBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoGenerateBtn_Click(sender, e);
    private void PhotoHistoryBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoHistoryBtn_Click(sender, e);
    private void PhotoMicBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoMicBtn_Click(sender, e);
    private void PhotoMissingActionBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoMissingActionBtn_Click(sender, e);
    private void PhotoModeSingleBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoModeSingleBtn_Click(sender, e);
    private void PhotoModeStoryBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoModeStoryBtn_Click(sender, e);
    private void PhotoModeUpscaleBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoModeUpscaleBtn_Click(sender, e);
    private void PhotoOpenResultBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoOpenResultBtn_Click(sender, e);
    private void PhotoPresetApplyBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoPresetApplyBtn_Click(sender, e);
    private void PhotoPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.PhotoPresetCombo_SelectionChanged(sender, e);
    private void PhotoPresetDeleteBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoPresetDeleteBtn_Click(sender, e);
    private void PhotoPresetSaveBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoPresetSaveBtn_Click(sender, e);
    private void PhotoPreviewBorder_Click(object sender, MouseButtonEventArgs e) => Host?.PhotoPreviewBorder_Click(sender, e);
    private void PhotoRefDrop_Drop(object sender, System.Windows.DragEventArgs e) => Host?.PhotoRefDrop_Drop(sender, e);
    private void PhotoUseLastSeedBtn_Click(object sender, RoutedEventArgs e) => Host?.PhotoUseLastSeedBtn_Click(sender, e);
    private void RefreshLorasBtn_Click(object sender, RoutedEventArgs e) => Host?.RefreshLorasBtn_Click(sender, e);
    private void ResetPhotoStoryBtn_Click(object sender, RoutedEventArgs e) => Host?.ResetPhotoStoryBtn_Click(sender, e);
    private void StudioClearPhotoMaskBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioClearPhotoMaskBtn_Click(sender, e);
    private void StudioClearPhotoRefBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioClearPhotoRefBtn_Click(sender, e);
    private void StudioEngineCombo_Changed(object sender, SelectionChangedEventArgs e) => Host?.StudioEngineCombo_Changed(sender, e);
    private void StudioOpenOutputFolderBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioOpenOutputFolderBtn_Click(sender, e);
    private void StudioPastePhotoRefBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioPastePhotoRefBtn_Click(sender, e);
    private void StudioPasteUpscaleBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioPasteUpscaleBtn_Click(sender, e);
    private void StudioPhotoMaskBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioPhotoMaskBtn_Click(sender, e);
    private void StudioPhotoRefThumb_Click(object sender, MouseButtonEventArgs e) => Host?.StudioPhotoRefThumb_Click(sender, e);
    private void StudioPickPhotoRefBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioPickPhotoRefBtn_Click(sender, e);
    private void StudioPickUpscaleBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioPickUpscaleBtn_Click(sender, e);
    private void StudioSendToStoryBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioSendToStoryBtn_Click(sender, e);
    private void StudioSendToUpscaleBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioSendToUpscaleBtn_Click(sender, e);
    private void StudioSendToVideoBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioSendToVideoBtn_Click(sender, e);
    private void UpscaleDrop_Drop(object sender, System.Windows.DragEventArgs e) => Host?.UpscaleDrop_Drop(sender, e);
}
