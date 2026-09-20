namespace BilinguaFlow.App.ViewModels;

public sealed record SelectionOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
