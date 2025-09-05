using Rampastring.Tools;
using Rampastring.XNAUI;
using System;
using System.IO;
using TSMapEditor.CCEngine;
using TSMapEditor.Initialization;
using TSMapEditor.Models;
using TSMapEditor.Rendering;
using TSMapEditor.Extensions;
using System.Collections.Generic;
using System.Linq;

namespace TSMapEditor.UI.Windows.MainMenuWindows
{
    /// <summary>
    /// Helper class for setting up a map.
    /// </summary>
    public static class MapSetup
    {
        private static Map LoadedMap;
        private static CCFileManager ccFileManager;

        /// <summary>
        /// Tries to load a map. If successful, returns null. If loading the map
        /// fails, returns an error message.
        /// </summary>
        /// <param name="gameDirectory">The path to the game directory.</param>
        /// <param name="createNew">Whether a new map should be created (instead of loading an existing map).</param>
        /// <param name="existingMapPath">The path to the existing map file to load, if loading an existing map. Can be null if creating a new map.</param>
        /// <param name="newMapTheater">The theater of the map, if creating a new map.</param>
        /// <param name="newMapSize">The size of the map, if creating a new map.</param>
        /// <param name="windowManager">The XNAUI window manager.</param>
        /// <returns>Null of loading the map was successful, otherwise an error message.</returns>
        public static string InitializeMap(string gameDirectory, bool createNew, string existingMapPath, CreateNewMapEventArgs newMapParameters, WindowManager windowManager)
        {
            ccFileManager = new() { GameDirectory = gameDirectory };
            ccFileManager.ReadConfig();

            var gameConfigIniFiles = new GameConfigINIFiles(gameDirectory, ccFileManager);

            // Search for tutorial lines from all directories specified in the file manager configuration
            string tutorialsPath = ccFileManager.FindFileFromDirectories(Constants.TutorialIniPath);
            if (tutorialsPath == null)
                tutorialsPath = Path.Combine(gameDirectory, Constants.TutorialIniPath);

            var tutorialLines = new TutorialLines(tutorialsPath, a => windowManager.AddCallback(a, null));
            var themes = new Themes(IniFileEx.FromPathOrMix(Constants.ThemeIniPath, gameDirectory, ccFileManager));
            var evaSpeeches = new EvaSpeeches(IniFileEx.FromPathOrMix(Constants.EvaIniPath, gameDirectory, ccFileManager));
            var sounds = new Sounds(IniFileEx.FromPathOrMix(Constants.SoundIniPath, gameDirectory, ccFileManager));

            Map map = new Map(ccFileManager);

            if (createNew)
            {
                if (newMapParameters == null)
                    throw new NullReferenceException("Null new map parameters encountered when creating a new map!");

                map.InitNew(gameConfigIniFiles, newMapParameters.TheaterIndex, newMapParameters.MapSize, newMapParameters.StartingLevel);
            }
            else
            {
                try
                {
                    IniFileEx mapIni = new(Path.Combine(gameDirectory, existingMapPath), ccFileManager);

                    MapLoader.PreCheckMapIni(mapIni);

                    map.LoadExisting(gameConfigIniFiles, mapIni);
                }
                catch (IniParseException ex)
                {
                    return "The selected file does not appear to be a proper map file (INI file). Maybe it's corrupted?\r\n\r\nReturned error: " + ex.Message;
                }
                catch (MapLoadException ex)
                {
                    return "Failed to load the selected map file.\r\n\r\nReturned error: " + ex.Message;
                }
            }

            map.Rules.TutorialLines = tutorialLines;
            map.Rules.Themes = themes;
            map.Rules.Speeches = evaSpeeches;
            map.Rules.Sounds = sounds;

            Console.WriteLine();
            Console.WriteLine("Map created.");

            LoadedMap = map;

            return null;
        }

