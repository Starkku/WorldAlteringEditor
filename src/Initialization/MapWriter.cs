﻿using CNCMaps.FileFormats.Encodings;
using Rampastring.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using TSMapEditor.Models;
using TSMapEditor.Models.MapFormat;

namespace TSMapEditor.Initialization
{
    /// <summary>
    /// Contains static methods for writing a map to a INI file.
    /// </summary>
    public static class MapWriter
    {
        private static IniSection FindOrMakeSection(string sectionName, IniFile mapIni)
        {
            var section = mapIni.GetSection(sectionName);
            if (section == null)
            {
                section = new IniSection(sectionName);
                mapIni.AddSection(section);
            }

            return section;
        }

        public static void WriteMapSection(IMap map, IniFile mapIni)
        {
            const string sectionName = "Map";

            var section = FindOrMakeSection(sectionName, mapIni);
            section.SetStringValue("Size", $"0,0,{map.Size.X},{map.Size.Y}");
            section.SetStringValue("Theater", map.Theater);
            section.SetStringValue("LocalSize", $"{map.LocalSize.X},{map.LocalSize.Y},{map.LocalSize.Width},{map.LocalSize.Height}");

            mapIni.AddSection(section);
        }

        public static void WriteBasicSection(IMap map, IniFile mapIni)
        {
            const string sectionName = "Basic";

            var section = FindOrMakeSection(sectionName, mapIni);
            map.Basic.WritePropertiesToIniSection(section);
        }

        public static void WriteIsoMapPack5(IMap map, IniFile mapIni)
        {
            const string sectionName = "IsoMapPack5";
            mapIni.RemoveSection(sectionName);

            var tilesToSave = new List<IsoMapPack5Tile>();

            for (int y = 0; y < map.Tiles.Length; y++)
            {
                for (int x = 0; x < map.Tiles[y].Length; x++)
                {
                    var tile = map.Tiles[y][x];
                    if (tile == null)
                        continue;

                    if (tile.Level == 0 && tile.TileIndex == 0)
                        continue;

                    tilesToSave.Add(tile);
                }
            }

            // Typically, removing the height level 0 clear tiles and then sorting 
            // the tiles first by X then by Level and then by TileIndex gives good compression. 
            // https://modenc.renegadeprojects.com/IsoMapPack5

            tilesToSave = tilesToSave.OrderBy(t => t.X).ThenBy(t => t.Level).ThenBy(t => t.TileIndex).ToList();

            // Now we pretty much have to reverse the process done in MapLoader.ReadIsoMapPack

            var buffer = new List<byte>();
            foreach (IsoMapPack5Tile tile in tilesToSave)
            {
                buffer.AddRange(BitConverter.GetBytes(tile.X));
                buffer.AddRange(BitConverter.GetBytes(tile.Y));
                buffer.AddRange(BitConverter.GetBytes(tile.TileIndex));
                buffer.Add(tile.SubTileIndex);
                buffer.Add(tile.Level);
                buffer.Add(tile.IceGrowth);
            }

            const int maxOutputSize = 8192;
            // generate IsoMapPack5 blocks
            int processedBytes = 0;
            List<byte> finalData = new List<byte>();
            List<byte> block = new List<byte>(maxOutputSize);
            while (buffer.Count > processedBytes)
            {
                ushort blockOutputSize = (ushort)Math.Min(buffer.Count - processedBytes, maxOutputSize);
                for (int i = processedBytes; i < processedBytes + blockOutputSize; i++)
                {
                    block.Add(buffer[i]);
                }

                byte[] compressedBlock = MiniLZO.MiniLZO.Compress(block.ToArray());
                // InputSize
                finalData.AddRange(BitConverter.GetBytes((ushort)compressedBlock.Length));
                // OutputSize
                finalData.AddRange(BitConverter.GetBytes(blockOutputSize));
                // actual data
                finalData.AddRange(compressedBlock);

                processedBytes += blockOutputSize;
                block.Clear();
            }

            // Base64 encode
            var section = new IniSection(sectionName);
            mapIni.AddSection(section);
            WriteBase64ToSection(finalData.ToArray(), section);
        }

