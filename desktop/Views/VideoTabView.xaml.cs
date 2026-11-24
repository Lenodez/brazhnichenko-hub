using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalAIHub.Views;

public partial class VideoTabView : System.Windows.Controls.UserControl
{
    public VideoTabView()
    {
        InitializeComponent();
    }

    private MainWindow? Host => IsLoaded ? Window.GetWindow(this) as MainWindow : null;

    private void AddVideoStageBtn_Click(object sender, RoutedEventArgs e) => Host?.AddVideoStageBtn_Click(sender, e);
    private void AskAiVideoSettingsBtn_Click(object sender, RoutedEventArgs e) => Host?.AskAiVideoSettingsBtn_Click(sender, e);
    private void DirectorModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.DirectorModelCombo_SelectionChanged(sender, e);
    private void ImageDrop_DragOver(object sender, System.Windows.DragEventArgs e) => Host?.ImageDrop_DragOver(sender, e);
    private void LoraSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Host?.LoraSlider_ValueChanged(sender, e);
    private void MergeVideoStoryBtn_Click(object sender, RoutedEventArgs e) => Host?.MergeVideoStoryBtn_Click(sender, e);
    private void OpenLorasFolderBtn_Click(object sender, RoutedEventArgs e) => Host?.OpenLorasFolderBtn_Click(sender, e);
    private void RefreshLorasBtn_Click(object sender, RoutedEventArgs e) => Host?.RefreshLorasBtn_Click(sender, e);
    private void ResetVideoStoryBtn_Click(object sender, RoutedEventArgs e) => Host?.ResetVideoStoryBtn_Click(sender, e);
    private void RunVideoPipelineBtn_Click(object sender, RoutedEventArgs e) => Host?.RunVideoPipelineBtn_Click(sender, e);
    private void SingleVideoDirectorApplyBtn_Click(object sender, RoutedEventArgs e) => Host?.SingleVideoDirectorApplyBtn_Click(sender, e);
    private void SingleVideoDirectorClearChatBtn_Click(object sender, RoutedEventArgs e) => Host?.SingleVideoDirectorClearChatBtn_Click(sender, e);
    private void SingleVideoDirectorHideChatBtn_Click(object sender, RoutedEventArgs e) => Host?.SingleVideoDirectorHideChatBtn_Click(sender, e);
    private void SingleVideoDirectorReplyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Host?.SingleVideoDirectorReplyBox_PreviewKeyDown(sender, e);
    private void SingleVideoDirectorSendBtn_Click(object sender, RoutedEventArgs e) => Host?.SingleVideoDirectorSendBtn_Click(sender, e);
    private void StudioClearVideoEndBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioClearVideoEndBtn_Click(sender, e);
    private void StudioClearVideoStartBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioClearVideoStartBtn_Click(sender, e);
    private void StudioEngineCombo_Changed(object sender, SelectionChangedEventArgs e) => Host?.StudioEngineCombo_Changed(sender, e);
    private void StudioOpenOutputFolderBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioOpenOutputFolderBtn_Click(sender, e);
    private void StudioPasteVideoEndBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioPasteVideoEndBtn_Click(sender, e);
    private void StudioPasteVideoStartBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioPasteVideoStartBtn_Click(sender, e);
    private void StudioPickVideoEndBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioPickVideoEndBtn_Click(sender, e);
    private void StudioPickVideoStartBtn_Click(object sender, RoutedEventArgs e) => Host?.StudioPickVideoStartBtn_Click(sender, e);
    private void StudioVideoEndThumb_Click(object sender, MouseButtonEventArgs e) => Host?.StudioVideoEndThumb_Click(sender, e);
    private void StudioVideoStartThumb_Click(object sender, MouseButtonEventArgs e) => Host?.StudioVideoStartThumb_Click(sender, e);
    private void VideoCopyPromptBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoCopyPromptBtn_Click(sender, e);
    private void VideoDirectorBuildBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoDirectorBuildBtn_Click(sender, e);
    private void VideoDirectorClearChatBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoDirectorClearChatBtn_Click(sender, e);
    private void VideoDirectorDiscussBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoDirectorDiscussBtn_Click(sender, e);
    private void VideoDirectorHideChatBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoDirectorHideChatBtn_Click(sender, e);
    private void VideoDirectorReplyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Host?.VideoDirectorReplyBox_PreviewKeyDown(sender, e);
    private void VideoDirectorReviseBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoDirectorReviseBtn_Click(sender, e);
    private void VideoDirectorSendReplyBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoDirectorSendReplyBtn_Click(sender, e);
    private void VideoEnhanceDirectorBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoEnhanceDirectorBtn_Click(sender, e);
    private void VideoEnqueueBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoEnqueueBtn_Click(sender, e);
    private void VideoGenerateBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoGenerateBtn_Click(sender, e);
    private void VideoHistoryBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoHistoryBtn_Click(sender, e);
    private void VideoMicBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoMicBtn_Click(sender, e);
    private void VideoMissingActionBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoMissingActionBtn_Click(sender, e);
    private void VideoModeSingleBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoModeSingleBtn_Click(sender, e);
    private void VideoModeStoryBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoModeStoryBtn_Click(sender, e);
    private void VideoOpenResultBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoOpenResultBtn_Click(sender, e);
    private void VideoPresetApplyBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoPresetApplyBtn_Click(sender, e);
    private void VideoPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.VideoPresetCombo_SelectionChanged(sender, e);
    private void VideoPresetDeleteBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoPresetDeleteBtn_Click(sender, e);
    private void VideoPresetSaveBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoPresetSaveBtn_Click(sender, e);
    private void VideoPreviewBorder_Click(object sender, MouseButtonEventArgs e) => Host?.VideoPreviewBorder_Click(sender, e);
    private void VideoSingleDiscussBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoSingleDiscussBtn_Click(sender, e);
    private void VideoStartDrop_Drop(object sender, System.Windows.DragEventArgs e) => Host?.VideoStartDrop_Drop(sender, e);
    private void VideoTtsMuxBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoTtsMuxBtn_Click(sender, e);
    private void VideoTtsPreviewBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoTtsPreviewBtn_Click(sender, e);
    private void VideoUseLastSeedBtn_Click(object sender, RoutedEventArgs e) => Host?.VideoUseLastSeedBtn_Click(sender, e);
}
