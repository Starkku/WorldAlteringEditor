using MapEditorLibrary;
using MapEditorLibrary.Misc;
using Rampastring.XNAUI.Input;
using Rampastring.XNAUI.XNAControls;
using System;
using System.Collections.Generic;
using System.Linq;
using TSMapEditor.UI.Controls;

namespace TSMapEditor.UI;

public static class UIHelpers
{
    public static void AddSearchTipsBoxToControl(XNAControl control)
    {
        var lblSearchTips = new XNALabel(control.WindowManager);
        lblSearchTips.Name = nameof(lblSearchTips);
        lblSearchTips.Text = "?";
        lblSearchTips.X = control.Width - Constants.UIEmptySideSpace - lblSearchTips.Width;
        lblSearchTips.Y = (control.Height - lblSearchTips.Height) / 2;
        control.AddChild(lblSearchTips);
        var tooltip = new ToolTip(control.WindowManager, lblSearchTips);
        tooltip.Text = Translate("UIHelpers.AddSearchTipsBoxToControl.SearchTip", "Search Tips\r\n\r\nWith the text box activated:\r\n- Press ENTER to move to next match in list\r\n- Press ESC to clear search query");
        control.ClientRectangleUpdated += Control_ClientRectangleUpdated;
        control.OnKilled += Control_OnKilled;
    }

    private static void Control_ClientRectangleUpdated(object sender, EventArgs e)
    {
        XNAControl senderControl = sender as XNAControl;
        foreach (var item in senderControl.Children)
        {
            if (item.Name == "lblSearchTips")
            {
                item.X = senderControl.Width - Constants.UIEmptySideSpace - item.Width;
                break;
            }
        }
    }

    private static void Control_OnKilled(object sender, EventArgs e)
    {
        XNAControl senderControl = sender as XNAControl;
        senderControl.ClientRectangleUpdated -= Control_ClientRectangleUpdated;
        senderControl.OnKilled -= Control_OnKilled;
    }

    public static T GetScrollItem<T>(List<T> list, T current, Cursor cursor, bool allowSelectionIfNull)
    {
        if (current == null)
        {
            if (list.Count > 0 && allowSelectionIfNull)
            {
                return current;
            }

            return default(T);
        }

        int index = list.IndexOf(current);

        // Check for possible error condition
        if (index < 0)
        {
            return default(T);
        }

        if (index > 0 && cursor.ScrollWheelValue > 0)
        {
            return list[index - 1];
        }

        if (index < list.Count - 1 && cursor.ScrollWheelValue < 0)
        {
            return list[index + 1];
        }

        return current;
    }

    public static void AddColorOptionsToDropDown(NamedColor[] colors, XNADropDown dropDown)
    {
        foreach (var supportedColor in colors)
        {
            dropDown.AddItem(new XNADropDownItem()
            {
                Text = Translate("NamedColors." + supportedColor.Name, supportedColor.Name),
                TextColor = supportedColor.Value,
                Tag = supportedColor.Name
            });
        }
    }

    public static void AutoAssignTextBoxNextControls(XNAControl parentControl)
    {
        var textBoxes = parentControl.Children.OfType<XNATextBox>().ToList();

        for (int i = 0; i < textBoxes.Count; i++)
        {
            var box = textBoxes[i];

            if (box.NextControl != null)
                continue;

            var next = FindStartingFromIndex(textBoxes, i + 1, tb => tb.PreviousControl == null && tb.Y == box.Y);

            if (next == null)
                next = FindStartingFromIndex(textBoxes, i + 1, tb => tb.Y > box.Y);

            if (next != null)
            {
                box.NextControl = next;
                next.PreviousControl = box;
            }
        }
    }

    private static T FindStartingFromIndex<T>(List<T> list, int startIndex, Func<T, bool> predicate)
    {
        for (int i = startIndex; i < list.Count; i++)
        {
            if (predicate(list[i]))
                return list[i];
        }

        return default;
    }
}
