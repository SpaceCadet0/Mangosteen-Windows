using System.Windows.Automation;
using static Mangosteen.Platform.Native;

namespace Mangosteen.Platform;

/// <summary>
/// Does what Explorer does on a right-button press, which Mangosteen swallows: selects the item under the
/// cursor unless it is already selected, so a right-click on one of several selected files keeps them all.
/// Works through UI Automation, which both Explorer's item view and the desktop list view support.
/// </summary>
internal static class ShellHitTest
{
    public enum Outcome
    {
        /// <summary>The item was already selected; the selection is left as it is.</summary>
        AlreadySelected,
        /// <summary>The item was not selected; now it is the only selected item.</summary>
        Selected,
        /// <summary>No item under the cursor (empty space, a group or column header).</summary>
        NoItem,
        Failed,
    }

    /// <param name="Detail">The UI Automation elements walked, for the log.</param>
    public sealed record Result(Outcome Outcome, string Detail);

    /// <summary>The first UI Automation call in a process is slow; doing it at startup hides that.</summary>
    public static void WarmUp()
    {
        try
        {
            _ = AutomationElement.RootElement.Current.ClassName;
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
        }
    }

    public static Result SelectItemAt(POINT point)
    {
        var chain = new List<string>();
        Result Done(Outcome outcome, string? note = null) =>
            new(outcome, string.Join(" < ", chain) + (note is null ? "" : $" ({note})"));
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            var walker = TreeWalker.ControlViewWalker;
            // From the text or icon under the cursor up to its list item (details view rows have cells inside).
            for (var depth = 0; element is not null && depth < 8; depth++)
            {
                var current = element.Current;
                var type = current.ControlType;
                chain.Add($"{type.ProgrammaticName.Replace("ControlType.", "")}/{current.ClassName}/{current.FrameworkId}");
                if (type == ControlType.ListItem || type == ControlType.DataItem)
                {
                    if (!element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
                    {
                        return Done(Outcome.Failed, "no SelectionItemPattern");
                    }
                    var item = (SelectionItemPattern)pattern;
                    if (item.Current.IsSelected)
                    {
                        return Done(Outcome.AlreadySelected);
                    }
                    item.Select();
                    return Done(Outcome.Selected);
                }
                if (type == ControlType.List || type == ControlType.Window || type == ControlType.Pane)
                {
                    return Done(Outcome.NoItem);
                }
                element = walker.GetParent(element);
            }
            return Done(Outcome.NoItem, "no list item above the point");
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return Done(Outcome.Failed, $"{error.GetType().Name}: {error.Message}");
        }
    }
}
