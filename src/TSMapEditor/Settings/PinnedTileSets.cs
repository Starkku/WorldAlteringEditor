using Rampastring.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace TSMapEditor.Settings
{
    public class PinnedTileSets
    {
        private const string IniSectionName = "PinnedTileSets";

        private List<string> entries = new List<string>();

        public void Add(string tileSetName)
        {
            if (entries.Contains(tileSetName))
                return;

            if (tileSetName.Contains(';'))
            {
                Logger.Log($"Warning: Cannot pin TileSet {tileSetName} because its name contains a semicolon.");
                return;
            }

            entries.Add(tileSetName);
        }

        public void Remove(string tileSetName)
        {
            entries.Remove(tileSetName);
        }

        public void ClearAll() => entries.Clear();

        public void WriteToIniFile(IniFile iniFile)
        {
            iniFile.RemoveSection(IniSectionName);

            if (entries.Count == 0)
                return;

            var section = new IniSection(IniSectionName);
            for (int i = 0; i < entries.Count; i++)
            {
                section.AddKey(i.ToString(CultureInfo.InvariantCulture), entries[i]);
            }
            iniFile.AddSection(section);
        }

        public void ReadFromIniFile(IniFile iniFile)
        {
            var keys = iniFile.GetSectionKeys(IniSectionName);
            if (keys == null)
                return;

            foreach (string key in keys)
            {
                string path = iniFile.GetStringValue(IniSectionName, key, string.Empty);
                if (!string.IsNullOrWhiteSpace(path))
                    entries.Add(path);
            }
        }

        public void DoForAllEntries(Action<string> action)
        {
            foreach (string entry in entries)
                action(entry);
        }
    }
}
