#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeBridge.VisualStudio.Firmware
{
    /// <summary>Finds the PlatformIO project (the folder with platformio.ini) the user is working on.</summary>
    internal static class FirmwareProjects
    {
        public static bool IsProject(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            try
            {
                return File.Exists(Path.Combine(directory, "platformio.ini"));
            }
            catch (ArgumentException)
            {
                return false; // a path with illegal characters is not a project
            }
        }

        /// <summary>Walks up from a file or folder to the folder holding platformio.ini.</summary>
        public static string? FindRootAbove(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                var directory = File.Exists(path) ? Path.GetDirectoryName(path) : path;
                for (var depth = 0; directory != null && depth < 12; depth++)
                {
                    if (IsProject(directory))
                        return directory;

                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch (ArgumentException)
            {
                // a path with illegal characters cannot be inside a project
            }

            return null;
        }

        /// <summary>Looks for platformio.ini a couple of levels below a folder (a solution folder that contains the firmware project).</summary>
        public static string? FindRootBelow(string? directory, int depth = 2)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return null;

            if (IsProject(directory))
                return directory;

            if (depth <= 0)
                return null;

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory).Where(d => !Path.GetFileName(d).StartsWith(".", StringComparison.Ordinal) && !Path.GetFileName(d).Equals("node_modules", StringComparison.OrdinalIgnoreCase)))
                {
                    var found = FindRootBelow(child, depth - 1);
                    if (found != null)
                        return found;
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }

            return null;
        }

        /// <summary>The first project found among the places a developer might be looking at: the open file, the selection, the solution folder.</summary>
        public static string? Locate(IEnumerable<string?> activePaths, string? solutionDirectory)
        {
            foreach (var path in activePaths)
            {
                var found = FindRootAbove(path);
                if (found != null)
                    return found;
            }

            return FindRootAbove(solutionDirectory) ?? FindRootBelow(solutionDirectory);
        }
    }
}