        /// <summary>
        /// Generic method for writing a byte array as a 
        /// base64-encoded line-length-limited block of data to a INI section.
        /// Used for writing IsoMapPack5, OverlayPack and OverlayDataPack.
        /// </summary>
        private static void WriteBase64ToSection(byte[] data, IniSection section)
        {
            string base64String = Convert.ToBase64String(data.ToArray());
            const int maxIsoMapPackEntryLineLength = 70;
            int lineIndex = 1; // TS/RA2 IsoMapPack5, OverlayPack and OverlayDataPack is indexed starting from 1
            int processedChars = 0;

            while (processedChars < base64String.Length)
            {
                int length = Math.Min(base64String.Length - processedChars, maxIsoMapPackEntryLineLength);

                string substring = base64String.Substring(processedChars, length);
                section.SetStringValue(lineIndex.ToString(), substring);
                lineIndex++;
                processedChars += length;
            }
        }

        public static void WriteOverlays(IMap map, IniFile mapIni)
        {
            const string overlayPackSectionName = "OverlayPack";
            const string overlayDataPackSectionName = "OverlayDataPack";

            mapIni.RemoveSection(overlayPackSectionName);
            mapIni.RemoveSection(overlayDataPackSectionName);

            var overlayArray = new byte[Constants.MAX_MAP_LENGTH_IN_DIMENSION * Constants.MAX_MAP_LENGTH_IN_DIMENSION];
            for (int i = 0; i < overlayArray.Length; i++)
                overlayArray[i] = Constants.NO_OVERLAY;

            var overlayDataArray = new byte[Constants.MAX_MAP_LENGTH_IN_DIMENSION * Constants.MAX_MAP_LENGTH_IN_DIMENSION];

            map.DoForAllValidTiles(tile =>
            {
                if (tile.Overlay == null)
                    return;

                int dataIndex = (tile.Y * Constants.MAX_MAP_LENGTH_IN_DIMENSION) + tile.X;

                overlayArray[dataIndex] = (byte)tile.Overlay.OverlayType.Index;
                overlayDataArray[dataIndex] = (byte)tile.Overlay.FrameIndex;
            });

            // Format80 compression
            byte[] compressedOverlayArray = Format5.Encode(overlayArray, Constants.OverlayPackFormat);
            byte[] compressedOverlayDataArray = Format5.Encode(overlayDataArray, Constants.OverlayPackFormat);

            // Base64 encode
            var overlayPackSection = new IniSection(overlayPackSectionName);
            mapIni.AddSection(overlayPackSection);
            WriteBase64ToSection(compressedOverlayArray, overlayPackSection);

            var overlayDataPackSection = new IniSection(overlayDataPackSectionName);
            mapIni.AddSection(overlayDataPackSection);
            WriteBase64ToSection(compressedOverlayDataArray, overlayDataPackSection);
        }

        private static string GetAttachedTagName(TechnoBase techno)
        {
            return techno.AttachedTag == null ? Constants.NoneValue2 : techno.AttachedTag.ID;
        }

        public static void WriteAircraft(IMap map, IniFile mapIni)
        {
            const string sectionName = "Aircraft";

            mapIni.RemoveSection(sectionName);
            if (map.Aircraft.Count == 0)
                return;

            var section = new IniSection(sectionName);
            mapIni.AddSection(section);

            for (int i = 0; i < map.Aircraft.Count; i++)
            {
                var aircraft = map.Aircraft[i];

                // INDEX = OWNER,ID,HEALTH,X,Y,FACING,MISSION,TAG,VETERANCY,GROUP,AUTOCREATE_NO_RECRUITABLE,AUTOCREATE_YES_RECRUITABLE

                string attachedTag = GetAttachedTagName(aircraft);

                string value = $"{aircraft.Owner.ININame},{aircraft.ObjectType.ININame},{aircraft.HP}," +
                               $"{aircraft.Position.X},{aircraft.Position.Y},{aircraft.Facing}," +
                               $"{aircraft.Mission},{attachedTag},{aircraft.Veterancy}," +
                               $"{aircraft.AutocreateNoRecruitable},{aircraft.AutocreateYesRecruitable}";

                section.SetStringValue(i.ToString(), value);
            }
        }

