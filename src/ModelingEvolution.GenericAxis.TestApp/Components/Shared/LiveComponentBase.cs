using Microsoft.AspNetCore.Components;

namespace ModelingEvolution.GenericAxis.TestApp.Components.Shared;

/// <summary>
/// A page that re-renders every 100 ms from immutable snapshots (design § Test app), so no page iterates a collection
/// that changes under it.
/// </summary>
public abstract class LiveComponentBase : ComponentBase, IDisposable
{
    private Timer? _timer;

    protected virtual TimeSpan RefreshInterval => TimeSpan.FromMilliseconds(100);

    protected override void OnInitialized()
    {
        _timer = new Timer(_ => InvokeAsync(StateHasChanged), null, RefreshInterval, RefreshInterval);
    }

    public virtual void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        GC.SuppressFinalize(this);
    }
}
