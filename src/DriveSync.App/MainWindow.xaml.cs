using System.Diagnostics;
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
            if (SearchBox.Text.Trim().Length > 0 || _selectedFormat.Extension != null)
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

        // With a file type chosen, an empty search box lists every file of that type.
        if (query.Length == 0 && extension == null)
        {
            ResultsList.ItemsSource = null;
            SearchTimeText.Text = "";
            return;
        }

        // Search on a background thread so the window never freezes.
        SearchResults results = await Task.Run(() => _indexManager.Index.Search(query, MaxResultsShown, extension));
        if (thisSearch != _searchNumber)
            return; // the user typed something else meanwhile

        ResultsList.ItemsSource = results.Items.Select(file => new ResultRow(file)).ToList();

        string type = extension != null ? $" {_selectedFormat.Label}" : "";
        string shown = results.TotalMatches > results.Items.Count ? $" (showing the best {results.Items.Count})" : "";
        SearchTimeText.Text = $"{results.TotalMatches:N0}{type} matches{shown} in {results.Elapsed.TotalMilliseconds:0.0} ms";
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
        SearchPlaceholder.Text = option.Extension == null
            ? "Search all files on this PC…"
            : $"Search {option.Label} files… (leave empty to list them all)";
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
                    MoveFormatHighlight(e.Key == Key.Down ? 1 : -1);
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

    private void MoveFormatHighlight(int step)
    {
        if (FormatList.Items.Count == 0)
            return;
        FormatList.SelectedIndex = Math.Clamp(FormatList.SelectedIndex + step, 0, FormatList.Items.Count - 1);
        FormatList.ScrollIntoView(FormatList.SelectedItem);
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
