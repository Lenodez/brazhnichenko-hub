using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalAIHub.Services;

namespace LocalAIHub;

public partial class MainWindow
{
    private sealed class GalleryMediaItem
    {
        public required string FullPath { get; init; }
        public required string FileName { get; init; }
        public required string FolderTag { get; init; }
        public required string Kind { get; init; } // "photo" | "video"
        public required bool IsTemp { get; init; }
        public required long LengthBytes { get; init; }
        public required DateTime LastWriteTime { get; init; }
        public string? PairedThumbPath { get; init; }
        public double SizeMB => LengthBytes / (1024.0 * 1024.0);
    }

    private static readonly ConcurrentDictionary<string, BitmapImage> _thumbCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> _thumbCacheOrder = new();
    private static readonly SemaphoreSlim _thumbSem = new(4, 4);
    private static readonly SemaphoreSlim _videoThumbSem = new(2, 2);
    private const int GalleryThumbCacheMaxEntries = 140;

    private static string VideoThumbCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAIHub",
        "thumb_cache");

    private CancellationTokenSource? _galleryThumbCts;
    private GenerationMetadata? _galleryCurrentMeta;

    private List<GalleryMediaItem> _galleryAllItems = [];
    private List<GalleryMediaItem> _galleryFiltered = [];
    private readonly HashSet<string> _gallerySelectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _galleryCardBorders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.Windows.Controls.CheckBox> _galleryCardChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.Windows.Controls.Button> _galleryCardFavBtns = new(StringComparer.OrdinalIgnoreCase);

    private string _galleryFilter = "all"; // all | photo | video | fav | temp
    private string? _galleryCurrentPath;
    private int _galleryVisibleLimit = 80;
    private bool _galleryLoading;
    private bool _galleryVideoPlaying;

    // Win32 Recycle Bin delete (safe undoable deletion without external assembly refs)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private static bool SendFilesToRecycleBin(IEnumerable<string> paths, out string? error)
    {
        error = null;
        var existing = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0) return true;

        // SHFileOperation expects double-null terminated list of paths
        string pFrom = string.Join('\0', existing) + "\0\0";
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 3, // FO_DELETE
            pFrom = pFrom,
            fFlags = 0x0040 | 0x0010 | 0x0004 // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
        };
        int res = SHFileOperation(ref op);
        if (res != 0)
        {
            error = $"Не удалось переместить файлы в Корзину (код {res}). Возможно, файл открыт другой программой.";
            return false;
        }
        return true;
    }

    private void CancelGalleryThumbLoads()
    {
        var old = Interlocked.Exchange(ref _galleryThumbCts, null);
        if (old is not null)
        {
            try { old.Cancel(); } catch { }
            try { old.Dispose(); } catch { }
        }
    }

    private static void PutGalleryThumbCache(string key, BitmapImage bmp)
    {
        if (_thumbCache.TryAdd(key, bmp))
        {
            _thumbCacheOrder.Enqueue(key);
            while (_thumbCache.Count > GalleryThumbCacheMaxEntries && _thumbCacheOrder.TryDequeue(out string? oldest))
                _thumbCache.TryRemove(oldest, out _);

            if (_thumbCache.Count > GalleryThumbCacheMaxEntries + 50)
            {
                _thumbCache.Clear();
                while (_thumbCacheOrder.TryDequeue(out _)) { }
                _thumbCache[key] = bmp;
                _thumbCacheOrder.Enqueue(key);
            }
        }
        else
        {
            _thumbCache[key] = bmp;
        }
    }

    private static string GetVideoThumbCachePath(string videoPath)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(videoPath.ToLowerInvariant()));
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(VideoThumbCacheDir, hex + ".jpg");
    }

    private static void TryDeleteVideoThumbCacheFile(string mediaPath)
    {
        try
        {
            string cachePath = GetVideoThumbCachePath(mediaPath);
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch { }
    }

    private void StopGalleryVideoPlayback()
    {
        if (GalleryVideoPlayer is null) return;
        try
        {
            GalleryVideoPlayer.Stop();
            GalleryVideoPlayer.Close();
            GalleryVideoPlayer.Source = null;
        }
        catch { }
        _galleryVideoPlaying = false;
    }

    private void StopGalleryVideo()
    {
        StopGalleryVideoPlayback();
        CancelGalleryThumbLoads();
    }

    internal static void ClearGalleryThumbCache()
    {
        _thumbCache.Clear();
        while (_thumbCacheOrder.TryDequeue(out _)) { }
    }

    private void ReleaseGalleryUiMemory()
    {
        StopGalleryVideo();
        if (GalleryPreviewImage is not null)
            GalleryPreviewImage.Source = null;
        if (GalleryItemsPanel is not null)
            GalleryItemsPanel.Children.Clear();
        _galleryCardBorders.Clear();
        _galleryCardChecks.Clear();
        _galleryCardFavBtns.Clear();
        if (_thumbCache.Count > 60)
            ClearGalleryThumbCache();
    }

    private async Task RefreshGalleryAsync(bool preserveSelection = false)
    {
        if (_galleryLoading) return;
        _galleryLoading = true;
        GalleryStatsText.Text = "Сканирую сгенерированные файлы...";

        string? prevCurrent = preserveSelection ? _galleryCurrentPath : null;
        if (!preserveSelection) _gallerySelectedPaths.Clear();

        try
        {
            var scanned = await Task.Run(() =>
            {
                GalleryFavoritesStore.PruneMissing();
                return ScanGalleryDisk();
            });
            _galleryAllItems = scanned;
            // Prune deleted paths from selection
            var existingSet = scanned.Select(x => x.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _gallerySelectedPaths.RemoveWhere(p => !existingSet.Contains(p));

            _galleryVisibleLimit = 80;
            ApplyGalleryFilterAndRender(prevCurrent);
        }
        finally
        {
            _galleryLoading = false;
        }
    }

    private static List<GalleryMediaItem> ScanGalleryDisk()
    {
        var results = new List<GalleryMediaItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool IsPhotoExt(string ext) =>
            ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);

        static bool IsVideoExt(string ext) =>
            ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".gif", StringComparison.OrdinalIgnoreCase);

        void ScanFolder(string rootDir, string defaultTag, bool forceTemp)
        {
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return;
            try
            {
                foreach (string file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
                {
                    if (!seen.Add(file)) continue;
                    string ext = Path.GetExtension(file);
                    bool isPhoto = IsPhotoExt(ext);
                    bool isVideo = IsVideoExt(ext);
                    if (!isPhoto && !isVideo) continue;

                    FileInfo fi;
                    try { fi = new FileInfo(file); } catch { continue; }
                    if (!fi.Exists || fi.Length == 0) continue;

                    string relDir = Path.GetRelativePath(rootDir, fi.DirectoryName ?? rootDir);
                    string folderTag = relDir == "." ? defaultTag : relDir.Replace('\\', '/');
                    bool isTemp = forceTemp ||
                                  folderTag.Contains("studio_frames", StringComparison.OrdinalIgnoreCase) ||
                                  folderTag.Contains("video_frames", StringComparison.OrdinalIgnoreCase) ||
                                  folderTag.Equals("input", StringComparison.OrdinalIgnoreCase);

                    string? pairedThumb = null;
                    if (isVideo)
                    {
                        string baseNoExt = Path.Combine(fi.DirectoryName ?? "", Path.GetFileNameWithoutExtension(fi.Name));
                        if (File.Exists(baseNoExt + ".png")) pairedThumb = baseNoExt + ".png";
                        else if (File.Exists(baseNoExt + ".jpg")) pairedThumb = baseNoExt + ".jpg";
                    }

                    results.Add(new GalleryMediaItem
                    {
                        FullPath = fi.FullName,
                        FileName = fi.Name,
                        FolderTag = folderTag,
                        Kind = isVideo ? "video" : "photo",
                        IsTemp = isTemp,
                        LengthBytes = fi.Length,
                        LastWriteTime = fi.LastWriteTime,
                        PairedThumbPath = pairedThumb
                    });
                }
            }
            catch { }
        }

        string comfyOutput = HubConfig.ComfyOutputDir;
        string comfyInput = Path.Combine(HubConfig.ComfyRootDir, "input");
        string storiesRoot = HubConfig.StoriesRoot;

        ScanFolder(comfyOutput, "output", forceTemp: false);
        ScanFolder(storiesRoot, "stories", forceTemp: false);
        ScanFolder(comfyInput, "ComfyUI/input", forceTemp: true);

        return results;
    }

    private void ApplyGalleryFilterAndRender(string? selectPath = null)
    {
        string query = (GallerySearchBox?.Text ?? "").Trim();
        string sortTag = (GallerySortCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "newest";

        IEnumerable<GalleryMediaItem> q = _galleryAllItems;
        q = _galleryFilter switch
        {
            "photo" => q.Where(x => !x.IsTemp && x.Kind == "photo"),
            "video" => q.Where(x => !x.IsTemp && x.Kind == "video"),
            "fav" => q.Where(x => GalleryFavoritesStore.IsFavorite(x.FullPath)),
            "temp" => q.Where(x => x.IsTemp),
            _ => q.Where(x => !x.IsTemp) // "all" shows all real outputs (not temp cache)
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            q = q.Where(x =>
                x.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.FolderTag.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        q = sortTag switch
        {
            "oldest" => q.OrderBy(x => x.LastWriteTime),
            "largest" => q.OrderByDescending(x => x.LengthBytes),
            _ => q.OrderByDescending(x => x.LastWriteTime)
        };

        _galleryFiltered = q.ToList();
        RenderGalleryGrid();
        UpdateGalleryStatsAndButtons();

        // Select requested item or keep first visible
        if (!string.IsNullOrWhiteSpace(selectPath) && _galleryFiltered.Any(x => x.FullPath.Equals(selectPath, StringComparison.OrdinalIgnoreCase)))
        {
            SelectGalleryPreview(selectPath);
        }
        else if (_galleryFiltered.Count > 0)
        {
            SelectGalleryPreview(_galleryFiltered[0].FullPath);
        }
        else
        {
            ClearGalleryPreview();
        }
    }

    private void RenderGalleryGrid()
    {
        CancelGalleryThumbLoads();
        _galleryThumbCts = new CancellationTokenSource();
        var ct = _galleryThumbCts.Token;

        GalleryItemsPanel.Children.Clear();
        _galleryCardBorders.Clear();
        _galleryCardChecks.Clear();
        _galleryCardFavBtns.Clear();

        if (_galleryFiltered.Count == 0)
        {
            string emptyMsg = _galleryFilter switch
            {
                "temp" => "Временные файлы и кэш ComfyUI\\input уже пусты — порядок!",
                "fav" => "Избранных файлов пока нет. Нажми ☆ на карточке или клавишу F, чтобы защитить лучшие фото и видео от чистки.",
                _ => "По этому фильтру файлов пока нет. Сгенерируй фото или видео, либо переключи фильтр сверху."
            };
            GalleryItemsPanel.Children.Add(new TextBlock
            {
                Text = emptyMsg,
                Foreground = BrushFromHex("#7E8BA6"),
                FontSize = 13,
                Margin = new Thickness(12, 18, 12, 18),
                TextWrapping = TextWrapping.Wrap
            });
            GalleryLoadMoreBtn.Visibility = Visibility.Collapsed;
            return;
        }

        int showCount = Math.Min(_galleryVisibleLimit, _galleryFiltered.Count);
        for (int i = 0; i < showCount; i++)
        {
            var item = _galleryFiltered[i];
            GalleryItemsPanel.Children.Add(BuildGalleryCard(item, ct));
        }

        if (_galleryFiltered.Count > showCount)
        {
            GalleryLoadMoreBtn.Content = $"Показать ещё (осталось {_galleryFiltered.Count - showCount})";
            GalleryLoadMoreBtn.Visibility = Visibility.Visible;
        }
        else
        {
            GalleryLoadMoreBtn.Visibility = Visibility.Collapsed;
        }
    }

    private Border BuildGalleryCard(GalleryMediaItem item, CancellationToken ct)
    {
        bool isChecked = _gallerySelectedPaths.Contains(item.FullPath);
        bool isCurrent = string.Equals(_galleryCurrentPath, item.FullPath, StringComparison.OrdinalIgnoreCase);
        bool isFav = GalleryFavoritesStore.IsFavorite(item.FullPath);

        var card = new Border
        {
            Width = 158,
            Background = BrushFromHex(isCurrent ? "#1D2840" : "#151A25"),
            BorderBrush = BrushFromHex(isCurrent ? "#5B8CFF" : isChecked ? "#E07A5F" : isFav ? "#B8942E" : "#262F42"),
            BorderThickness = new Thickness(isCurrent ? 1.5 : 1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var root = new StackPanel();

        // Thumbnail container
        var thumbGrid = new Grid
        {
            Height = 104,
            Background = BrushFromHex("#0A0D13"),
            ClipToBounds = true
        };

        bool isGif = item.FileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        string? imgPath = (item.Kind == "photo" || isGif) ? item.FullPath : item.PairedThumbPath;

        if (_thumbCache.TryGetValue(item.FullPath, out var cachedThumb))
        {
            thumbGrid.Children.Add(new System.Windows.Controls.Image
            {
                Source = cachedThumb,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else
        {
            var placeholder = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = item.Kind == "video" ? "🎬" : "🖼",
                        FontSize = 26,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Opacity = item.Kind == "video" ? 0.9 : 0.45
                    },
                    new TextBlock
                    {
                        Text = Path.GetExtension(item.FileName).TrimStart('.').ToUpperInvariant() + $" • {item.SizeMB:F1} МБ",
                        FontSize = 10.5,
                        Foreground = BrushFromHex("#9EC0FF"),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Margin = new Thickness(0, 3, 0, 0),
                        Opacity = item.Kind == "video" ? 0.95 : 0.65
                    }
                }
            };
            var asyncImg = new System.Windows.Controls.Image
            {
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            thumbGrid.Children.Add(placeholder);
            thumbGrid.Children.Add(asyncImg);

            if (!string.IsNullOrEmpty(imgPath))
            {
                _ = LoadCardThumbAsync(item.FullPath, imgPath, asyncImg, placeholder, ct);
            }
            else if (item.Kind == "video" &&
                     (item.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                      item.FileName.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)))
            {
                _ = LoadVideoCardThumbAsync(item.FullPath, asyncImg, placeholder, ct);
            }
        }

        // Video badge bottom-left
        if (item.Kind == "video")
        {
            thumbGrid.Children.Add(new Border
            {
                Background = BrushFromHex("#CC0D1320"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4),
                Child = new TextBlock
                {
                    Text = "▶ VIDEO",
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushFromHex("#6AE89E")
                }
            });
        }

        // Selection checkbox top-left
        var chk = new System.Windows.Controls.CheckBox
        {
            IsChecked = isChecked,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            ToolTip = "Отметить для массового удаления"
        };
        chk.Click += (_, ev) =>
        {
            ev.Handled = true;
            if (chk.IsChecked == true) _gallerySelectedPaths.Add(item.FullPath);
            else _gallerySelectedPaths.Remove(item.FullPath);
            UpdateCardHighlight(item.FullPath);
            UpdateGalleryStatsAndButtons();
        };
        thumbGrid.Children.Add(chk);
        _galleryCardChecks[item.FullPath] = chk;

        // Quick 1-click delete to Recycle Bin top-right
        var delBtn = new System.Windows.Controls.Button
        {
            Content = "×",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Background = BrushFromHex("#CC2A1519"),
            Foreground = BrushFromHex("#FF9EA8"),
            BorderBrush = BrushFromHex("#7D3232"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            ToolTip = "Отправить этот файл в Корзину"
        };
        delBtn.Click += (_, ev) =>
        {
            ev.Handled = true;
            DeleteGalleryItems([item.FullPath], askConfirm: false);
        };
        thumbGrid.Children.Add(delBtn);

        // Favorite toggle star bottom-right
        var favBtn = new System.Windows.Controls.Button
        {
            Content = isFav ? "⭐" : "☆",
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 12,
            Background = BrushFromHex("#CC0D1320"),
            Foreground = BrushFromHex(isFav ? "#F5CB5C" : "#8C96AD"),
            BorderBrush = BrushFromHex(isFav ? "#B8942E" : "#32405C"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4),
            ToolTip = isFav ? "Убрать из избранного (F)" : "В избранное — защитить от чистки (F)"
        };
        favBtn.Click += (_, ev) =>
        {
            ev.Handled = true;
            ToggleGalleryFavorite(item.FullPath);
        };
        thumbGrid.Children.Add(favBtn);
        _galleryCardFavBtns[item.FullPath] = favBtn;

        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = thumbGrid
        });

        // Filename & metadata line
        root.Children.Add(new TextBlock
        {
            Text = item.FileName,
            FontSize = 11,
            Foreground = BrushFromHex("#DCE5F7"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(1, 5, 1, 1),
            ToolTip = item.FullPath
        });
        root.Children.Add(new TextBlock
        {
            Text = $"{item.FolderTag} • {FormatSizeShort(item.LengthBytes)}",
            FontSize = 10,
            Foreground = BrushFromHex("#7E8BA6"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(1, 0, 1, 0)
        });

        card.Child = root;
        card.MouseLeftButtonUp += (_, ev) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                bool next = !_gallerySelectedPaths.Contains(item.FullPath);
                if (next) _gallerySelectedPaths.Add(item.FullPath);
                else _gallerySelectedPaths.Remove(item.FullPath);
                chk.IsChecked = next;
                UpdateCardHighlight(item.FullPath);
                UpdateGalleryStatsAndButtons();
            }
            else
            {
                SelectGalleryPreview(item.FullPath);
            }
        };

        _galleryCardBorders[item.FullPath] = card;
        return card;
    }

    private async Task LoadCardThumbAsync(
        string cacheKey,
        string imagePath,
        System.Windows.Controls.Image targetImg,
        UIElement placeholder,
        CancellationToken ct)
    {
        bool acquired = false;
        try
        {
            await _thumbSem.WaitAsync(ct);
            acquired = true;
            if (ct.IsCancellationRequested) return;

            var bmp = await Task.Run(() => LoadGalleryThumbNoLock(imagePath, 190), ct);
            if (bmp is null) return;

            PutGalleryThumbCache(cacheKey, bmp);
            if (ct.IsCancellationRequested) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                targetImg.Source = bmp;
                targetImg.Visibility = Visibility.Visible;
                placeholder.Visibility = Visibility.Collapsed;
            });
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (acquired) _thumbSem.Release();
        }
    }

    private async Task LoadVideoCardThumbAsync(
        string videoPath,
        System.Windows.Controls.Image targetImg,
        UIElement placeholder,
        CancellationToken ct)
    {
        string cachePath = GetVideoThumbCachePath(videoPath);

        bool diskCached = false;
        try
        {
            var fi = new FileInfo(cachePath);
            diskCached = fi.Exists && fi.Length > 0;
        }
        catch { }

        if (diskCached)
        {
            await LoadCardThumbAsync(videoPath, cachePath, targetImg, placeholder, ct);
            return;
        }

        if (_storyClient is null) return;

        bool acquired = false;
        try
        {
            await _videoThumbSem.WaitAsync(ct);
            acquired = true;
            if (ct.IsCancellationRequested || !File.Exists(videoPath)) return;

            bool existsNow = false;
            try
            {
                var fi = new FileInfo(cachePath);
                existsNow = fi.Exists && fi.Length > 0;
            }
            catch { }

            if (!existsNow)
            {
                Directory.CreateDirectory(VideoThumbCacheDir);
                await _storyClient.ExtractVideoFrameAsync(videoPath, cachePath, "first");
            }

            if (ct.IsCancellationRequested || !File.Exists(cachePath)) return;

            var bmp = await Task.Run(() => LoadGalleryThumbNoLock(cachePath, 190), ct);
            if (bmp is null) return;

            PutGalleryThumbCache(videoPath, bmp);
            if (ct.IsCancellationRequested) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                targetImg.Source = bmp;
                targetImg.Visibility = Visibility.Visible;
                placeholder.Visibility = Visibility.Collapsed;
            });
        }
        catch (OperationCanceledException) { }
        catch { /* Keep 🎬 fallback silently if extraction fails */ }
        finally
        {
            if (acquired) _videoThumbSem.Release();
        }
    }

    private void UpdateCardHighlight(string path)
    {
        if (!_galleryCardBorders.TryGetValue(path, out var card)) return;
        bool isCurrent = string.Equals(_galleryCurrentPath, path, StringComparison.OrdinalIgnoreCase);
        bool isChecked = _gallerySelectedPaths.Contains(path);
        bool isFav = GalleryFavoritesStore.IsFavorite(path);
        card.Background = BrushFromHex(isCurrent ? "#1D2840" : "#151A25");
        card.BorderBrush = BrushFromHex(isCurrent ? "#5B8CFF" : isChecked ? "#E07A5F" : isFav ? "#B8942E" : "#262F42");
        card.BorderThickness = new Thickness(isCurrent ? 1.5 : 1);
    }

    private void UpdateCardFavVisual(string path)
    {
        bool isFav = GalleryFavoritesStore.IsFavorite(path);
        if (_galleryCardFavBtns.TryGetValue(path, out var btn))
        {
            btn.Content = isFav ? "⭐" : "☆";
            btn.Foreground = BrushFromHex(isFav ? "#F5CB5C" : "#8C96AD");
            btn.BorderBrush = BrushFromHex(isFav ? "#B8942E" : "#32405C");
            btn.ToolTip = isFav ? "Убрать из избранного (F)" : "В избранное — защитить от чистки (F)";
        }
        UpdateCardHighlight(path);
    }

    private void UpdatePreviewFavButton()
    {
        if (GalleryToggleFavBtn is null) return;
        if (string.IsNullOrEmpty(_galleryCurrentPath))
        {
            GalleryToggleFavBtn.Visibility = Visibility.Collapsed;
            return;
        }
        bool isFav = GalleryFavoritesStore.IsFavorite(_galleryCurrentPath);
        GalleryToggleFavBtn.Visibility = Visibility.Visible;
        GalleryToggleFavBtn.Content = isFav ? "★ В избранном" : "☆ В избранное";
        if (isFav)
        {
            GalleryToggleFavBtn.Foreground = BrushFromHex("#F5CB5C");
            GalleryToggleFavBtn.BorderBrush = BrushFromHex("#B8942E");
        }
        else
        {
            GalleryToggleFavBtn.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            GalleryToggleFavBtn.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        }
    }

    private void ToggleGalleryFavorite(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        bool nowFav = GalleryFavoritesStore.Toggle(path);
        UpdateCardFavVisual(path);
        if (string.Equals(_galleryCurrentPath, path, StringComparison.OrdinalIgnoreCase))
            UpdatePreviewFavButton();
        UpdateGalleryStatsAndButtons();

        string name = Path.GetFileName(path);
        ShowToast(
            nowFav ? $"⭐ Добавлено в избранное: {name}" : $"☆ Убрано из избранного: {name}",
            nowFav ? "success" : "info");

        if (_galleryFilter == "fav" && !nowFav)
        {
            string? nextPath = _galleryCurrentPath;
            if (string.Equals(_galleryCurrentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                int curIdx = _galleryFiltered.FindIndex(x => x.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
                nextPath = curIdx >= 0 && curIdx + 1 < _galleryFiltered.Count
                    ? _galleryFiltered[curIdx + 1].FullPath
                    : curIdx > 0 ? _galleryFiltered[curIdx - 1].FullPath : null;
            }
            ApplyGalleryFilterAndRender(nextPath);
        }
    }

    private void SelectGalleryPreview(string path)
    {
        string? old = _galleryCurrentPath;
        _galleryCurrentPath = path;
        if (!string.IsNullOrEmpty(old)) UpdateCardHighlight(old);
        UpdateCardHighlight(path);

        var item = _galleryAllItems.FirstOrDefault(x => x.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (item is null || !File.Exists(path))
        {
            ClearGalleryPreview();
            return;
        }

        StopGalleryVideoPlayback();
        GalleryPreviewPlaceholder.Visibility = Visibility.Collapsed;
        GalleryDeleteCurrentBtn.Visibility = Visibility.Visible;
        GalleryOpenExternalBtn.Visibility = Visibility.Visible;
        GalleryRevealBtn.Visibility = Visibility.Visible;
        UpdatePreviewFavButton();
        UpdateGalleryMetadataUi(path);

        if (item.Kind == "video")
        {
            GalleryPreviewImage.Source = null;
            GalleryPreviewImage.Visibility = Visibility.Collapsed;
            GalleryCopyImageBtn.Visibility = Visibility.Collapsed;
            GallerySendToVideoBtn.Visibility = Visibility.Collapsed;
            GallerySendToUpscaleBtn.Visibility = Visibility.Collapsed;
            GallerySendToPhotoRefBtn.Visibility = Visibility.Collapsed;
            if (GalleryLightboxOverlay?.Visibility == Visibility.Visible)
                CloseGalleryLightbox();

            GalleryVideoPlayer.Visibility = Visibility.Visible;
            GalleryPlayPauseBtn.Visibility = Visibility.Visible;
            try
            {
                GalleryVideoPlayer.Source = new Uri(path);
                GalleryVideoPlayer.Play();
                _galleryVideoPlaying = true;
                GalleryPlayPauseBtn.Content = "⏸ Пауза";
            }
            catch
            {
                GalleryVideoPlayer.Visibility = Visibility.Collapsed;
                GalleryPreviewPlaceholder.Text = "Не удалось проиграть видео встроенным плеером — нажми «Открыть ↗»";
                GalleryPreviewPlaceholder.Visibility = Visibility.Visible;
            }
            GalleryMetaTitleText.Text = item.FileName;
            GalleryMetaSubText.Text = $"Видео • {FormatSizeShort(item.LengthBytes)} • {item.LastWriteTime:dd.MM.yyyy HH:mm}\nПапка: {item.FolderTag}";
        }
        else
        {
            GalleryVideoPlayer.Visibility = Visibility.Collapsed;
            GalleryPlayPauseBtn.Visibility = Visibility.Collapsed;
            GalleryCopyImageBtn.Visibility = Visibility.Visible;
            GallerySendToVideoBtn.Visibility = Visibility.Visible;
            GallerySendToUpscaleBtn.Visibility = Visibility.Visible;
            GallerySendToPhotoRefBtn.Visibility = Visibility.Visible;

            var bmp = LoadGalleryThumbNoLock(path, 1024);
            GalleryPreviewImage.Source = bmp;
            GalleryPreviewImage.Visibility = bmp is not null ? Visibility.Visible : Visibility.Collapsed;
            var origDims = ReadImageDimensionsFast(path);
            string dims = origDims is { Width: > 0, Height: > 0 }
                ? $"{origDims.Value.Width}×{origDims.Value.Height} • "
                : (bmp is not null ? $"{bmp.PixelWidth}×{bmp.PixelHeight} • " : "");
            GalleryMetaTitleText.Text = item.FileName;
            GalleryMetaSubText.Text = $"Фото • {dims}{FormatSizeShort(item.LengthBytes)} • {item.LastWriteTime:dd.MM.yyyy HH:mm}\nПапка: {item.FolderTag}";

            if (GalleryLightboxOverlay?.Visibility == Visibility.Visible)
            {
                var fullBmp = LoadGalleryThumbNoLock(path, 2048) ?? bmp;
                if (fullBmp is not null) OpenGalleryLightbox(path, fullBmp);
                else CloseGalleryLightbox();
            }
        }
    }

    private void ClearGalleryPreview()
    {
        StopGalleryVideoPlayback();
        CloseGalleryLightbox();
        _galleryCurrentPath = null;
        UpdateGalleryMetadataUi(null);
        GalleryPreviewImage.Source = null;
        GalleryPreviewImage.Visibility = Visibility.Collapsed;
        GalleryVideoPlayer.Visibility = Visibility.Collapsed;
        GalleryPreviewPlaceholder.Text = "Выбери фото или видео слева\nдля просмотра и быстрой чистки";
        GalleryPreviewPlaceholder.Visibility = Visibility.Visible;
        GalleryMetaTitleText.Text = "Файл не выбран";
        GalleryMetaSubText.Text = "—";
        GalleryDeleteCurrentBtn.Visibility = Visibility.Collapsed;
        GalleryPlayPauseBtn.Visibility = Visibility.Collapsed;
        GalleryToggleFavBtn.Visibility = Visibility.Collapsed;
        GalleryCopyImageBtn.Visibility = Visibility.Collapsed;
        GalleryOpenExternalBtn.Visibility = Visibility.Collapsed;
        GalleryRevealBtn.Visibility = Visibility.Collapsed;
        GallerySendToVideoBtn.Visibility = Visibility.Collapsed;
        GallerySendToUpscaleBtn.Visibility = Visibility.Collapsed;
        GallerySendToPhotoRefBtn.Visibility = Visibility.Collapsed;
    }

    private void UpdateGalleryMetadataUi(string? path)
    {
        _galleryCurrentMeta = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { _galleryCurrentMeta = GenerationMetadataService.TryRead(path); }
            catch { _galleryCurrentMeta = null; }
        }

        if (_galleryCurrentMeta is { } meta && !string.IsNullOrWhiteSpace(meta.Prompt))
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(meta.Engine)) parts.Add($"Модель: {meta.Engine}");
            if (!string.IsNullOrWhiteSpace(meta.Size)) parts.Add(meta.Size);
            if (meta.DurationSec > 0) parts.Add($"{meta.DurationSec}с");
            if (meta.Seed >= 0) parts.Add($"Seed: {meta.Seed}");
            if (!string.IsNullOrWhiteSpace(meta.LoraHigh)) parts.Add($"LoRA: {Path.GetFileNameWithoutExtension(meta.LoraHigh)}");

            string header = parts.Count > 0 ? string.Join(" • ", parts) : "Метаданные генерации";
            string flatPrompt = meta.Prompt.Replace("\r\n", " ").Replace('\n', ' ').Trim();
            if (flatPrompt.Length > 220) flatPrompt = flatPrompt[..220] + "…";

            if (GalleryMetaPromptText is not null)
            {
                GalleryMetaPromptText.Text = $"{header}\n{flatPrompt}";
                GalleryMetaPromptText.ToolTip = meta.Prompt;
            }
            if (GalleryMetaPromptBorder is not null)
            {
                GalleryMetaPromptBorder.ToolTip = meta.Prompt;
                GalleryMetaPromptBorder.Visibility = Visibility.Visible;
            }
            if (GalleryRestoreStudioBtn is not null)
                GalleryRestoreStudioBtn.Visibility = Visibility.Visible;
        }
        else
        {
            if (GalleryMetaPromptBorder is not null)
                GalleryMetaPromptBorder.Visibility = Visibility.Collapsed;
            if (GalleryMetaPromptText is not null)
            {
                GalleryMetaPromptText.Text = "";
                GalleryMetaPromptText.ToolTip = null;
            }
            if (GalleryRestoreStudioBtn is not null)
                GalleryRestoreStudioBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateGalleryStatsAndButtons()
    {
        double filteredMb = _galleryFiltered.Sum(x => x.SizeMB);
        int tempCount = _galleryAllItems.Count(x => x.IsTemp);
        double tempMb = _galleryAllItems.Where(x => x.IsTemp).Sum(x => x.SizeMB);
        int favCount = _galleryAllItems.Count(x => GalleryFavoritesStore.IsFavorite(x.FullPath));

        GalleryStatsText.Text = $"Показано: {_galleryFiltered.Count} ({filteredMb:F1} МБ) • ★ Избранное: {favCount} • Временные/кэш: {tempCount} ({tempMb:F1} МБ)";
        if (GalleryFilterFavBtn is not null)
        {
            string favLabel = favCount > 0 ? $"Избранное ({favCount})" : "Избранное";
            if (GalleryFilterFavBtn.Content is System.Windows.Controls.StackPanel sp &&
                sp.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault() is { } tb)
                tb.Text = favLabel;
            else
                GalleryFilterFavBtn.Content = favLabel;
        }

        GalleryCleanTempBtn.Content = tempCount > 0 ? $"Очистить временные ({tempMb:F0} МБ)" : "Временные чисты";
        GalleryCleanTempBtn.IsEnabled = tempCount > 0;

        int selCount = _gallerySelectedPaths.Count;
        double selMb = _galleryAllItems.Where(x => _gallerySelectedPaths.Contains(x.FullPath)).Sum(x => x.SizeMB);
        GalleryDeleteSelectedBtn.Content = selCount > 0 ? $"В корзину ({selCount} • {selMb:F1} МБ)" : "В корзину (0)";
        GalleryDeleteSelectedBtn.IsEnabled = selCount > 0;
    }

    private void DeleteGalleryItems(List<string> paths, bool askConfirm)
    {
        if (paths.Count == 0) return;

        if (askConfirm)
        {
            double mb = _galleryAllItems.Where(x => paths.Contains(x.FullPath, StringComparer.OrdinalIgnoreCase)).Sum(x => x.SizeMB);
            var confirm = HubDialogWindow.Show(
                $"Отправить выбранные файлы ({paths.Count} шт., {mb:F1} МБ) в Корзину Windows?\n\nПри необходимости их можно будет восстановить из Корзины.",
                "Очистка Галереи",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        // Determine next neighbor to keep smooth flow when deleting current file
        string? nextPath = null;
        if (!string.IsNullOrEmpty(_galleryCurrentPath) && paths.Contains(_galleryCurrentPath, StringComparer.OrdinalIgnoreCase))
        {
            int curIdx = _galleryFiltered.FindIndex(x => x.FullPath.Equals(_galleryCurrentPath, StringComparison.OrdinalIgnoreCase));
            var removeSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int i = curIdx + 1; i < _galleryFiltered.Count; i++)
            {
                if (!removeSet.Contains(_galleryFiltered[i].FullPath)) { nextPath = _galleryFiltered[i].FullPath; break; }
            }
            if (nextPath is null)
            {
                for (int i = curIdx - 1; i >= 0; i--)
                {
                    if (!removeSet.Contains(_galleryFiltered[i].FullPath)) { nextPath = _galleryFiltered[i].FullPath; break; }
                }
            }
            StopGalleryVideoPlayback();
            CloseGalleryLightbox();
            GalleryPreviewImage.Source = null;
        }

        if (!SendFilesToRecycleBin(paths, out string? err))
        {
            HubDialogWindow.Show(err ?? "Ошибка удаления.", "Галерея", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var delSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _galleryAllItems.RemoveAll(x => delSet.Contains(x.FullPath));
        _gallerySelectedPaths.RemoveWhere(p => delSet.Contains(p));
        foreach (string p in paths)
        {
            GalleryFavoritesStore.Remove(p);
            GenerationMetadataService.DeleteSidecarIfExists(p);
            _thumbCache.TryRemove(p, out _);
            TryDeleteVideoThumbCacheFile(p);
        }

        string msg = paths.Count == 1
            ? $"Отправлен в Корзину: {Path.GetFileName(paths[0])}"
            : $"Отправлено в Корзину файлов: {paths.Count}";
        StatusFooterText.Text = msg;
        ShowToast($"🗑 {msg}", "info");

        ApplyGalleryFilterAndRender(nextPath ?? _galleryCurrentPath);
    }

    private static (int Width, int Height)? ReadImageDimensionsFast(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (dec.Frames.Count > 0)
                return (dec.Frames[0].PixelWidth, dec.Frames[0].PixelHeight);
        }
        catch { }
        return null;
    }

    private static BitmapImage? LoadGalleryThumbNoLock(string path, int decodeWidth)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static string FormatSizeShort(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} МБ";
        return $"{Math.Max(1, bytes / 1024.0):F0} КБ";
    }

    // ---- Gallery UI Events ----

    internal void GalleryFilterBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string tag) return;
        _galleryFilter = tag;
        var primary = (Style)FindResource("PrimaryButton");
        var dark = (Style)FindResource("DarkButton");
        GalleryFilterAllBtn.Style = tag == "all" ? primary : dark;
        GalleryFilterPhotoBtn.Style = tag == "photo" ? primary : dark;
        GalleryFilterVideoBtn.Style = tag == "video" ? primary : dark;
        GalleryFilterFavBtn.Style = tag == "fav" ? primary : dark;
        GalleryFilterTempBtn.Style = tag == "temp" ? primary : dark;
        _galleryVisibleLimit = 80;
        ApplyGalleryFilterAndRender();
    }

    internal void GallerySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _galleryVisibleLimit = 80;
        ApplyGalleryFilterAndRender(_galleryCurrentPath);
    }

    internal void GallerySortCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyGalleryFilterAndRender(_galleryCurrentPath);
    }

    internal async void GalleryRefreshBtn_Click(object sender, RoutedEventArgs e) =>
        await RefreshGalleryAsync(preserveSelection: true);

    internal void GalleryLoadMoreBtn_Click(object sender, RoutedEventArgs e)
    {
        _galleryVisibleLimit += 80;
        RenderGalleryGrid();
    }

    internal void GallerySelectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _galleryFiltered)
            _gallerySelectedPaths.Add(item.FullPath);
        foreach (var kv in _galleryCardChecks)
            kv.Value.IsChecked = true;
        foreach (var path in _galleryCardBorders.Keys)
            UpdateCardHighlight(path);
        UpdateGalleryStatsAndButtons();
    }

    internal void GallerySelectTrashBtn_Click(object sender, RoutedEventArgs e)
    {
        _gallerySelectedPaths.Clear();
        foreach (var item in _galleryFiltered)
        {
            if (!GalleryFavoritesStore.IsFavorite(item.FullPath))
                _gallerySelectedPaths.Add(item.FullPath);
        }
        foreach (var kv in _galleryCardChecks)
            kv.Value.IsChecked = _gallerySelectedPaths.Contains(kv.Key);
        foreach (var path in _galleryCardBorders.Keys)
            UpdateCardHighlight(path);
        UpdateGalleryStatsAndButtons();

        int n = _gallerySelectedPaths.Count;
        ShowToast($"Выбрано {n} файлов для чистки (⭐ избранные защищены)", "info");
    }

    internal void GalleryToggleFavBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_galleryCurrentPath)) return;
        ToggleGalleryFavorite(_galleryCurrentPath);
    }

    internal void GalleryCopyImageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_galleryCurrentPath) || !File.Exists(_galleryCurrentPath)) return;
        var bmp = LoadGalleryThumbNoLock(_galleryCurrentPath, 0);
        if (bmp is null)
        {
            ShowToast("Не удалось прочитать фото для копирования", "warn");
            return;
        }
        try
        {
            System.Windows.Clipboard.SetImage(bmp);
            StatusFooterText.Text = $"Фото скопировано в буфер: {Path.GetFileName(_galleryCurrentPath)}";
            ShowToast("Фото скопировано в буфер (можно вставить Ctrl+V)", "success");
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось скопировать фото: {ex.Message}", "warn");
        }
    }

    private void OpenGalleryLightbox(string path, BitmapImage? existingBmp = null)
    {
        if (GalleryLightboxOverlay is null || GalleryLightboxImage is null) return;
        var bmp = existingBmp ?? LoadGalleryThumbNoLock(path, 0);
        if (bmp is null) return;

        GalleryLightboxImage.Source = bmp;
        if (GalleryLightboxCaption is not null)
        {
            var item = _galleryAllItems.FirstOrDefault(x => x.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            string name = item?.FileName ?? Path.GetFileName(path);
            string sizePart = item is not null ? $" • {FormatSizeShort(item.LengthBytes)}" : "";
            GalleryLightboxCaption.Text = $"{name} • {bmp.PixelWidth}×{bmp.PixelHeight}{sizePart}";
        }
        GalleryLightboxOverlay.Visibility = Visibility.Visible;
    }

    private void CloseGalleryLightbox()
    {
        if (GalleryLightboxOverlay is null) return;
        GalleryLightboxOverlay.Visibility = Visibility.Collapsed;
        if (GalleryLightboxImage is not null)
            GalleryLightboxImage.Source = null;
    }

    internal void GalleryPreviewImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(_galleryCurrentPath) || !File.Exists(_galleryCurrentPath)) return;
        e.Handled = true;
        OpenGalleryLightbox(_galleryCurrentPath);
    }

    private void GalleryLightboxCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseGalleryLightbox();
    }

    private void GalleryLightboxBg_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseGalleryLightbox();
    }

    internal void GalleryClearSelBtn_Click(object sender, RoutedEventArgs e)
    {
        _gallerySelectedPaths.Clear();
        foreach (var kv in _galleryCardChecks)
            kv.Value.IsChecked = false;
        foreach (var path in _galleryCardBorders.Keys)
            UpdateCardHighlight(path);
        UpdateGalleryStatsAndButtons();
    }

    internal void GalleryDeleteSelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_gallerySelectedPaths.Count == 0) return;
        DeleteGalleryItems(_gallerySelectedPaths.ToList(), askConfirm: true);
    }

    internal void GalleryCleanTempBtn_Click(object sender, RoutedEventArgs e)
    {
        var temps = _galleryAllItems.Where(x => x.IsTemp).Select(x => x.FullPath).ToList();
        if (temps.Count == 0) return;
        DeleteGalleryItems(temps, askConfirm: true);
    }

    internal void GalleryDeleteCurrentBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_galleryCurrentPath)) return;
        DeleteGalleryItems([_galleryCurrentPath], askConfirm: false);
    }

    internal void GalleryPrevBtn_Click(object sender, RoutedEventArgs e) => StepGallerySelection(-1);
    internal void GalleryNextBtn_Click(object sender, RoutedEventArgs e) => StepGallerySelection(1);

    private void StepGallerySelection(int delta)
    {
        if (_galleryFiltered.Count == 0) return;
        int idx = string.IsNullOrEmpty(_galleryCurrentPath)
            ? 0
            : _galleryFiltered.FindIndex(x => x.FullPath.Equals(_galleryCurrentPath, StringComparison.OrdinalIgnoreCase));
        int next = idx < 0 ? 0 : Math.Clamp(idx + delta, 0, _galleryFiltered.Count - 1);
        SelectGalleryPreview(_galleryFiltered[next].FullPath);
    }

    internal void GalleryGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && GalleryLightboxOverlay?.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CloseGalleryLightbox();
            return;
        }
        if (GallerySearchBox.IsKeyboardFocusWithin) return;
        if (e.Key == Key.Left)
        {
            e.Handled = true;
            StepGallerySelection(-1);
        }
        else if (e.Key == Key.Right)
        {
            e.Handled = true;
            StepGallerySelection(1);
        }
        else if (e.Key == Key.F && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            if (!string.IsNullOrEmpty(_galleryCurrentPath))
            {
                e.Handled = true;
                ToggleGalleryFavorite(_galleryCurrentPath);
            }
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            if (_gallerySelectedPaths.Count > 0)
                DeleteGalleryItems(_gallerySelectedPaths.ToList(), askConfirm: true);
            else if (!string.IsNullOrEmpty(_galleryCurrentPath))
                DeleteGalleryItems([_galleryCurrentPath], askConfirm: false);
        }
    }

    internal void GalleryVideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        try
        {
            GalleryVideoPlayer.Position = TimeSpan.Zero;
            GalleryVideoPlayer.Play();
        }
        catch { }
    }

    internal void GalleryPlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryVideoPlayer.Source is null) return;
        try
        {
            if (_galleryVideoPlaying)
            {
                GalleryVideoPlayer.Pause();
                _galleryVideoPlaying = false;
                GalleryPlayPauseBtn.Content = "▶ Играть";
            }
            else
            {
                GalleryVideoPlayer.Play();
                _galleryVideoPlaying = true;
                GalleryPlayPauseBtn.Content = "⏸ Пауза";
            }
        }
        catch { }
    }

    internal void GalleryOpenExternalBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_galleryCurrentPath) && File.Exists(_galleryCurrentPath))
            AiServiceManager.OpenExternal(_galleryCurrentPath);
    }

    internal void GalleryRevealBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_galleryCurrentPath) || !File.Exists(_galleryCurrentPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_galleryCurrentPath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    internal void GallerySendToVideoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_galleryCurrentPath) || !File.Exists(_galleryCurrentPath)) return;
        _studioVideoStartPath = _galleryCurrentPath;
        StudioVideoStartLabel.Text = $"Старт: {Path.GetFileName(_studioVideoStartPath)}";
        StudioClearVideoStartBtn.Visibility = Visibility.Visible;
        MainTabs.SelectedItem = VideoTab;
        SetVideoSubMode("single");
        StatusFooterText.Text = $"Фото из Галереи подставлено как стартовый кадр (I2V): {Path.GetFileName(_studioVideoStartPath)}";
    }

    internal void GallerySendToUpscaleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_galleryCurrentPath) || !File.Exists(_galleryCurrentPath)) return;
        _studioUpscaleSourcePath = _galleryCurrentPath;
        StudioUpscaleSourceLabel.Text = Path.GetFileName(_studioUpscaleSourcePath);
        MainTabs.SelectedItem = PhotoTab;
        SetPhotoSubMode("upscale");
        StatusFooterText.Text = $"Фото из Галереи отправлено в Апскейл ×4: {Path.GetFileName(_studioUpscaleSourcePath)}";
    }

    internal void GallerySendToPhotoRefBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_galleryCurrentPath) || !File.Exists(_galleryCurrentPath)) return;
        _studioPhotoRefPath = _galleryCurrentPath;
        StudioPhotoRefLabel.Text = $"Референс: {Path.GetFileName(_studioPhotoRefPath)}";
        StudioClearPhotoRefBtn.Visibility = Visibility.Visible;
        string curEngine = (StudioPhotoEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zimage";
        if (curEngine == "zimage") SelectComboByTag(StudioPhotoEngineCombo, "flux3");
        MainTabs.SelectedItem = PhotoTab;
        SetPhotoSubMode("single");
        StatusFooterText.Text = $"Фото из Галереи выбрано как референс для правки: {Path.GetFileName(_studioPhotoRefPath)}";
    }

    internal void GalleryRestoreStudioBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_galleryCurrentMeta is null) return;
        RestoreMetadataToStudio(_galleryCurrentMeta);
    }

    private void RestoreMetadataToStudio(GenerationMetadata meta)
    {
        bool isVideo = string.Equals(meta.Kind, "video", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(meta.Kind) && !string.IsNullOrEmpty(_galleryCurrentPath))
        {
            var curItem = _galleryAllItems.FirstOrDefault(x => x.FullPath.Equals(_galleryCurrentPath, StringComparison.OrdinalIgnoreCase));
            isVideo = curItem?.Kind == "video";
        }

        if (isVideo)
        {
            MainTabs.SelectedItem = VideoTab;
            SetVideoSubMode("single");
            RefreshCustomLoras();

            VideoPromptBox.Text = meta.Prompt ?? "";
            if (!string.IsNullOrWhiteSpace(meta.Engine))
                SelectComboByTag(StudioVideoEngineCombo, meta.Engine);
            if (!string.IsNullOrWhiteSpace(meta.Size))
                SelectComboByTag(StudioVideoSizeCombo, meta.Size);
            if (meta.DurationSec > 0)
                SelectComboByTag(StudioVideoDurCombo, meta.DurationSec.ToString());
            StudioVideoSeedBox.Text = meta.Seed >= 0 ? meta.Seed.ToString() : "-1";

            SelectComboByTag(VideoExtraLoraHighCombo, "");
            if (!string.IsNullOrWhiteSpace(meta.LoraHigh))
                SelectComboByTag(VideoExtraLoraHighCombo, meta.LoraHigh);
            double hStr = meta.LoraHighStrength > 0 ? meta.LoraHighStrength : 0.75;
            VideoExtraLoraHighSlider.Value = Math.Clamp(hStr, VideoExtraLoraHighSlider.Minimum, VideoExtraLoraHighSlider.Maximum);

            SelectComboByTag(VideoExtraLoraLowCombo, "");
            if (!string.IsNullOrWhiteSpace(meta.LoraLow))
                SelectComboByTag(VideoExtraLoraLowCombo, meta.LoraLow);
            double lStr = meta.LoraLowStrength > 0 ? meta.LoraLowStrength : 0.75;
            VideoExtraLoraLowSlider.Value = Math.Clamp(lStr, VideoExtraLoraLowSlider.Minimum, VideoExtraLoraLowSlider.Maximum);

            if (!string.IsNullOrWhiteSpace(meta.RefImagePath) && File.Exists(meta.RefImagePath))
                SetStudioVideoStartImage(meta.RefImagePath);
            else
                StudioClearVideoStartBtn_Click(this, new RoutedEventArgs());

            if (!string.IsNullOrWhiteSpace(meta.EndImagePath) && File.Exists(meta.EndImagePath))
                SetStudioVideoEndImage(meta.EndImagePath);
            else
                StudioClearVideoEndBtn_Click(this, new RoutedEventArgs());

            bool hasVideoAdv = !string.IsNullOrWhiteSpace(meta.LoraHigh) || !string.IsNullOrWhiteSpace(meta.LoraLow) || (!string.IsNullOrWhiteSpace(meta.EndImagePath) && File.Exists(meta.EndImagePath));
            if (hasVideoAdv && VideoAdvancedExpander is not null)
                VideoAdvancedExpander.IsExpanded = true;
        }
        else
        {
            MainTabs.SelectedItem = PhotoTab;
            SetPhotoSubMode("single");
            RefreshCustomLoras();

            PhotoPromptBox.Text = meta.Prompt ?? "";
            if (!string.IsNullOrWhiteSpace(meta.Engine))
                SelectComboByTag(StudioPhotoEngineCombo, meta.Engine);
            if (!string.IsNullOrWhiteSpace(meta.Size))
                SelectComboByTag(StudioPhotoSizeCombo, meta.Size);
            StudioPhotoSeedBox.Text = meta.Seed >= 0 ? meta.Seed.ToString() : "-1";

            string? photoLora = !string.IsNullOrWhiteSpace(meta.LoraHigh) ? meta.LoraHigh : meta.LoraLow;
            if (!string.IsNullOrWhiteSpace(photoLora) && PhotoAdvancedExpander is not null)
                PhotoAdvancedExpander.IsExpanded = true;
            double pStr = !string.IsNullOrWhiteSpace(meta.LoraHigh)
                ? (meta.LoraHighStrength > 0 ? meta.LoraHighStrength : 0.75)
                : (meta.LoraLowStrength > 0 ? meta.LoraLowStrength : 0.75);
            SelectComboByTag(PhotoExtraLoraCombo, "");
            if (!string.IsNullOrWhiteSpace(photoLora))
                SelectComboByTag(PhotoExtraLoraCombo, photoLora);
            PhotoExtraLoraSlider.Value = Math.Clamp(pStr, PhotoExtraLoraSlider.Minimum, PhotoExtraLoraSlider.Maximum);

            if (!string.IsNullOrWhiteSpace(meta.RefImagePath) && File.Exists(meta.RefImagePath))
                SetStudioPhotoRefImage(meta.RefImagePath);
            else
                StudioClearPhotoRefBtn_Click(this, new RoutedEventArgs());
        }

        UpdateStudioMissingBanner();
        StatusFooterText.Text = "Промпт и настройки кадра восстановлены в Студии";
        ShowToast("Промпт и настройки кадра восстановлены в Студии", "success");
    }
}
