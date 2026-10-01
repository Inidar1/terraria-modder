using TerrariaModManager.Models;

namespace TerrariaModManager.Services;

public static class NexusFileSelector
{
    public static NexusModFile? SelectCurrentMain(IEnumerable<NexusModFile> files)
    {
        var candidates = files
            .Where(file => !IsInactive(file.CategoryName))
            .ToArray();

        return candidates
            .Where(file => file.IsPrimary)
            .OrderByDescending(file => file.UploadedTimestamp)
            .FirstOrDefault()
            ?? candidates
                .Where(file => string.Equals(file.CategoryName, "MAIN", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.UploadedTimestamp)
                .FirstOrDefault();
    }

    private static bool IsInactive(string category) =>
        category.Equals("ARCHIVED", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("REMOVED", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("OLD_VERSION", StringComparison.OrdinalIgnoreCase);
}
