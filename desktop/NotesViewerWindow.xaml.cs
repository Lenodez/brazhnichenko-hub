using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LocalAIHub.Services;
using MediaColor = System.Windows.Media.Color;

namespace LocalAIHub;

public partial class NotesViewerWindow : Window
{
    private List<AssistantNoteEntry> _allNotes = [];
    private List<AssistantNoteEntry> _filteredNotes = [];
    private AssistantNoteEntry? _current;
    private string? _activeTag;
    private bool _editMode;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public NotesViewerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        };
        ReloadNotes();
    }

    public void ReloadNotes(string? selectFilePath = null)
    {
        string dir = AssistantNotesService.ResolveNotesDirectory();
        NotesDirText.Text = dir;
        _allNotes = AssistantNotesService.ListNotes();
        RebuildTagFilterChips();
        ApplyFilterAndBind(selectFilePath ?? _current?.FilePath);
    }

    public void SelectNoteByPath(string filePath)
    {
        ReloadNotes(filePath);
    }

    private void RebuildTagFilterChips()
    {
        TagFilterPanel.Children.Clear();
        var topTags = _allNotes
            .SelectMany(n => n.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Take(8)
            .Select(g => g.Key)
            .ToList();

        if (topTags.Count == 0) return;

        AddTagChip("Все", null);
        foreach (string tag in topTags)
            AddTagChip("#" + tag, tag);
    }

    private void AddTagChip(string label, string? tagValue)
    {
        bool isSelected = string.Equals(_activeTag, tagValue, StringComparison.OrdinalIgnoreCase)
            || (_activeTag is null && tagValue is null);
        var btn = new System.Windows.Controls.Button
        {
            Content = label,
            Style = (Style)FindResource(isSelected ? "PrimaryButton" : "DarkButton"),
            Padding = new Thickness(7, 2.5, 7, 2.5),
            FontSize = 10.5,
            Margin = new Thickness(0, 0, 4, 4),
            Tag = tagValue
        };
        btn.Click += (_, _) =>
        {
            _activeTag = tagValue;
            RebuildTagFilterChips();
            ApplyFilterAndBind(_current?.FilePath);
        };
        TagFilterPanel.Children.Add(btn);
    }

    private void ApplyFilterAndBind(string? selectFilePath = null)
    {
        string q = SearchBox.Text.Trim();
        _filteredNotes = AssistantNotesService.ListNotes(
            searchQuery: string.IsNullOrWhiteSpace(q) ? null : q,
            tagFilter: _activeTag);

        NotesCountBadge.Text = _filteredNotes.Count == _allNotes.Count
            ? $"{_allNotes.Count} заметок"
            : $"{_filteredNotes.Count} / {_allNotes.Count}";

        NotesListBox.Items.Clear();
        int selectIdx = -1;
        for (int i = 0; i < _filteredNotes.Count; i++)
        {
            var note = _filteredNotes[i];
            NotesListBox.Items.Add(BuildNoteListItemVisual(note));
            if (!string.IsNullOrWhiteSpace(selectFilePath) &&
                string.Equals(note.FilePath, selectFilePath, StringComparison.OrdinalIgnoreCase))
            {
                selectIdx = i;
            }
        }

        if (_filteredNotes.Count > 0)
        {
            NotesListBox.SelectedIndex = selectIdx >= 0 ? selectIdx : 0;
        }
        else
        {
            _current = null;
            ShowEmptyState();
        }
    }

    private static StackPanel BuildNoteListItemVisual(AssistantNoteEntry note)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = note.Title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(244, 244, 246)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        string tagsLine = note.Tags.Length > 0 ? " · " + string.Join(" ", note.Tags.Take(3).Select(t => "#" + t)) : "";
        sp.Children.Add(new TextBlock
        {
            Text = $"{note.CreatedLocal:dd.MM.yyyy HH:mm}{tagsLine}",
            FontSize = 10.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(126, 126, 135)),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (!string.IsNullOrWhiteSpace(note.Preview))
        {
            sp.Children.Add(new TextBlock
            {
                Text = note.Preview,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(161, 161, 170)),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        return sp;
    }

    private void NotesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = NotesListBox.SelectedIndex;
        if (idx < 0 || idx >= _filteredNotes.Count)
        {
            ShowEmptyState();
            return;
        }
        _current = _filteredNotes[idx];
        SetEditMode(false);
        RenderCurrentNote();
    }

    private void RenderCurrentNote()
    {
        if (_current is null)
        {
            ShowEmptyState();
            return;
        }

        NoteToolbarBorder.Visibility = Visibility.Visible;
        SelectedTitleText.Text = _current.Title;
        string tagsText = _current.Tags.Length > 0 ? "  ·  " + string.Join(" ", _current.Tags.Select(t => "#" + t)) : "";
        SelectedMetaText.Text = $"{_current.CreatedLocal:yyyy-MM-dd HH:mm}  ·  {_current.FileName}{tagsText}";

        PreviewContentPanel.Children.Clear();
        PreviewContentPanel.Children.Add(MarkdownUiRenderer.Build(_current.BodyMarkdown, 13.5));
        RawEditorBox.Text = _current.RawMarkdown;
        StatusFooterText.Text = _current.FilePath;
    }

    private void ShowEmptyState()
    {
        NoteToolbarBorder.Visibility = Visibility.Collapsed;
        SetEditMode(false);
        PreviewContentPanel.Children.Clear();
        PreviewContentPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(18, 18, 20)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(40, 40, 46)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(18, 16, 18, 16),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Заметок пока нет",
                        FontSize = 15,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(MediaColor.FromRgb(244, 244, 246))
                    },
                    new TextBlock
                    {
                        Text = $"Нажми Alt+Space и скажи или напиши «Сделай заметку ...» — файл .md автоматически сохранится в:\n{AssistantNotesService.ResolveNotesDirectory()}\n\nПапку можно подключить как Vault в Obsidian или открыть в любом редакторе.",
                        FontSize = 12.5,
                        Foreground = new SolidColorBrush(MediaColor.FromRgb(161, 161, 170)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0)
                    }
                }
            }
        });
    }

    private void SetEditMode(bool editing)
    {
        _editMode = editing;
        PreviewScroll.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        RawEditorBox.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        SaveEditBtn.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ToggleEditBtn.Content = editing ? "Просмотр" : "Редактировать";
        if (editing) RawEditorBox.Focus();
    }

    private void ToggleEditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (!_editMode)
        {
            RawEditorBox.Text = _current.RawMarkdown;
            SetEditMode(true);
        }
        else
        {
            SetEditMode(false);
            RenderCurrentNote();
        }
    }

    private void SaveEditBtn_Click(object sender, RoutedEventArgs e) => SaveCurrentEdit();

    private void SaveCurrentEdit()
    {
        if (_current is null || !_editMode) return;
        try
        {
            var updated = AssistantNotesService.UpdateNote(_current.FilePath, RawEditorBox.Text);
            _current = updated;
            SetEditMode(false);
            ReloadNotes(updated.FilePath);
            StatusFooterText.Text = $"Сохранено: {updated.FileName}";
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = "Ошибка сохранения: " + ex.Message;
        }
    }

    private void NewNoteBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var created = AssistantNotesService.SaveNote("Новая заметка", "Текст заметки...", ["manual"]);
            ReloadNotes(created.FilePath);
            SetEditMode(true);
            StatusFooterText.Text = $"Создана заметка: {created.FileName}";
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = "Ошибка создания заметки: " + ex.Message;
        }
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        ReloadNotes(_current?.FilePath);
        StatusFooterText.Text = "Список заметок обновлён";
    }

    private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        string dir = AssistantNotesService.ResolveNotesDirectory();
        Directory.CreateDirectory(dir);
        AiServiceManager.OpenExternal(dir);
    }

    private void CopyNoteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        try
        {
            System.Windows.Clipboard.SetText(_current.BodyMarkdown);
            StatusFooterText.Text = "Текст заметки скопирован в буфер";
        }
        catch { }
    }

    private void OpenExternalBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || !File.Exists(_current.FilePath)) return;
        AiServiceManager.OpenExternal(_current.FilePath);
    }

    private void DeleteNoteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var res = HubDialogWindow.Show(
            $"Удалить заметку «{_current.Title}»?\nФайл: {_current.FileName}",
            "Удаление заметки",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;

        try
        {
            AssistantNotesService.DeleteNote(_current.FilePath);
            _current = null;
            ReloadNotes();
            StatusFooterText.Text = "Заметка удалена";
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = "Ошибка удаления: " + ex.Message;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilterAndBind(_current?.FilePath);

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && _editMode)
        {
            e.Handled = true;
            SaveCurrentEdit();
        }
        else if (e.Key == Key.Escape && !_editMode)
        {
            e.Handled = true;
            Close();
        }
    }
}
