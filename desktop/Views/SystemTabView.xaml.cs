using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalAIHub.Views;

public partial class SystemTabView : System.Windows.Controls.UserControl
{
    public SystemTabView()
    {
        InitializeComponent();
    }

    private MainWindow? Host => IsLoaded ? Window.GetWindow(this) as MainWindow : null;

    private void ApplyQwenAntiLoopBtn_Click(object sender, RoutedEventArgs e) => Host?.ApplyQwenAntiLoopBtn_Click(sender, e);
    private void AskAiTuningSettingsBtn_Click(object sender, RoutedEventArgs e) => Host?.AskAiTuningSettingsBtn_Click(sender, e);
    private void CfgLlmIdleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.CfgLlmIdleCombo_SelectionChanged(sender, e);
    private void CopyDiagnosticReportBtn_Click(object sender, RoutedEventArgs e) => Host?.CopyDiagnosticReportBtn_Click(sender, e);
    private void CopyMcpJsonBtn_Click(object sender, RoutedEventArgs e) => Host?.CopyMcpJsonBtn_Click(sender, e);
    private void ExportProfileBtn_Click(object sender, RoutedEventArgs e) => Host?.ExportProfileBtn_Click(sender, e);
    private void HfConnectBtn_Click(object sender, RoutedEventArgs e) => Host?.HfConnectBtn_Click(sender, e);
    private void HfDisconnectBtn_Click(object sender, RoutedEventArgs e) => Host?.HfDisconnectBtn_Click(sender, e);
    private void HfOpenTokensPageBtn_Click(object sender, RoutedEventArgs e) => Host?.HfOpenTokensPageBtn_Click(sender, e);
    private void ImportProfileBtn_Click(object sender, RoutedEventArgs e) => Host?.ImportProfileBtn_Click(sender, e);
    private void InstallComfyBtn_Click(object sender, RoutedEventArgs e) => Host?.InstallComfyBtn_Click(sender, e);
    private void InstallLlamaBtn_Click(object sender, RoutedEventArgs e) => Host?.InstallLlamaBtn_Click(sender, e);
    private void InstallPiperBtn_Click(object sender, RoutedEventArgs e) => Host?.InstallPiperBtn_Click(sender, e);
    private void InstallGigaAmBtn_Click(object sender, RoutedEventArgs e) => Host?.InstallGigaAmBtn_Click(sender, e);
    private void InstallParakeetBtn_Click(object sender, RoutedEventArgs e) => Host?.InstallParakeetBtn_Click(sender, e);
    private void InstallWhisperBtn_Click(object sender, RoutedEventArgs e) => Host?.InstallWhisperBtn_Click(sender, e);
    private void JumpAssistantBtn_Click(object sender, RoutedEventArgs e) => Host?.JumpAssistantBtn_Click(sender, e);
    private void JumpCatalogBtn_Click(object sender, RoutedEventArgs e) => Host?.JumpCatalogBtn_Click(sender, e);
    private void JumpPhotoBtn_Click(object sender, RoutedEventArgs e) => Host?.JumpPhotoBtn_Click(sender, e);
    private void JumpVideoBtn_Click(object sender, RoutedEventArgs e) => Host?.JumpVideoBtn_Click(sender, e);
    private void LogServiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.LogServiceCombo_SelectionChanged(sender, e);
    private void OpenConfigJsonBtn_Click(object sender, RoutedEventArgs e) => Host?.OpenConfigJsonBtn_Click(sender, e);
    private void RefreshLogBtn_Click(object sender, RoutedEventArgs e) => Host?.RefreshLogBtn_Click(sender, e);
    private void RegisterAntigravityMcpBtn_Click(object sender, RoutedEventArgs e) => Host?.RegisterAntigravityMcpBtn_Click(sender, e);
    private void RegisterClaudeMcpBtn_Click(object sender, RoutedEventArgs e) => Host?.RegisterClaudeMcpBtn_Click(sender, e);
    private void SaveHubSettingsBtn_Click(object sender, RoutedEventArgs e) => Host?.SaveHubSettingsBtn_Click(sender, e);
    private void SysTabGuideBtn_Click(object sender, RoutedEventArgs e) => Host?.SysTabGuideBtn_Click(sender, e);
    private void SysTabLogsBtn_Click(object sender, RoutedEventArgs e) => Host?.SysTabLogsBtn_Click(sender, e);
    private void SysTabServicesBtn_Click(object sender, RoutedEventArgs e) => Host?.SysTabServicesBtn_Click(sender, e);
    private void SysTabSettingsBtn_Click(object sender, RoutedEventArgs e) => Host?.SysTabSettingsBtn_Click(sender, e);
    private void TestMcpBtn_Click(object sender, RoutedEventArgs e) => Host?.TestMcpBtn_Click(sender, e);
    private void UpdateCheckBtn_Click(object sender, RoutedEventArgs e) => Host?.UpdateCheckBtn_Click(sender, e);
    private void UpdateInstallNowBtn_Click(object sender, RoutedEventArgs e) => Host?.UpdateInstallNowBtn_Click(sender, e);
    private void UpdateServerResetBtn_Click(object sender, RoutedEventArgs e) => Host?.UpdateServerResetBtn_Click(sender, e);
    private void UpdateServerSaveBtn_Click(object sender, RoutedEventArgs e) => Host?.UpdateServerSaveBtn_Click(sender, e);
    private void UpdateServerToggleBtn_Click(object sender, RoutedEventArgs e) => Host?.UpdateServerToggleBtn_Click(sender, e);
    private void OpenDesktopAssistantBtn_Click(object sender, RoutedEventArgs e) => Host?.OpenDesktopAssistantBtn_Click(sender, e);
    private void OpenNotesViewerBtn_Click(object sender, RoutedEventArgs e) => Host?.OpenNotesViewerBtn_Click(sender, e);
    private void BrowseAssistNotesDirBtn_Click(object sender, RoutedEventArgs e) => Host?.BrowseAssistNotesDirBtn_Click(sender, e);
    private void SaveDesktopAssistantBtn_Click(object sender, RoutedEventArgs e) => Host?.SaveDesktopAssistantBtn_Click(sender, e);
}
