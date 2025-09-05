﻿using Rampastring.Tools;

namespace TSMapEditor
{
    public static class Constants
    {
        public const string ReleaseVersion = "1.7.0";

        public static int CellSizeX = 48;
        public static int CellSizeY = 24;
        public const int CellSizeInLeptons = 256;
        public static int CellHeight => CellSizeY / 2;
        public static int HighBridgeHeight = 4;
        public static int TileColorBufferSize = 576;

        public static int RenderPixelPadding = 50;

        public static bool IsFlatWorld = false;
        public static bool TheaterPaletteForTiberium = false;
        public static bool TheaterPaletteForVeins = false;
        public static bool TiberiumAffectedByLighting = false;
        public static bool TiberiumTreesAffectedByLighting = false;
        public static bool TerrainPaletteBuildingsAffectedByLighting = false;
        public static bool VoxelsAffectedByLighting = false;
        public static bool NewTheaterGenericBuilding = false;
        public static bool DrawBuildingAnimationShadows = false;
        public static bool IsRA2YR = false;
        public static bool WarnOfTooManyTriggerActions = true;
        public static bool DefaultPreview = false;

        public static string[] ExpectedClientExecutableNames = new string[] { "DTA.exe" };
        public static string GameRegistryInstallPath = "SOFTWARE\\DawnOfTheTiberiumAge";
        public static string OpenFileDialogFilter = "TS maps|*.map|All files|*.*";

        public static bool EnableIniInclude = false;
        public static bool EnableIniInheritance = false;

        public static bool IntegerVariables = false;

        public static string RulesIniPath;
        public static string FirestormIniPath;
        public static string ArtIniPath;
        public static string FirestormArtIniPath;
        public static string AIIniPath;
        public static string FirestormAIIniPath;
        public static string TutorialIniPath;
        public static string ThemeIniPath;
        public static string EvaIniPath;
        public static string SoundIniPath;

        public const int TextureSizeLimit = 16384;

        public static int MaxMapWidth;
        public static int MaxMapHeight;

        public const byte MaxMapHeightLevel = 12;
        public static int MapYBaseline => MaxMapHeightLevel * CellHeight;

        public static int MaxWaypoint = 100;

        public const int ObjectHealthMax = 256;
        public const int FacingMax = 255;

        public const int TurretFrameCount = 32;

        // TODO parse from Rules.ini
        public const int ConditionYellowHP = 128;

        public const int UIEmptySideSpace = 10;
        public const int UIEmptyTopSpace = 10;
        public const int UIEmptyBottomSpace = 10;

        public const int UIHorizontalSpacing = 6;
        public const int UIVerticalSpacing = 6;

        public const int UIDefaultFont = 0;
        public const int UIBoldFont = 1;

        public const int UITextBoxHeight = 21;
        public const int UIButtonHeight = 23;

        public const int UITopBarMenuHeight = 23;

        public static int UITreeViewLineHeight = 20;
        public static int UIDefaultSidebarWidth = 250;
        public static int UITileSetListWidth = 180;

        public static double UIAccidentalClickPreventionTime = 0.2;

        public static int MapPreviewMaxWidth = 800;
        public static int MapPreviewMaxHeight = 400;

        public static int MaxHouseTechLevel = 10;

        public const int MAX_MAP_LENGTH_IN_DIMENSION = 512;
        public const int NO_OVERLAY = -1;
        public const int OverlayPackFormat = 80;

        public const string NoneValue1 = "<none>";
        public const string NoneValue2 = "None";

        public const float RemapBrightenFactor = 1.25f;

        // The resolution of depth rendering. In other words, the minimum depth difference that is significant enough to have an impact on rendering order.
        public const float DepthEpsilon = 1e-5f;

        // Depth is between 0.0 and 1.0. How much of the scale is reserved for depth increasing as we go southwards on the map.
        public const float DownwardsDepthRenderSpace = 0.90f;

        // How much of the depth scale (0.0 to 1.0) is reserved for depth increasing as we go up the map height levels.
        // Calculated dynamically.
        public static float DepthRenderStep = 0;

        public const string ClipboardMapDataFormatValue = "ScenarioEditorCopiedMapData";
        public const string UserDataFolder = "UserData";

        public const char NewTheaterGenericLetter = 'G';

        public const string VeinholeMonsterTypeName = "VEINHOLE";
        public const string VeinholeDummyTypeName = "VEINHOLEDUMMY";

        public const int MultiplayerMaxPlayers = 8;

        public const int TS_WAYPT_SPECIAL = 100;