        public static void WriteUnits(IMap map, IniFile mapIni)
        {
            const string sectionName = "Units";

            mapIni.RemoveSection(sectionName);
            if (map.Units.Count == 0)
                return;

            var section = new IniSection(sectionName);
            mapIni.AddSection(section);

            for (int i = 0; i < map.Units.Count; i++)
            {
                var unit = map.Units[i];

                // INDEX=OWNER,ID,HEALTH,X,Y,FACING,MISSION,TAG,VETERANCY,GROUP,HIGH,FOLLOWS_INDEX,AUTOCREATE_NO_RECRUITABLE,AUTOCREATE_YES_RECRUITABLE

                string attachedTag = GetAttachedTagName(unit);
                string followsIndex = unit.FollowedUnit == null ? "-1" : map.Units.FindIndex(otherUnit => otherUnit == unit.FollowedUnit).ToString();

                string value = $"{unit.Owner.ININame},{unit.ObjectType.ININame},{unit.HP}," +
                               $"{unit.Position.X},{unit.Position.Y},{unit.Facing}," +
                               $"{unit.Mission},{attachedTag},{unit.Veterancy}," +
                               $"{unit.Group},{unit.High},{followsIndex}," +
                               $"{unit.AutocreateNoRecruitable},{unit.AutocreateYesRecruitable}";

                section.SetStringValue(i.ToString(), value);
            }
        }

        public static void WriteInfantry(IMap map, IniFile mapIni)
        {
            const string sectionName = "Infantry";

            mapIni.RemoveSection(sectionName);
            if (map.Units.Count == 0)
                return;

            var section = new IniSection(sectionName);
            mapIni.AddSection(section);

            for (int i = 0; i < map.Infantry.Count; i++)
            {
                var infantry = map.Infantry[i];

                // INDEX=OWNER,ID,HEALTH,X,Y,SUB_CELL,MISSION,FACING,TAG,VETERANCY,GROUP,HIGH,AUTOCREATE_NO_RECRUITABLE,AUTOCREATE_YES_RECRUITABLE

                string attachedTag = GetAttachedTagName(infantry);

                string value = $"{infantry.Owner.ININame},{infantry.ObjectType.ININame},{infantry.HP}," +
                               $"{infantry.Position.X},{infantry.Position.Y},{infantry.SubCell}," +
                               $"{infantry.Mission},{infantry.Facing},{attachedTag},{infantry.Veterancy}," +
                               $"{infantry.Group},{infantry.High}," +
                               $"{infantry.AutocreateNoRecruitable},{infantry.AutocreateYesRecruitable}";

                section.SetStringValue(i.ToString(), value);
            }
        }

        private static string UpgradeToString(BuildingType upgrade)
        {
            if (upgrade == null)
                return Constants.NoneValue2;

            return upgrade.ININame;
        }

        public static void WriteBuildings(IMap map, IniFile mapIni)
        {
            const string sectionName = "Structures";

            mapIni.RemoveSection(sectionName);
            if (map.Structures.Count == 0)
                return;

            var section = new IniSection(sectionName);
            mapIni.AddSection(section);

            for (int i = 0; i < map.Structures.Count; i++)
            {
                var structure = map.Structures[i];

                // INDEX=OWNER,ID,HEALTH,X,Y,FACING,TAG,AI_SELLABLE,AI_REBUILDABLE,POWERED_ON,UPGRADES,SPOTLIGHT,UPGRADE_1,UPGRADE_2,UPGRADE_3,AI_REPAIRABLE,NOMINAL

                string attachedTag = GetAttachedTagName(structure);
                string upgrade1 = UpgradeToString(structure.Upgrades[0]);
                string upgrade2 = UpgradeToString(structure.Upgrades[1]);
                string upgrade3 = UpgradeToString(structure.Upgrades[2]);

                string value = $"{structure.Owner.ININame},{structure.ObjectType.ININame},{structure.HP}," +
                               $"{structure.Position.X},{structure.Position.Y}," +
                               $"{structure.Facing},{attachedTag},{structure.AISellable}," +
                               $"{structure.AIRebuildable},{structure.Powered}," +
                               $"{structure.UpgradeCount},{((int)structure.Spotlight)}," + 
                               $"{upgrade1},{upgrade2},{upgrade3},{structure.AIRepairable},{structure.Nominal}";

                section.SetStringValue(i.ToString(), value);
            }
        }
    }
}