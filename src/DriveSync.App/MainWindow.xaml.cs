using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DriveSync.Core;

namespace DriveSync.App;

/// <summary>
/// The main window. It only handles the screen: what the user types and clicks,
/// and showing results. Indexing, searching and summaries come from DriveSync.Core.
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxResultsShown = 500;

    private readonly IndexManager _indexManager = new();

    // Waits a moment after the last key press before searching, so we don't
    // search for "r", "re", "rep"… while the user is still typing.
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };

    // Every search gets a number; results from an older search are ignored
    // if a newer one was started in the meantime.
    private int _searchNumber;
    private int _previewNumber;

    // The "File type" picker: every type in the list, the one chosen, and what the list is filtered by.
    private List<FormatOption> _formats = new() { FormatOption.All };
    private FormatOption _selectedFormat = FormatOption.All;
    private string _formatFilter = "";
    private bool _settingFormatText; // true while the code (not the user) changes FormatBox.Text

    // The "Folder" picker: the folder chosen, folders used recently, and a number per folder search.
    private FolderOption _selectedFolder = FolderOption.All;
    private readonly List<FolderOption> _recentFolders = new();
    private int _folderSearchNumber;
    private bool _settingFolderText;

    public MainWindow()
    {
        InitializeComponent();

        // Show the well-known types straight away; types found on this PC are added once the index is loaded.
        LoadFormats(new Dictionary<string, int>());

        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await RunSearchAsync();
        };

        // These events come from background threads, so hop back to the UI thread.
        _indexManager.StatusChanged += message => Dispatcher.BeginInvoke(() => StatusText.Text = message);
        _indexManager.IndexReplaced += () => Dispatcher.BeginInvoke(async () =>
        {
            await RefreshFormatsAsync();
            if (SearchBox.Text.Trim().Length > 0 || _selectedFormat.Extension != null || _selectedFolder.Path != null)
                await RunSearchAsync();
        });

        Loaded += async (_, _) =>
        {
            // The file type is asked first, so the picker gets the keyboard and opens its list.
            FormatBox.Focus();
            await _indexManager.StartAsync(FileScanner.GetAllDriveRoots());
        };
        Closed += (_, _) => _indexManager.Dispose();
    }

    // ------------------------------------------------------------------
    // Searching
    // ------------------------------------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private async Task RunSearchAsync()
    {
        int thisSearch = ++_searchNumber;
        string query = SearchBox.Text.Trim();
        string? extension = _selectedFormat.Extension;
        string? folder = _selectedFolder.Path;

        // With a file type or folder chosen, an empty search box lists every file that fits.
        if (query.Length == 0 && extension == null && folder == null)
        {
            ResultsList.ItemsSource = null;
            SearchTimeText.Text = "";
            return;
        }

        // Search on a background thread so the window never freezes.
        SearchResults results = await Task.Run(() => _indexManager.Index.Search(query, MaxResultsShown, extension, folder));
        if (thisSearch != _searchNumber)
            return; // the user typed something else meanwhile

        ResultsList.ItemsSource = results.Items.Select(file => new ResultRow(file)).ToList();

        string type = extension != null ? $" {_selectedFormat.Label}" : "";
        string where = folder != null ? $" in {_selectedFolder.Label}" : "";
        string shown = results.TotalMatches > results.Items.Count ? $" (showing the best {results.Items.Count})" : "";
        SearchTimeText.Text = $"{results.TotalMatches:N0}{type} matches{where}{shown} in {results.Elapsed.TotalMilliseconds:0.0} ms";
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Down arrow or Enter jumps from the search box into the results.
        if ((e.Key == Key.Down || e.Key == Key.Enter) && ResultsList.Items.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
            (ResultsList.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem)?.Focus();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------
    // File type picker
    // ------------------------------------------------------------------

    private async Task RefreshFormatsAsync()
    {
        Dictionary<string, int> counts = await Task.Run(_indexManager.Index.CountExtensions);
        LoadFormats(counts);
    }

    private void LoadFormats(IReadOnlyDictionary<string, int> countsInIndex)
    {
        _formats = new List<FormatOption> { FormatOption.All };
        _formats.AddRange(FileFormats.Build(countsInIndex).Select(format => new FormatOption(format)));

        // Keep the chosen type, but take its new file count.
        if (_selectedFormat.Extension != null)
            _selectedFormat = _formats.FirstOrDefault(f => f.Extension == _selectedFormat.Extension) ?? _selectedFormat;

        FilterFormats(_formatFilter);
    }

    /// <summary>
    /// Shows the types matching what was typed: "py" lists .py first, then types containing "py"
    /// (like .ipynb), then types whose description matches (typing "image" finds .png, .jpg…).
    /// </summary>
    private void FilterFormats(string typed)
    {
        _formatFilter = typed;
        string text = FileFormats.Normalize(typed);

        List<FormatOption> shown;
        if (text.Length == 0)
        {
            shown = _formats;
        }
        else
        {
            shown = _formats
                .Where(f => f.Extension != null)
                .Select(f => (Format: f, Rank:
                    f.Extension == text ? 0 :
                    f.Extension!.StartsWith(text, StringComparison.Ordinal) ? 1 :
                    f.Extension.Contains(text, StringComparison.Ordinal) ? 2 :
                    f.Description.Contains(text, StringComparison.OrdinalIgnoreCase) ? 3 : -1))
                .Where(m => m.Rank >= 0)
                .OrderBy(m => m.Rank) // a stable sort, so types with the most files stay first within each rank
                .Select(m => m.Format)
                .ToList();
        }

        FormatList.ItemsSource = shown;
        FormatList.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoFormatsText.Visibility = shown.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        // Highlight the best match, or the current choice when showing the whole list.
        FormatList.SelectedItem = text.Length == 0 ? _selectedFormat : shown.FirstOrDefault();
        if (FormatList.SelectedItem != null)
            FormatList.ScrollIntoView(FormatList.SelectedItem);
    }

    private void OpenFormatList()
    {
        FilterFormats("");
        FormatPopup.IsOpen = true;
    }

    private void ChooseFormat(FormatOption option)
    {
        SetSelectedFormat(option);
        FormatPopup.IsOpen = false;
        SearchBox.Focus(); // step 2: type the name
        _ = RunSearchAsync();
    }

    private void SetSelectedFormat(FormatOption option)
    {
        _selectedFormat = option;
        ShowSelectedFormatText();
        ClearFormatButton.Visibility = option.Extension != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateSearchPlaceholder();
    }

    /// <summary>"Search all files on this PC…", or for example "Search .pdf files in Documents…".</summary>
    private void UpdateSearchPlaceholder()
    {
        string type = _selectedFormat.Extension != null ? $"{_selectedFormat.Label} files" : "all files";
        string where = _selectedFolder.Path != null ? $"in {_selectedFolder.Label}" : "on this PC";
        bool narrowed = _selectedFormat.Extension != null || _selectedFolder.Path != null;
        SearchPlaceholder.Text = $"Search {type} {where}…" + (narrowed ? " (leave empty to list them all)" : "");
    }

    private void ShowSelectedFormatText()
    {
        _settingFormatText = true;
        FormatBox.Text = _selectedFormat.Extension == null ? "" : _selectedFormat.Label;
        _settingFormatText = false;
    }

    private void FormatBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FormatPlaceholder.Visibility = FormatBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_settingFormatText)
            return;

        FilterFormats(FormatBox.Text);
        FormatPopup.IsOpen = true;
    }

    private void FormatBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Select the current text, so typing replaces it and searches the whole list.
        FormatBox.SelectAll();
        OpenFormatList();
    }

    private void FormatBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Without this, the click would undo the "select all" from GotKeyboardFocus.
        if (!FormatBox.IsKeyboardFocusWithin)
        {
            FormatBox.Focus();
            e.Handled = true;
        }
        else if (!FormatPopup.IsOpen)
        {
            OpenFormatList();
        }
    }

    private void FormatBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                if (!FormatPopup.IsOpen)
                    OpenFormatList();
                else
                    MoveHighlight(FormatList, e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;

            case Key.Enter:
                if (FormatPopup.IsOpen && FormatList.SelectedItem is FormatOption option)
                    ChooseFormat(option);
                else if (!FormatPopup.IsOpen)
                    SearchBox.Focus();
                e.Handled = true;
                break;

            case Key.Escape:
                if (FormatPopup.IsOpen)
                {
                    FormatPopup.IsOpen = false;
                    ShowSelectedFormatText();
                    FormatBox.SelectAll();
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>Moves the highlighted row of a picker list up or down, while the keyboard stays in the box.</summary>
    private static void MoveHighlight(ListBox list, int step)
    {
        if (list.Items.Count == 0)
            return;
        list.SelectedIndex = Math.Clamp(list.SelectedIndex + step, 0, list.Items.Count - 1);
        list.ScrollIntoView(list.SelectedItem);
    }

    private void FormatBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        FormatPopup.IsOpen = false;

        // Leaving the box (for example with Tab) keeps a type typed in full, like "pdf";
        // an empty box means all types, and anything else goes back to the previous choice.
        string text = FileFormats.Normalize(FormatBox.Text);
        FormatOption chosen = text.Length == 0
            ? FormatOption.All
            : _formats.FirstOrDefault(f => f.Extension == text) ?? _selectedFormat;

        if (chosen != _selectedFormat)
        {
            SetSelectedFormat(chosen);
            _ = RunSearchAsync();
        }
        else
        {
            ShowSelectedFormatText();
        }
    }

    private void FormatList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FormatList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: FormatOption option })
            ChooseFormat(option);
    }

    private void ClearFormatButton_Click(object sender, RoutedEventArgs e) => ChooseFormat(FormatOption.All);

    private void ShowFormatsButton_Click(object sender, RoutedEventArgs e)
    {
        if (FormatBox.IsKeyboardFocusWithin)
            OpenFormatList();
        else
            FormatBox.Focus(); // opens the list too
    }

    // ------------------------------------------------------------------
    // Folder picker
    // ------------------------------------------------------------------

    /// <summary>
    /// Shows the folders matching what was typed. Folder names are searched in the in-memory
    /// index (on a background thread), so this is as quick as the file search and never touches the disk.
    /// </summary>
    private async Task UpdateFolderListAsync(string typed)
    {
        int thisSearch = ++_folderSearchNumber;
        string query = typed.Trim();

        List<FolderOption> shown;
        if (query.Length == 0)
        {
            shown = SuggestedFolders();
        }
        else
        {
            List<FolderMatch> found = await Task.Run(() => _indexManager.Index.SearchFolders(query));
            if (thisSearch != _folderSearchNumber)
                return; // the user typed something else meanwhile
            shown = found.Select(match => new FolderOption(match)).ToList();
        }

        FolderList.ItemsSource = shown;
        FolderList.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoFoldersText.Visibility = shown.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        // Highlight the best match, or the current choice when showing the suggestions.
        FolderList.SelectedItem = query.Length == 0
            ? shown.FirstOrDefault(f => SamePath(f.Path, _selectedFolder.Path))
            : shown.FirstOrDefault();
        if (FolderList.SelectedItem != null)
            FolderList.ScrollIntoView(FolderList.SelectedItem);
    }

    /// <summary>What the list shows before anything is typed: all drives, recent folders, your main folders, each drive.</summary>
    private List<FolderOption> SuggestedFolders()
    {
        var suggestions = new List<FolderOption> { FolderOption.All };
        suggestions.AddRange(_recentFolders);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] mainFolders =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        suggestions.AddRange(mainFolders.Where(Directory.Exists).Select(path => FolderOption.FromPath(path)));
        suggestions.AddRange(FileScanner.GetAllDriveRoots().Select(root => FolderOption.FromPath(root)));

        // The same folder can appear twice (recent and a main folder); keep the first.
        return suggestions.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool SamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void OpenFolderList()
    {
        _ = UpdateFolderListAsync("");
        FolderPopup.IsOpen = true;
    }

    private void ChooseFolder(FolderOption option)
    {
        SetSelectedFolder(option);
        FolderPopup.IsOpen = false;
        SearchBox.Focus(); // step 3: type the name
        _ = RunSearchAsync();
    }

    private void SetSelectedFolder(FolderOption option)
    {
        _selectedFolder = option;
        if (option.Path != null)
        {
            _recentFolders.RemoveAll(f => SamePath(f.Path, option.Path));
            _recentFolders.Insert(0, option);
            if (_recentFolders.Count > 5)
                _recentFolders.RemoveAt(5);
        }

        ShowSelectedFolderText();
        ClearFolderButton.Visibility = option.Path != null ? Visibility.Visible : Visibility.Collapsed;
        FolderBorder.ToolTip = option.Path;
        UpdateSearchPlaceholder();
    }

    private void ShowSelectedFolderText()
    {
        _settingFolderText = true;
        FolderBox.Text = _selectedFolder.Path ?? "";
        FolderBox.CaretIndex = FolderBox.Text.Length; // show the end of a long path: the folder's own name
        _settingFolderText = false;
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FolderPlaceholder.Visibility = FolderBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_settingFolderText)
            return;

        _ = UpdateFolderListAsync(FolderBox.Text);
        FolderPopup.IsOpen = true;
    }

    private void FolderBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        FolderBox.SelectAll();
        OpenFolderList();
    }

    private void FolderBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!FolderBox.IsKeyboardFocusWithin)
        {
            FolderBox.Focus();
            e.Handled = true;
        }
        else if (!FolderPopup.IsOpen)
        {
            OpenFolderList();
        }
    }

    private void FolderBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                if (!FolderPopup.IsOpen)
                    OpenFolderList();
                else
                    MoveHighlight(FolderList, e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;

            case Key.Enter:
                if (FolderPopup.IsOpen && FolderList.SelectedItem is FolderOption option)
                    ChooseFolder(option);
                else if (TypedFolder() is { } typed)
                    ChooseFolder(typed);
                else if (!FolderPopup.IsOpen)
                    SearchBox.Focus();
                e.Handled = true;
                break;

            case Key.Escape:
                if (FolderPopup.IsOpen)
                {
                    FolderPopup.IsOpen = false;
                    ShowSelectedFolderText();
                    FolderBox.SelectAll();
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>A full folder path typed or pasted into the box, if that folder exists.</summary>
    private FolderOption? TypedFolder()
    {
        string text = FolderBox.Text.Trim().Trim('"');
        return text.Length > 0 && Path.IsPathFullyQualified(text) && Directory.Exists(text)
            ? FolderOption.FromPath(text)
            : null;
    }

    private void FolderBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        FolderPopup.IsOpen = false;

        // Leaving the box keeps a full path typed or pasted into it; an empty box means all drives;
        // anything else goes back to the previous choice.
        FolderOption chosen = FolderBox.Text.Trim().Length == 0
            ? FolderOption.All
            : TypedFolder() ?? _selectedFolder;

        if (!SamePath(chosen.Path, _selectedFolder.Path))
        {
            SetSelectedFolder(chosen);
            _ = RunSearchAsync();
        }
        else
        {
            ShowSelectedFolderText();
        }
    }

    private void FolderList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FolderList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: FolderOption option })
            ChooseFolder(option);
    }

    private void ClearFolderButton_Click(object sender, RoutedEventArgs e) => ChooseFolder(FolderOption.All);

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder to search in" };
        if (_selectedFolder.Path != null)
            dialog.InitialDirectory = _selectedFolder.Path;

        if (dialog.ShowDialog(this) == true)
            ChooseFolder(FolderOption.FromPath(dialog.FolderName));
    }

    private void SearchInFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
            ChooseFolder(FolderOption.FromPath(row.Folder));
    }

    // ------------------------------------------------------------------
    // Summary and preview of the selected file
    // ------------------------------------------------------------------

    private ResultRow? SelectedRow => ResultsList.SelectedItem as ResultRow;

    private async void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int thisPreview = ++_previewNumber;
        ResultRow? row = SelectedRow;
        if (row == null)
        {
            PreviewPanel.Visibility = Visibility.Collapsed;
            EmptyPreviewText.Visibility = Visibility.Visible;
            return;
        }

        FileSummary summary;
        try
        {
            summary = await Task.Run(() => SummaryService.Summarize(row.FullPath));
        }
        catch (Exception ex)
        {
            summary = new FileSummary(row.Name, row.FullPath, "", Array.Empty<string>(), "Could not read this file: " + ex.Message, "", false);
        }

        if (thisPreview != _previewNumber)
            return; // another file was selected meanwhile

        ShowSummary(summary);
    }

    private void ShowSummary(FileSummary summary)
    {
        EmptyPreviewText.Visibility = Visibility.Collapsed;
        PreviewPanel.Visibility = Visibility.Visible;

        FileNameText.Text = summary.Name;
        FileDetailsText.Text = summary.Details;
        FilePathText.Text = summary.FullPath;

        KeywordsList.ItemsSource = summary.Keywords;
        KeywordsHeading.Visibility = summary.Keywords.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        SummaryText.Text = summary.Summary;

        PreviewTextBox.Text = summary.Preview;
        PreviewTextBox.Visibility = summary.Preview.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Source = summary.IsImage ? LoadImage(summary.FullPath) : null;
        PreviewHeading.Visibility = summary.Preview.Length > 0 || PreviewImage.Source != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static BitmapImage? LoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 600;                   // a small copy is enough and loads fast
            image.CacheOption = BitmapCacheOption.OnLoad;   // don't keep the file locked
            image.EndInit();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Opening files
    // ------------------------------------------------------------------

    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void ResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OpenSelected();
    }

    private void OpenSelected()
    {
        if (SelectedRow is not { } row)
            return;
        try
        {
            // UseShellExecute opens the file with its normal program, like double-clicking it in Explorer.
            Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the file:\n" + ex.Message, "DriveSync");
        }
    }

    private void ShowInFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
            Process.Start("explorer.exe", $"/select,\"{row.FullPath}\"");
    }
}
