using Terminal.Gui.ViewBase;

namespace Vega.TerminalUI;

internal static class TerminalUiFixedLayout
{
    public static void Apply(View desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        foreach (View panel in desktop.SubViews)
        {
            panel.Arrangement = ViewArrangement.Resizable;
        }
    }
}
