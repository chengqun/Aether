using System;
using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

public partial class ExpertEditView : UserControl
{
    private Expander[]? _accordions;

    public ExpertEditView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        _accordions = new[]
        {
            this.FindControl<Expander>("ExpBasic")!,
            this.FindControl<Expander>("ExpPrompt")!,
            this.FindControl<Expander>("ExpModel")!,
            this.FindControl<Expander>("ExpSample")!,
            this.FindControl<Expander>("ExpSkills")!,
        };
        foreach (var exp in _accordions)
        {
            exp.PropertyChanged += (s, args) =>
            {
                if (args.Property == Expander.IsExpandedProperty && (bool)args.NewValue!)
                    foreach (var other in _accordions!)
                        if (!ReferenceEquals(other, s)) other.IsExpanded = false;
            };
        }
    }
}
