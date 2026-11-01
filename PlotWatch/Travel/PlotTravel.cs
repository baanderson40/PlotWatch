global using HousingTravelAddress = (string Name, int World, int City, int Ward, int PropertyType, int Plot, int Apartment, bool ApartmentSubdivision, bool AliasEnabled, string Alias);

namespace PlotWatch.Travel;

public sealed record PlotDestination(int? WorldId, string WorldName, int? DistrictId, int? Ward, int? Plot)
{
    public string Label => $"{WorldName} / {DistrictId switch
    {
        339 => "Mist", 340 => "The Lavender Beds", 341 => "The Goblet",
        641 => "Shirogane", 979 => "Empyreum", _ => "Unknown district",
    }} / W{Ward} P{Plot}";
}

public static class PlotTravelPolicy
{
    // Lifestream ResidentialAetheryteKind values, not territory IDs.
    public static int? GetCity(int? districtId) => districtId switch
    {
        339 => 8, 340 => 2, 341 => 9, 641 => 111, 979 => 70, _ => null,
    };

    public static string? Validate(PlotDestination destination)
    {
        if (destination.WorldId is null or <= 0 || string.IsNullOrWhiteSpace(destination.WorldName))
            return "World is missing or invalid.";
        if (GetCity(destination.DistrictId) is null) return "Housing district is unsupported.";
        if (destination.Ward is null or < 1 or > 30) return "Ward must be 1–30.";
        if (destination.Plot is null or < 1 or > 60) return "Plot must be 1–60.";
        return null;
    }

    public static HousingTravelAddress BuildAddress(PlotDestination destination)
    {
        if (Validate(destination) is { } error) throw new ArgumentException(error);
        return ($"PlotWatch: {destination.WorldName}", destination.WorldId!.Value,
            GetCity(destination.DistrictId)!.Value, destination.Ward!.Value,
            0, destination.Plot!.Value, 1, false, false, "");
    }
}

public interface IPlotTravelBackend
{
    bool Available { get; }
    bool IsBusy();
    bool CanVisit(int worldId);
    void Go(PlotDestination destination);
    void Abort();
}

public sealed class PlotTravel(IPlotTravelBackend backend, Func<bool> isLoggedIn, Action<bool, string> log)
{
    public bool Available { get; private set; }
    public bool Busy { get; private set; }
    public bool OwnsRequest { get; private set; }
    public PlotDestination? Destination { get; private set; }
    public string Status { get; private set; } = "Travel uses optional Lifestream.";
    public bool CanStop => Available && Busy && OwnsRequest;

    public void Refresh()
    {
        try
        {
            Available = backend.Available;
            Busy = Available && backend.IsBusy();
            if (OwnsRequest && (!Available || !isLoggedIn() || !Busy))
            {
                OwnsRequest = false;
                Status = "Lifestream travel is no longer active. Arrival not verified.";
            }
        }
        catch (Exception)
        {
            Available = Busy = OwnsRequest = false;
            Status = "Lifestream status unavailable. Arrival not verified.";
        }
    }

    public string? GetDisabledReason(PlotDestination destination)
    {
        if (PlotTravelPolicy.Validate(destination) is { } invalid) return invalid;
        if (!isLoggedIn()) return "Log in before traveling.";
        if (!Available) return "Lifestream is unavailable or its travel IPC is incompatible.";
        if (Busy) return "Lifestream is busy.";
        try
        {
            return backend.CanVisit(destination.WorldId!.Value) ? null : "Lifestream cannot visit this world.";
        }
        catch (Exception) { return "Lifestream destination check failed."; }
    }

    public bool Start(PlotDestination destination)
    {
        Refresh(); // Recheck queued clicks before issuing any travel action.
        if (GetDisabledReason(destination) is { } reason)
        {
            Status = reason;
            log(false, $"Travel rejected: {reason}");
            return false;
        }
        try
        {
            backend.Go(destination);
            Destination = destination;
            OwnsRequest = true;
            Status = "Travel request sent to Lifestream. Arrival not verified.";
            log(true, $"Travel requested: {destination.Label}.");
            return true;
        }
        catch (Exception)
        {
            Status = "Lifestream travel request failed. Check Lifestream for any active task.";
            log(false, Status);
            return false;
        }
    }

    public bool Stop()
    {
        Refresh();
        if (!CanStop) return false;
        try
        {
            backend.Abort();
            OwnsRequest = false;
            Status = "Stop requested from Lifestream. Arrival not verified.";
            log(true, Status);
            return true;
        }
        catch (Exception)
        {
            Status = "Lifestream stop failed; use Lifestream to stop travel.";
            log(false, Status);
            return false;
        }
    }
}
