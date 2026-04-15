using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RandVideoPlayer.Library;

public sealed class FolderLibrary
{
    // All extensions we consider playable. Matches what LibVLC handles broadly.
    public static readonly HashSet<string> PlayableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
        ".mpg", ".mpeg", ".ts", ".m2ts", ".vob", ".ogv", ".3gp", ".3g2",
        // Audio
        ".mp3", ".ogg", ".oga", ".opus", ".flac", ".wav", ".m4a", ".aac",
        ".wma", ".aif", ".aiff", ".ape", ".mka"
    };

    public string RootFolder { get; }

    // Full paths of everything currently in the folder (recursive), alpha-sorted.
    public List<string> AlphaList { get; private set; } = new();

    public FolderLibrary(string rootFolder)
    {
        RootFolder = rootFolder;
    }

    public void Rescan()
    {
        var results = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(RootFolder, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(path);
                if (PlayableExtensions.Contains(ext))
                {
                    results.Add(path);
                }
            }
        }
        catch (Exception)
        {
            // Surface via caller if needed. Partial result is acceptable.
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
        AlphaList = results;
    }

    public string ToRelative(string fullPath)
    {
        var rel = Path.GetRelativePath(RootFolder, fullPath);
        return rel.Replace('\\', '/');
    }

    public string ToFull(string relative)
    {
        return Path.GetFullPath(Path.Combine(RootFolder, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    public IEnumerable<string> AlphaRelative() => AlphaList.Select(ToRelative);
}
