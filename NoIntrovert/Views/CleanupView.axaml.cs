using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.LogicalTree;
using NoIntrovert.Controls;

namespace NoIntrovert.Views;

public partial class CleanupView : UserControl
{
    public CleanupView()
    {
        InitializeComponent();
        // The run button pulses only when the plan has something to do: with nothing to organize it's off, and
        // a pulsing button that can't be clicked would say the opposite. (The list panel pulses it whenever
        // there are rows; Organize adds its plan to that.)
        foreach (var glow in this.GetLogicalDescendants().OfType<GlowBorder>().Where(g => g.Mode == GlowBorderMode.Pulse))
            glow.Bind(GlowBorder.IsActiveProperty, new MultiBinding
            {
                Converter = BoolConverters.And,
                Bindings = { new Binding("HasItems"), new Binding("IsIdle"), new Binding("HasWorkToDo"), new Binding("!IsBusy") },
            });
    }
}