        public static void Init()
        {
            const string ConstantsSectionName = "Constants";
            const string FilePathsSectionName = "FilePaths";

            IniFile constantsIni = Helpers.ReadConfigINI("Constants.ini");

            CellSizeX = constantsIni.GetIntValue(ConstantsSectionName, nameof(CellSizeX), CellSizeX);
            MaxMapWidth = TextureSizeLimit / CellSizeX;
            CellSizeY = constantsIni.GetIntValue(ConstantsSectionName, nameof(CellSizeY), CellSizeY);
            MaxMapHeight = TextureSizeLimit / CellSizeY;

            TileColorBufferSize = constantsIni.GetIntValue(ConstantsSectionName, nameof(TileColorBufferSize), TileColorBufferSize);

            RenderPixelPadding = constantsIni.GetIntValue(ConstantsSectionName, nameof(RenderPixelPadding), RenderPixelPadding);

            IsFlatWorld = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(IsFlatWorld), IsFlatWorld);
            TheaterPaletteForTiberium = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(TheaterPaletteForTiberium), TheaterPaletteForTiberium);
            TheaterPaletteForVeins = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(TheaterPaletteForVeins), TheaterPaletteForVeins);
            TiberiumAffectedByLighting = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(TiberiumAffectedByLighting), TiberiumAffectedByLighting);
            TiberiumTreesAffectedByLighting = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(TiberiumTreesAffectedByLighting), TiberiumTreesAffectedByLighting);
            TerrainPaletteBuildingsAffectedByLighting = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(TerrainPaletteBuildingsAffectedByLighting), TerrainPaletteBuildingsAffectedByLighting);
            VoxelsAffectedByLighting = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(VoxelsAffectedByLighting), VoxelsAffectedByLighting);
            NewTheaterGenericBuilding = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(NewTheaterGenericBuilding), NewTheaterGenericBuilding);
            DrawBuildingAnimationShadows = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(DrawBuildingAnimationShadows), DrawBuildingAnimationShadows);
            IsRA2YR = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(IsRA2YR), IsRA2YR);
            WarnOfTooManyTriggerActions = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(WarnOfTooManyTriggerActions), WarnOfTooManyTriggerActions);
            DefaultPreview = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(DefaultPreview), DefaultPreview);

            // Check two keys for backwards compatibility
            if (constantsIni.KeyExists(ConstantsSectionName, "ExpectedClientExecutableName"))
                ExpectedClientExecutableNames = constantsIni.GetSection(ConstantsSectionName).GetListValue("ExpectedClientExecutableName", ',', s => s).ToArray();
            else
                ExpectedClientExecutableNames = constantsIni.GetSection(ConstantsSectionName).GetListValue(nameof(ExpectedClientExecutableNames), ',', s => s).ToArray();

            GameRegistryInstallPath = constantsIni.GetStringValue(ConstantsSectionName, nameof(GameRegistryInstallPath), GameRegistryInstallPath);
            OpenFileDialogFilter = constantsIni.GetStringValue(ConstantsSectionName, nameof(OpenFileDialogFilter), OpenFileDialogFilter);

            EnableIniInclude = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(EnableIniInclude), EnableIniInclude);
            EnableIniInheritance = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(EnableIniInheritance), EnableIniInheritance);

            IntegerVariables = constantsIni.GetBooleanValue(ConstantsSectionName, nameof(IntegerVariables), IntegerVariables);

            MaxWaypoint = constantsIni.GetIntValue(ConstantsSectionName, nameof(MaxWaypoint), MaxWaypoint);

            MapPreviewMaxWidth = constantsIni.GetIntValue(ConstantsSectionName, nameof(MapPreviewMaxWidth), MapPreviewMaxWidth);
            MapPreviewMaxHeight = constantsIni.GetIntValue(ConstantsSectionName, nameof(MapPreviewMaxHeight), MapPreviewMaxHeight);

            MaxHouseTechLevel = constantsIni.GetIntValue(ConstantsSectionName, nameof(MaxHouseTechLevel), MaxHouseTechLevel);

            RulesIniPath = constantsIni.GetStringValue(FilePathsSectionName, "Rules", "INI/Rules.ini");
            FirestormIniPath = constantsIni.GetStringValue(FilePathsSectionName, "Firestorm", "INI/Enhance.ini");
            ArtIniPath = constantsIni.GetStringValue(FilePathsSectionName, "Art", "INI/Art.ini");
            FirestormArtIniPath = constantsIni.GetStringValue(FilePathsSectionName, "ArtFS", "INI/ArtE.ini");
            AIIniPath = constantsIni.GetStringValue(FilePathsSectionName, "AI", "INI/AI.ini");
            FirestormAIIniPath = constantsIni.GetStringValue(FilePathsSectionName, "AIFS", "INI/AIE.ini");
            TutorialIniPath = constantsIni.GetStringValue(FilePathsSectionName, "Tutorial", "INI/Tutorial.ini");
            ThemeIniPath = constantsIni.GetStringValue(FilePathsSectionName, "Theme", "INI/Theme.ini");
            EvaIniPath = constantsIni.GetStringValue(FilePathsSectionName, "EVA", "INI/Eva.ini");
            SoundIniPath = constantsIni.GetStringValue(FilePathsSectionName, "Sound", "INI/Sound01.ini");

            InitUIConstants();
        }

        public static void InitUIConstants()
        {
            IniFile uiConstantsIni = Helpers.ReadConfigINI("UI/UIConstants.ini");

            UITreeViewLineHeight = uiConstantsIni.GetIntValue("UI", nameof(UITreeViewLineHeight), UITreeViewLineHeight);
            UIDefaultSidebarWidth = uiConstantsIni.GetIntValue("UI", nameof(UIDefaultSidebarWidth), UIDefaultSidebarWidth);
            UITileSetListWidth = uiConstantsIni.GetIntValue("UI", nameof(UITileSetListWidth), UITileSetListWidth);
        }
    }
}
