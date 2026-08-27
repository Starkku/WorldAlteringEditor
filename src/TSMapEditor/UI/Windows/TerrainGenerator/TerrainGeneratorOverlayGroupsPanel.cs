using MapEditorLibrary;
using MapEditorLibrary.Models;
using MapEditorLibrary.Mutations.Classes;
using Rampastring.Tools;
using Rampastring.XNAUI;
using Rampastring.XNAUI.XNAControls;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TSMapEditor.UI.Controls;

namespace TSMapEditor.UI.Windows.TerrainGenerator;

/// <summary>
/// A panel that allows the user to customize how the terrain 
/// generator places overlay on the map.
/// </summary>
public class TerrainGeneratorOverlayGroupsPanel : EditorPanel
{
    private const int MaxOverlayGroupCount = 8;

    public TerrainGeneratorOverlayGroupsPanel(WindowManager windowManager, Map map) : base(windowManager)
    {
        this.map = map;
    }

    private readonly Map map;

    private EditorTextBox[] overlayNames;
    private EditorTextBox[] frameIndices;
    private EditorNumberTextBox[] overlayGroupOpenChances;
    private EditorNumberTextBox[] overlayGroupOccupiedChances;

    public override void Initialize()
    {
        overlayNames = new EditorTextBox[MaxOverlayGroupCount];
        frameIndices = new EditorTextBox[MaxOverlayGroupCount];
        overlayGroupOpenChances = new EditorNumberTextBox[MaxOverlayGroupCount];
        overlayGroupOccupiedChances = new EditorNumberTextBox[MaxOverlayGroupCount];

        int y = Constants.UIEmptyTopSpace;

        for (int i = 0; i < MaxOverlayGroupCount; i++)
        {
            var lblOverlayType = new XNALabel(WindowManager);
            lblOverlayType.Name = nameof(lblOverlayType) + i;
            lblOverlayType.X = Constants.UIEmptySideSpace;
            lblOverlayType.Y = y;
            lblOverlayType.FontIndex = Constants.UIBoldFont;
            lblOverlayType.Text = string.Format(Translate(this, "OverlayNameType", "Overlay Type Name (Group #{0})"), i + 1);
            AddChild(lblOverlayType);

            var selOverlayType = new EditorTextBox(WindowManager);
            selOverlayType.Name = nameof(selOverlayType) + i;
            selOverlayType.X = lblOverlayType.X;
            selOverlayType.Y = lblOverlayType.Bottom + Constants.UIVerticalSpacing;
            selOverlayType.Width = 200;
            AddChild(selOverlayType);
            overlayNames[i] = selOverlayType;

            var lblFrameIndices = new XNALabel(WindowManager);
            lblFrameIndices.Name = nameof(lblFrameIndices) + i;
            lblFrameIndices.X = selOverlayType.Right + Constants.UIHorizontalSpacing;
            lblFrameIndices.Y = lblOverlayType.Y;
            lblFrameIndices.Text = Translate(this, "OverlayIndices", "Indexes of frames to place (leave blank for all)");
            AddChild(lblFrameIndices);

            var tbFrameIndices = new EditorTextBox(WindowManager);
            tbFrameIndices.Name = nameof(selOverlayType) + i;
            tbFrameIndices.X = lblFrameIndices.X;
            tbFrameIndices.Y = selOverlayType.Y;
            tbFrameIndices.Width = 280;
            AddChild(tbFrameIndices);
            frameIndices[i] = tbFrameIndices;

            var lblOpenChance = new XNALabel(WindowManager);
            lblOpenChance.Name = nameof(lblOpenChance) + i;
            lblOpenChance.X = tbFrameIndices.Right + Constants.UIHorizontalSpacing;
            lblOpenChance.Y = lblOverlayType.Y;
            lblOpenChance.Text = Translate(this, "OpenCellChance", "Open cell chance:");
            AddChild(lblOpenChance);

            var tbOpenChance = new EditorNumberTextBox(WindowManager);
            tbOpenChance.Name = nameof(tbOpenChance) + i;
            tbOpenChance.X = lblOpenChance.X;
            tbOpenChance.Y = selOverlayType.Y;
            tbOpenChance.AllowDecimals = true;
            tbOpenChance.Width = 120;
            AddChild(tbOpenChance);
            overlayGroupOpenChances[i] = tbOpenChance;

            var lblOccupiedChance = new XNALabel(WindowManager);
            lblOccupiedChance.Name = nameof(lblOccupiedChance) + i;
            lblOccupiedChance.X = tbOpenChance.Right + Constants.UIHorizontalSpacing;
            lblOccupiedChance.Y = lblOpenChance.Y;
            lblOccupiedChance.Text = Translate(this, "OccupiedCellChance", "Occupied cell chance:");
            AddChild(lblOccupiedChance);

            var tbOccupiedChance = new EditorNumberTextBox(WindowManager);
            tbOccupiedChance.Name = nameof(tbOpenChance) + i;
            tbOccupiedChance.X = lblOccupiedChance.X;
            tbOccupiedChance.Y = selOverlayType.Y;
            tbOccupiedChance.AllowDecimals = true;
            tbOccupiedChance.Width = Width - tbOccupiedChance.X - Constants.UIEmptySideSpace;
            AddChild(tbOccupiedChance);
            overlayGroupOccupiedChances[i] = tbOccupiedChance;

            y = tbOccupiedChance.Bottom + Constants.UIEmptyTopSpace;
        }

        base.Initialize();
    }