        /// <summary>
        /// Loads the theater graphics for the last-loaded map.
        /// </summary>
        /// <param name="windowManager">The window manager.</param>
        /// <param name="gameDirectory">The path to the game directory.</param>
        public static void LoadTheaterGraphics(WindowManager windowManager, string gameDirectory)
        {
            Theater theater = LoadedMap.EditorConfig.Theaters.Find(t => t.UIName.Equals(LoadedMap.TheaterName, StringComparison.InvariantCultureIgnoreCase));
            if (theater == null)
            {
                throw new InvalidOperationException("Theater of map not found: " + LoadedMap.TheaterName);
            }
            theater.ReadConfigINI(gameDirectory, ccFileManager);

            foreach (string theaterMIXName in theater.ContentMIXName)
                ccFileManager.LoadRequiredMixFile(theaterMIXName);

            foreach (string theaterMIXName in theater.OptionalContentMIXName)
                ccFileManager.LoadOptionalMixFile(theaterMIXName);

            TheaterGraphics theaterGraphics = new TheaterGraphics(windowManager.GraphicsDevice, theater, ccFileManager, LoadedMap.Rules);
            LoadedMap.TheaterInstance = theaterGraphics;
            FillConnectedTileFoundations(theaterGraphics);

            MapLoader.PostCheckMap(LoadedMap, theaterGraphics);

            EditorGraphics editorGraphics = new EditorGraphics();

            var uiManager = new UIManager(windowManager, LoadedMap, theaterGraphics, editorGraphics);
            windowManager.AddAndInitializeControl(uiManager);

            const int margin = 60;
            string errorList = string.Join("\r\n\r\n", MapLoader.MapLoadErrors);
            int errorListHeight = (int)Renderer.GetTextDimensions(errorList, Constants.UIDefaultFont).Y;

            if (errorListHeight > windowManager.RenderResolutionY - margin)
            {
                EditorMessageBox.Show(windowManager, "Errors while loading map",
                    "A massive number of errors was encountered while loading the map. See MapEditorLog.log for details.", MessageBoxButtons.OK);
            }
            else if (MapLoader.MapLoadErrors.Count > 0)
            {
                EditorMessageBox.Show(windowManager, "Errors while loading map",
                    "One or more errors were encountered while loading the map:\r\n\r\n" + errorList, MessageBoxButtons.OK);
            }
        }

        /// <summary>
        /// Automatically fills the foundations of all connected tiles
        /// for which the foundation has not been specified in the config.
        /// </summary>
        private static void FillConnectedTileFoundations(TheaterGraphics theaterGraphics)
        {
            foreach (var cliffType in LoadedMap.EditorConfig.Cliffs)
            {
                if (!cliffType.AllowedTheaters.Select(at => at.ToUpperInvariant()).Contains(LoadedMap.LoadedTheaterName.ToUpperInvariant()))
                    continue;

                var tiles = cliffType.Tiles;
                if (tiles.Count == 0)
                    throw new INIConfigException($"Connected terrain type {cliffType.IniName} has 0 tiles!");

                foreach (var cliffTypeTile in cliffType.Tiles)
                {
                    var tileSet = theaterGraphics.Theater.TileSets.Find(ts => ts.SetName == cliffTypeTile.TileSetName && ts.AllowToPlace);

                    if (tileSet == null)
                    {
                        string errorMessage = $"Unable to find TileSet \"{cliffTypeTile.TileSetName}\" " +
                            $"for connected terrain type \"{cliffType.IniName}\", tile index {cliffTypeTile.Index}";
#if DEBUG
                        throw new INIConfigException(errorMessage);
#else
                        Logger.Log("WARNING: " + errorMessage + ". Disabling the connected terrain type.");
                        cliffType.IsLegal = false;
                        break;
#endif
                    }

                    if (cliffTypeTile.IndicesInTileSet.Count == 0)
                        continue;

                    if (cliffTypeTile.Foundation != null)
                        continue;

                    cliffTypeTile.Foundation = new HashSet<GameMath.Point2D>();

                    int firstTileIndexWithinSet = cliffTypeTile.IndicesInTileSet[0];

                    int totalFirstTileIndex = tileSet.StartTileIndex + firstTileIndexWithinSet;

                    var tile = theaterGraphics.GetTile(totalFirstTileIndex);

                    for (int i = 0; i < tile.SubTileCount; i++)
                    {
                        var subTile = tile.GetSubTile(i);
                        if (subTile == null)
                            continue;

                        var offset = tile.GetSubTileCoordOffset(i).Value;
                        cliffTypeTile.Foundation.Add(offset);
                    }
                }
            }
        }
    }
}
