# DriveSync, module 1: fast file name search with summary and preview

DriveSync indexes every file name on all your drives, lets you search them instantly,
and shows a short keyword summary and a preview of the file you select.

## 1. Install once

1. **.NET 8 SDK**: download "SDK 8.0 x64 for Windows" from <https://dotnet.microsoft.com/download/dotnet/8.0> and install it.
   Check it worked: open a new Command Prompt and run `dotnet --version` (it should print 8.0.something).
2. **Visual Studio Code**: <https://code.visualstudio.com>.
3. In VS Code, open the Extensions panel (Ctrl+Shift+X) and install **C# Dev Kit** (by Microsoft).
   VS Code will also suggest it when you open the folder.

## 2. Open, build and run

1. Copy the whole `DriveSync` folder to your PC, for example to `C:\Projects\DriveSync`.
2. In VS Code: **File > Open Folder…** and choose that `DriveSync` folder (the one with `DriveSync.sln` in it).
3. Open the terminal (**Terminal > New Terminal**) and run:

   ```
   dotnet run --project src/DriveSync.App
   ```

   The first run downloads the SQLite package from NuGet, so you need internet once.

   Or press **F5** to run with the debugger (it uses `.vscode/launch.json`), so you can set breakpoints.
4. Other useful commands:
   - `dotnet build` builds everything without running it.
   - `dotnet publish src/DriveSync.App -c Release -r win-x64 --self-contained` makes a standalone `.exe` (milestone 7).

**First start:** DriveSync scans all drives. On a typical laptop this takes from under a minute to a few minutes;
the status bar shows progress. The index is then saved, so every later start has results ready straight away
while a refresh runs in the background.

## 3. How to use it

- **Step 1, file type:** the "File type" box at the top left opens a list of every file type on your PC
  (plus well-known ones like .pdf, .docx, .png, .py), with how many files of each type you have.
  Type to filter it (`py`, `.pdf`, or a description like `image`), then click a type or press Enter.
  Keep "All file types" to search everything; the ✕ button goes back to it.
- **Step 2, folder (optional):** the "Folder" box limits the search to one folder and everything below it.
  Click it for a list of your main folders (Desktop, Documents, Downloads…), recently used folders and drives;
  type a folder name (`invoices 2026`) to find any folder on the PC; type or paste a path (`C:\Work`);
  or press **Browse…** to pick one in the Windows folder dialog. "Search in this folder" in the preview
  panel limits the search to the selected file's folder. Keep "All drives" (or press ✕) to search everywhere.
- **Step 3, file name:** type part of a file name. Several words narrow it down: `report 2026` with type .docx
  finds `Final-Report-2026.docx`. With a type or folder chosen, leaving the name empty lists every file that fits.
- Down arrow or Enter moves into the results; Enter or double-click opens the file.
- Selecting a file shows its keywords, a short summary, and a preview (text or image).
- The bottom right shows how many files matched and how long the search took, in milliseconds.

## 4. How it works

```
DriveSync.sln
├── src/DriveSync.Core      the engine (class library, no UI)
│   ├── FileScanner.cs       walks all folders on all drives, in parallel
│   ├── FileIndex.cs         the in-memory index, and the search itself
│   ├── FileEntry.cs         one file: name, size, date, folder
│   ├── FileFormats.cs       the list of file types for the "File type" picker
│   ├── FolderNode.cs        one folder and the files directly in it
│   ├── IndexStore.cs        saves/loads the index to SQLite (%LOCALAPPDATA%\DriveSync\index.db)
│   ├── IndexWatcher.cs      FileSystemWatcher: hears about new, changed, deleted, renamed files
│   ├── IndexManager.cs      ties it together: load, watch, scan, rescan every 30 minutes
│   ├── TextExtractor.cs     reads text from .txt/.md/.csv/code, .docx, .pptx, .xlsx, .html
│   └── SummaryService.cs    keywords, summary sentences, preview text
└── src/DriveSync.App       the Windows window (WPF)
    ├── MainWindow.xaml      the layout: file type picker, search box, results list, preview panel, status bar
    ├── MainWindow.xaml.cs   what happens when you type, select, open
    ├── ResultRow.cs         one formatted row of the results list
    ├── FormatOption.cs      one formatted row of the file type list
    └── FolderOption.cs      one formatted row of the folder list
```

**Why it is faster than Windows search.** Windows File Explorer search often walks the folders on disk
again when you search outside indexed locations, and its index covers only some folders by default.
DriveSync reads every folder once, keeps all names in memory, and answers a search by checking those names
on every CPU core at once. Nothing touches the disk while you search, so results come back in milliseconds.

**Startup:**
1. Load the saved index from SQLite, so searching works immediately.
2. Start a `FileSystemWatcher` on every drive, so new, deleted and renamed files update the index live.
3. Scan all drives again in the background (catching anything that changed while the app was closed),
   swap in the new index and save it.
4. Rescan every 30 minutes as a safety net. This is the "periodic update" from the proposal.

**Scanning in parallel.** `FileScanner` keeps a shared stack of "folders still to read". One worker per CPU core
takes a folder, records its files, and pushes its sub-folders back on the stack. Junctions/shortcuts
(reparse points) are skipped so the scan can't loop, and folders without permission are skipped quietly.

**Searching in a folder stays fast.** With a folder chosen, the search first drops every folder outside it,
with one path check per folder, before looking at any file names. So a search in one folder is quicker than a search
of the whole PC, not slower. Finding folders for the picker uses the same in-memory index, so it never touches the disk either.

**Ranking.** Matches are ordered: exact name first, then names starting with what you typed,
then a word inside the name starting with it, then anything else. Ties go to shorter names. Only the best 500 are shown, so they are picked with a small heap
instead of sorting every match, which keeps broad searches (like "every .dll") quick.

**Summary.** `SummaryService` counts how often each word appears (ignoring common words like "the" and "and").
The top 8 are the keywords. Each sentence is scored by how many frequent words it contains, and the best 3
are shown in their original order. This is called a frequency-based extractive summary.
Only the first 200,000 characters of a file are read, to keep it fast.

## 5. Ideas for the next milestones

- **Benchmarks (milestone 6):** the status bar already shows search time. Compare it with Windows search on
  the same queries, and record the first-scan time and the index size.
- **Parser module (milestone 4):** add PDF text (for example with the PdfPig library) and smarter summaries
  using headings and first paragraphs.
- **Faster first scan:** reading the NTFS Master File Table directly (as the tool "Everything" does) can index
  a whole drive in seconds, but needs administrator rights.
- **Settings:** choose which folders to index or exclude, saved in the same SQLite file.
