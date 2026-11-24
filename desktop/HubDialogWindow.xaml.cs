using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LocalAIHub;

public partial class HubDialogWindow : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;
    private MessageBoxResult _cancelResult = MessageBoxResult.Cancel;
    private MessageBoxResult _defaultResult = MessageBoxResult.OK;

    public HubDialogWindow()
    {
        InitializeComponent();
    }

    public static MessageBoxResult Show(
        string message,
        string title = "Brazhnichenko Hub",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.Information,
        Window? owner = null)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null || !app.Dispatcher.CheckAccess())
            {
                if (app is not null)
                    return app.Dispatcher.Invoke(() => Show(message, title, buttons, icon, owner));
                return System.Windows.MessageBox.Show(message, title, buttons, icon);
            }

            var dlg = new HubDialogWindow();
            Window? resolvedOwner = owner;
            if (resolvedOwner is null)
            {
                foreach (Window w in app.Windows)
                {
                    if (w != dlg && w.IsLoaded && w.IsVisible)
                    {
                        resolvedOwner = w;
                        if (w.IsActive) break;
                    }
                }
            }

            if (resolvedOwner is not null && resolvedOwner != dlg)
            {
                dlg.Owner = resolvedOwner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dlg.Configure(message, title, buttons, icon);
            dlg.ShowDialog();
            return dlg._result == MessageBoxResult.None ? dlg._cancelResult : dlg._result;
        }
        catch
        {
            return System.Windows.MessageBox.Show(message, title, buttons, icon);
        }
    }

    private void Configure(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "Brazhnichenko Hub" : title;
        TitleText.Text = Title;
        MessageBoxText.Text = message ?? "";

        // Style icon badge by severity
        string geomKey;
        string strokeHex;
        string bgHex;
        switch (icon)
        {
            case MessageBoxImage.Error:
                geomKey = "IconError";
                strokeHex = "#EF4444";
                bgHex = "#38191E";
                break;
            case MessageBoxImage.Warning:
                geomKey = "IconAlert";
                strokeHex = "#F59E0B";
                bgHex = "#332712";
                break;
            case MessageBoxImage.Question:
                geomKey = "IconSparkles";
                strokeHex = "#5B8CFF";
                bgHex = "#182747";
                break;
            default:
                geomKey = "IconInfo";
                strokeHex = "#5B8CFF";
                bgHex = "#182747";
                break;
        }

        if (TryFindResource(geomKey) is Geometry g)
            IconPath.Data = g;
        IconPath.Stroke = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(strokeHex));
        IconBadge.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(bgHex));

        // Build localized action buttons
        ButtonsPanel.Children.Clear();
        bool destructive = icon == MessageBoxImage.Warning || icon == MessageBoxImage.Error;

        void AddBtn(string label, MessageBoxResult res, string styleKey, bool isDefault = false)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                MinWidth = 86,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault
            };
            if (TryFindResource(styleKey) is Style st)
                btn.Style = st;
            btn.Click += (_, _) =>
            {
                _result = res;
                DialogResult = true;
            };
            ButtonsPanel.Children.Add(btn);
        }

        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                _defaultResult = MessageBoxResult.OK;
                _cancelResult = MessageBoxResult.Cancel;
                AddBtn("Отмена", MessageBoxResult.Cancel, "DarkButton");
                AddBtn("ОК", MessageBoxResult.OK, destructive ? "DangerButton" : "PrimaryButton", isDefault: true);
                break;

            case MessageBoxButton.YesNo:
                _defaultResult = MessageBoxResult.Yes;
                _cancelResult = MessageBoxResult.No;
                AddBtn("Нет", MessageBoxResult.No, "DarkButton");
                AddBtn("Да", MessageBoxResult.Yes, destructive ? "DangerButton" : "PrimaryButton", isDefault: true);
                break;

            case MessageBoxButton.YesNoCancel:
                _defaultResult = MessageBoxResult.Yes;
                _cancelResult = MessageBoxResult.Cancel;
                AddBtn("Отмена", MessageBoxResult.Cancel, "GhostButton");
                AddBtn("Нет", MessageBoxResult.No, "DarkButton");
                AddBtn("Да", MessageBoxResult.Yes, destructive ? "DangerButton" : "PrimaryButton", isDefault: true);
                break;

            default:
                _defaultResult = MessageBoxResult.OK;
                _cancelResult = MessageBoxResult.OK;
                AddBtn("ОК", MessageBoxResult.OK, "PrimaryButton", isDefault: true);
                break;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseHeaderBtn_Click(object sender, RoutedEventArgs e)
    {
        _result = _cancelResult;
        DialogResult = false;
    }

    private void CopyTextBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText($"{TitleText.Text}\n\n{MessageBoxText.Text}");
            CopyTextLabel.Text = "Скопировано";
        }
        catch { }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _result = _cancelResult;
            DialogResult = false;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _result = _defaultResult;
            DialogResult = true;
        }
    }
}