    public List<TerrainGeneratorOverlayGroup> GetOverlayGroups()
    {
        var overlayGroups = new List<TerrainGeneratorOverlayGroup>();

        for (int i = 0; i < overlayNames.Length; i++)
        {
            string overlayTypeName = overlayNames[i].Text;
            if (string.IsNullOrWhiteSpace(overlayTypeName))
                continue;

            var overlayType = map.Rules.OverlayTypes.Find(ot => ot.ININame == overlayTypeName);
            if (overlayType == null)
            {
                EditorMessageBox.Show(WindowManager, 
                    Translate(this, "GeneratorConfigError.OverlayNotFound.Title", "Generator Config Error"),
                    string.Format(Translate(this, "GeneratorConfigError.OverlayNotFound.Description", 
                        "An overlay type named '{0}' does not exist! Make sure you typed the overlay type's INI name and spelled it correctly."), overlayTypeName), 
                    MessageBoxButtons.OK);
                return null;
            }

            List<int> frameIndexes = null;
            string frameIndexesText = frameIndices[i].Text.Trim();
            if (!string.IsNullOrEmpty(frameIndexesText))
            {
                string[] parts = frameIndexesText.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                frameIndexes = parts.Select(str => Conversions.IntFromString(str, -1)).ToList();
                int invalidElement = frameIndexes.Find(index => index <= -1 || index >= map.TheaterInstance.GetOverlayFrameCount(overlayType));

                if (invalidElement != 0) // this can never be 0 if an invalid element exists, because each valid overlay has at least 1 frame
                {
                    EditorMessageBox.Show(WindowManager, 
                        Translate(this, "GeneratorConfigError.InvalidFrame.Title", "Generator Config Error"),
                        string.Format(Translate(this, "GeneratorConfigError.InvalidFrame.Description",
                            "Frame '{0}' does not exist in overlay type '{1}'!"), invalidElement, overlayType.ININame),
                        MessageBoxButtons.OK);
                    return null;
                }
            }

            var overlayGroup = new TerrainGeneratorOverlayGroup(overlayType, frameIndexes,
                overlayGroupOpenChances[i].DoubleValue,
                overlayGroupOccupiedChances[i].DoubleValue);

            overlayGroups.Add(overlayGroup);
        }

        return overlayGroups;
    }

    public void LoadConfig(TerrainGeneratorConfiguration configuration)
    {
        for (int i = 0; i < configuration.OverlayGroups.Count && i < MaxOverlayGroupCount; i++)
        {
            var overlayGroup = configuration.OverlayGroups[i];

            overlayNames[i].Text = overlayGroup.OverlayType.ININame;

            if (overlayGroup.FrameIndices == null)
                frameIndices[i].Text = string.Empty;
            else
                frameIndices[i].Text = string.Join(",", overlayGroup.FrameIndices.Select(index => index.ToString(CultureInfo.InvariantCulture)));

            overlayGroupOpenChances[i].DoubleValue = overlayGroup.OpenChance;
            overlayGroupOccupiedChances[i].DoubleValue = overlayGroup.OverlapChance;
        }

        for (int i = configuration.OverlayGroups.Count; i < overlayNames.Length; i++)
        {
            overlayNames[i].Text = string.Empty;
            frameIndices[i].Text = string.Empty;
            overlayGroupOpenChances[i].DoubleValue = 0.0;
            overlayGroupOccupiedChances[i].DoubleValue = 0.0;
        }
    }
}
