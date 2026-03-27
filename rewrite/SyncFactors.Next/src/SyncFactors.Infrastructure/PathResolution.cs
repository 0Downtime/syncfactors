namespace SyncFactors.Infrastructure;

internal static class PathResolution
{
    public static string? ResolveExistingFile(string? configured, params string[] candidates)
    {
        foreach (var path in EnumerateCandidatePaths(configured, candidates))
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public static string ResolvePreferredPath(string? configured, params string[] candidates)
    {
        var existing = ResolveExistingFile(configured, candidates);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var preferred = EnumerateCandidatePaths(configured, candidates).FirstOrDefault();
        return string.IsNullOrWhiteSpace(preferred)
            ? Path.GetFullPath(configured ?? string.Empty)
            : preferred;
    }

    public static string ResolveConfiguredPath(string configured)
    {
        foreach (var path in EnumeratePathOptions(configured))
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return EnumeratePathOptions(configured).First();
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string? configured, params string[] candidates)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var path in EnumeratePathOptions(configured))
            {
                yield return path;
            }

            yield break;
        }

        foreach (var candidate in candidates)
        {
            foreach (var path in EnumeratePathOptions(candidate))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumeratePathOptions(string path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Path.IsPathRooted(path))
        {
            var full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                yield return full;
            }

            yield break;
        }

        foreach (var root in EnumerateSearchRoots())
        {
            var full = Path.GetFullPath(Path.Combine(root, path));
            if (seen.Add(full))
            {
                yield return full;
            }
        }
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateRootAndAncestors(Directory.GetCurrentDirectory()))
        {
            if (seen.Add(root))
            {
                yield return root;
            }
        }

        foreach (var root in EnumerateRootAndAncestors(AppContext.BaseDirectory))
        {
            if (seen.Add(root))
            {
                yield return root;
            }
        }
    }

    private static IEnumerable<string> EnumerateRootAndAncestors(string startPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }
}
