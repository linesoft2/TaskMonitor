using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace task_monitor
{
    /// <summary>
    /// Resolves an executable's file description (the version-resource <c>FileDescription</c>
    /// field — Task Manager's process "描述") from its full image path, for the per-process
    /// list hover tooltip (CPU/RAM/Net). Cached per exe path: the version resource is a small
    /// disk read done once per distinct exe, then reused across the three lists for the life of
    /// the app. Returns "" when the path is missing or the file has no description.
    /// </summary>
    internal static class FileDescriptionResolver
    {
        // Shared across the CPU/RAM/Net lists. Path-keyed; bounded by the distinct exes hovered.
        private static readonly Dictionary<string, string> s_descriptionByPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            lock (s_descriptionByPath)
                if (s_descriptionByPath.TryGetValue(path, out string cached)) return cached;

            string description = ResolveUncached(path);

            // Cache hit and miss both stored — unreadable paths aren't retried every hover.
            lock (s_descriptionByPath)
                s_descriptionByPath[path] = description;

            return description;
        }

        private static string ResolveUncached(string path)
        {
            try
            {
                // GetVersionInfo returns empty fields rather than throwing for a file with no
                // version resource; guard anyway for odd/locked paths.
                return FileVersionInfo.GetVersionInfo(path)?.FileDescription?.Trim() ?? "";
            }
            catch
            {
                return ""; // unreadable / inaccessible — tooltip shows the path alone
            }
        }
    }
}
