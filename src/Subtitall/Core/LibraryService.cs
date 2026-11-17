using System.Text.RegularExpressions;

namespace Subtitall.Core;

/// <summary>In-memory list of linked video files, deduplicated by path.</summary>
public sealed class LibraryService
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, MediaItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MediaItem> _ordered = [];
    private int _nextId;

    /// <summary>Adds video files and/or folders (scanned recursively). Returns newly added items.</summary>
    public IReadOnlyList<MediaItem> AddPaths(IEnumerable<string> paths)
    {
        var added = new List<MediaItem>();
        lock (_lock)
        {
            foreach (var path in ExpandToVideoFiles(paths))
            {
                var fullPath = Path.GetFullPath(path);
                if (_byPath.ContainsKey(fullPath))
                    continue;

                long size = 0;
                try { size = new FileInfo(fullPath).Length; } catch (IOException) { }

                if (IsSampleFile(fullPath, size) || Reencode.EncodePlanner.IsPendingFile(fullPath))
                    continue;

                var reading = MediaNameReader.Read(fullPath);
                var item = new MediaItem
                {
                    Id = (++_nextId).ToString(),
                    Path = fullPath,
                    Parsed = reading.Parsed,
                    NameSource = reading.Source,
                    SourceName = reading.Name,
                    SizeBytes = size,
                };
                _byPath[fullPath] = item;
                _ordered.Add(item);
                added.Add(item);
            }
        }
        return added;
    }

    public IReadOnlyList<MediaItem> GetAll()
    {
        lock (_lock) return _ordered.ToList();
    }

    public MediaItem? Get(string id)
    {
        lock (_lock) return _ordered.FirstOrDefault(i => i.Id == id);
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var item = _ordered.FirstOrDefault(i => i.Id == id);
            if (item is null)
                return false;
            _ordered.Remove(item);
            _byPath.Remove(item.Path);
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _ordered.Clear();
            _byPath.Clear();
        }
    }

    /// <summary>Updates an item's path after a rename, keeping dedupe consistent.</summary>
    public void UpdatePath(MediaItem item, string newPath)
    {
        lock (_lock)
        {
            _byPath.Remove(item.Path);
            item.Path = Path.GetFullPath(newPath);
            _byPath[item.Path] = item;
        }
        // The new name is read afresh; detection re-runs for it (see MediaDetectionService).
        Reread(item);
    }

    /// <summary>
    /// The file was replaced by a re-encoded copy: same video, possibly another extension and
    /// certainly another size. What the app knows about it (show, episode) still holds.
    /// </summary>
    public void Replace(MediaItem item, string newPath)
    {
        lock (_lock)
        {
            _byPath.Remove(item.Path);
            item.Path = Path.GetFullPath(newPath);
            _byPath[item.Path] = item;
        }
        try { item.SizeBytes = new FileInfo(item.Path).Length; } catch (IOException) { }
    }

    /// <summary>Reads the file again from scratch, dropping what the user or a lookup said it was.</summary>
    public void Reread(MediaItem item)
    {
        var reading = MediaNameReader.Read(item.Path);
        item.Parsed = reading.Parsed;
        item.NameSource = reading.Source;
        item.SourceName = reading.Name;
        item.HashLookupDone = false;
        item.Guess = null;
    }

    /// <summary>Records what a file is when that came from somewhere else than its name.</summary>
    public void Identify(MediaItem item, ParsedMedia parsed, NameSource source, string? sourceName)
    {
        item.Parsed = parsed;
        item.NameSource = source;
        item.SourceName = sourceName;
        item.Guess = null;
        item.Detection = null;
        item.SeasonOverride = null;
    }

    /// <summary>Re-checks which subtitle files exist on disk for the given languages.</summary>
    public void RefreshSubtitleStatus(IReadOnlyList<string> languages)
    {
        foreach (var item in GetAll())
        {
            var status = new Dictionary<string, bool>();
            foreach (var lang in languages)
                status[lang] = SubtitleNaming.HasSubtitle(item.Path, lang);
            item.Subtitles = status;
        }
    }

    private static IEnumerable<string> ExpandToVideoFiles(IEnumerable<string> paths)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                // Materialize inside the try — the enumeration is lazy, so exceptions
                // surface at iteration time, not at EnumerateFiles().
                List<string> files;
                try
                {
                    files = Directory.EnumerateFiles(path, "*", options)
                        .Where(FilenameParser.IsVideoFile)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (var file in files)
                    yield return file;
            }
            else if (File.Exists(path) && FilenameParser.IsVideoFile(path))
            {
                yield return path;
            }
        }
    }

    // Scene "sample" clips are small teaser files shipped next to the real release.
    private static bool IsSampleFile(string path, long size) =>
        size < 100 * 1024 * 1024 &&
        Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"(^|[ ._\-])sample([ ._\-]|$)", RegexOptions.IgnoreCase);
}
