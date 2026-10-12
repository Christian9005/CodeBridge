#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace CodeBridge.VisualStudio.Editor
{
    /// <summary>Per-user preferences stored in %LOCALAPPDATA%\CodeBridge\editor-settings.json (last port, tour progress).</summary>
    internal sealed class EditorSettings
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodeBridge",
            "editor-settings.json");

        public string? LastPort { get; set; }

        /// <summary>Home Assistant MQTT broker (address, port, user, device name). The password is stored encrypted in <see cref="BoardTokens"/>.</summary>
        public string? HaBroker { get; set; }

        public int HaPort { get; set; } = 1883;

        public string? HaUser { get; set; }

        public string? HaDeviceName { get; set; }

        /// <summary>True once the CodeBridge Tour was opened automatically (it is only offered once).</summary>
        public bool TourOffered { get; set; }

        /// <summary>Indexes of the tour chapters the user has opened.</summary>
        public HashSet<int> TourVisited { get; } = new HashSet<int>();

        public int TourLastChapter { get; set; }

        public static EditorSettings Load()
        {
            var settings = new EditorSettings();
            try
            {
                if (File.Exists(FilePath) &&
                    new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath)) is Dictionary<string, object> map)
                {
                    settings.LastPort = map.TryGetValue("lastPort", out var port) ? port as string : null;
                    settings.HaBroker = map.TryGetValue("haBroker", out var haBroker) ? haBroker as string : null;
                    settings.HaPort = map.TryGetValue("haPort", out var haPort) && haPort is int hp && hp > 0 && hp <= 65535 ? hp : 1883;
                    settings.HaUser = map.TryGetValue("haUser", out var haUser) ? haUser as string : null;
                    settings.HaDeviceName = map.TryGetValue("haDeviceName", out var haName) ? haName as string : null;
                    settings.TourOffered = map.TryGetValue("tourOffered", out var offered) && offered is bool b && b;
                    settings.TourLastChapter = map.TryGetValue("tourLastChapter", out var last) && last is int i ? i : 0;
                    if (map.TryGetValue("tourVisited", out var visited) && visited is string text)
                    {
                        foreach (var part in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (int.TryParse(part, out var chapter))
                                settings.TourVisited.Add(chapter);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A corrupt settings file must never break the editor.
            }

            return settings;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var map = new Dictionary<string, object?>
                {
                    ["lastPort"] = LastPort,
                    ["haBroker"] = HaBroker,
                    ["haPort"] = HaPort,
                    ["haUser"] = HaUser,
                    ["haDeviceName"] = HaDeviceName,
                    ["tourOffered"] = TourOffered,
                    ["tourLastChapter"] = TourLastChapter,
                    ["tourVisited"] = string.Join(",", TourVisited.OrderBy(v => v))
                };
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(map));
            }
            catch (Exception)
            {
                // Read-only profile: preferences simply are not remembered.
            }
        }
    }
}
