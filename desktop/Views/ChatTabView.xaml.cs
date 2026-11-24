using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalAIHub.Views;

public partial class ChatTabView : System.Windows.Controls.UserControl
{
    public ChatTabView()
    {
        InitializeComponent();
    }

    private MainWindow? Host => IsLoaded ? Window.GetWindow(this) as MainWindow : null;

    private void ApplyQwenAntiLoopBtn_Click(object sender, RoutedEventArgs e) => Host?.ApplyQwenAntiLoopBtn_Click(sender, e);
    private void AssistantBackBtn_Click(object sender, RoutedEventArgs e) => Host?.AssistantBackBtn_Click(sender, e);
    private void AssistantClearBtn_Click(object sender, RoutedEventArgs e) => Host?.AssistantClearBtn_Click(sender, e);
    private void AssistantInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Host?.AssistantInputBox_PreviewKeyDown(sender, e);
    private void AssistantNetCheckBtn_Click(object sender, RoutedEventArgs e) => Host?.AssistantNetCheckBtn_Click(sender, e);
    private void AssistantQuickChip_Click(object sender, RoutedEventArgs e) => Host?.AssistantQuickChip_Click(sender, e);
    private void AssistantSendBtn_Click(object sender, RoutedEventArgs e) => Host?.AssistantSendBtn_Click(sender, e);
    private void ChatJumpCatalogTextBtn_Click(object sender, RoutedEventArgs e) => Host?.ChatJumpCatalogTextBtn_Click(sender, e);
    private void ChatLlmClaudeBtn_Click(object sender, RoutedEventArgs e) => Host?.ChatLlmClaudeBtn_Click(sender, e);
    private void ChatLlmModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.ChatLlmModelCombo_SelectionChanged(sender, e);
    private void ChatLlmOpenUiBtn_Click(object sender, RoutedEventArgs e) => Host?.ChatLlmOpenUiBtn_Click(sender, e);
    private void ChatLlmStartBtn_Click(object sender, RoutedEventArgs e) => Host?.ChatLlmStartBtn_Click(sender, e);
    private void ChatLlmStopBtn_Click(object sender, RoutedEventArgs e) => Host?.ChatLlmStopBtn_Click(sender, e);
    private void ChatModeAssistantBtn_Click(object sender, RoutedEventArgs e) => Host?.ChatModeAssistantBtn_Click(sender, e);
    private void ChatModeLlmBtn_Click(object sender, RoutedEventArgs e) => Host?.ChatModeLlmBtn_Click(sender, e);
    private void CopyStackContextBtn_Click(object sender, RoutedEventArgs e) => Host?.CopyStackContextBtn_Click(sender, e);
    private void ImageDrop_DragOver(object sender, System.Windows.DragEventArgs e) => Host?.ImageDrop_DragOver(sender, e);
    private void InstallAssistantBtn_Click(object sender, RoutedEventArgs e) => Host?.InstallAssistantBtn_Click(sender, e);
    private void LocalChat_Drop(object sender, System.Windows.DragEventArgs e) => Host?.LocalChat_Drop(sender, e);
    private void LocalChatAttachImgBtn_Click(object sender, RoutedEventArgs e) => Host?.LocalChatAttachImgBtn_Click(sender, e);
    private void LocalChatClearBtn_Click(object sender, RoutedEventArgs e) => Host?.LocalChatClearBtn_Click(sender, e);
    private void LocalChatClearImgBtn_Click(object sender, RoutedEventArgs e) => Host?.LocalChatClearImgBtn_Click(sender, e);
    private void LocalChatDeleteSessionBtn_Click(object sender, RoutedEventArgs e) => Host?.LocalChatDeleteSessionBtn_Click(sender, e);
    private void LocalChatInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Host?.LocalChatInputBox_PreviewKeyDown(sender, e);
    private void LocalChatMicBtn_Click(object sender, RoutedEventArgs e) => Host?.LocalChatMicBtn_Click(sender, e);
    private void LocalChatNewBtn_Click(object sender, RoutedEventArgs e) => Host?.LocalChatNewBtn_Click(sender, e);
    private void LocalChatPersonaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.LocalChatPersonaCombo_SelectionChanged(sender, e);
    private void LocalChatRenameBtn_Click(object sender, RoutedEventArgs e) => Host?.LocalChatRenameBtn_Click(sender, e);
    private void LocalChatSearchBox_TextChanged(object sender, TextChangedEventArgs e) => Host?.LocalChatSearchBox_TextChanged(sender, e);
    private void LocalChatSendBtn_Click(object sender, RoutedEventArgs e) => Host?.LocalChatSendBtn_Click(sender, e);
    private void LocalChatSessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.LocalChatSessionsList_SelectionChanged(sender, e);
    private void LocalChatStopBtn_Click(object sender, RoutedEventArgs e) => Host?.LocalChatStopBtn_Click(sender, e);
    private void LocalChatTempSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Host?.LocalChatTempSlider_ValueChanged(sender, e);
    private void LocalChatToggleSidebarBtn_Click(object sender, RoutedEventArgs e) => Host?.LocalChatToggleSidebarBtn_Click(sender, e);
    private void OpenStackContextFileBtn_Click(object sender, RoutedEventArgs e) => Host?.OpenStackContextFileBtn_Click(sender, e);
}
