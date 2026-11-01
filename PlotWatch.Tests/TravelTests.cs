using PlotWatch.Travel;

internal static class TravelTests
{
    public static readonly (string Name, Action Test)[] Cases =
    [
        ("plot travel maps all districts to Lifestream cities", Districts),
        ("plot travel validates addresses and preserves one-based subdivision plots", Addresses),
        ("plot travel blocks unavailable busy logged-out and unsupported requests", Guards),
        ("plot travel rechecks state when queued requests execute", Recheck),
        ("plot travel tracks request without claiming arrival", Lifecycle),
        ("plot travel cannot stop unrelated Lifestream tasks", Unrelated),
        ("plot travel handles IPC failures and plugin unload", Failures),
    ];

    private static PlotDestination Destination => new(65, "Midgardsormr", 339, 5, 11);
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void Districts()
    {
        foreach (var (district, city) in new[] { (339, 8), (340, 2), (341, 9), (641, 111), (979, 70) })
            Equal<int?>(city, PlotTravelPolicy.GetCity(district));
        Equal<int?>(null, PlotTravelPolicy.GetCity(null));
        Equal<int?>(null, PlotTravelPolicy.GetCity(999));
    }

    private static void Addresses()
    {
        foreach (var (ward, plot) in new[] { (1, 1), (5, 11), (30, 31), (30, 60) })
        {
            var address = PlotTravelPolicy.BuildAddress(Destination with { Ward = ward, Plot = plot });
            Equal(ward, address.Ward);
            Equal(plot, address.Plot);
            Equal(65, address.World);
            Equal(8, address.City);
            Equal(0, address.PropertyType);
            Equal(false, address.ApartmentSubdivision);
            Equal(false, address.AliasEnabled);
        }
        foreach (var invalid in new[]
        {
            Destination with { WorldId = null }, Destination with { WorldId = 0 },
            Destination with { WorldName = "" }, Destination with { DistrictId = null },
            Destination with { DistrictId = 1 }, Destination with { Ward = null },
            Destination with { Ward = 0 }, Destination with { Ward = 31 },
            Destination with { Plot = null }, Destination with { Plot = 0 }, Destination with { Plot = 61 },
        })
            Equal(true, PlotTravelPolicy.Validate(invalid) is not null);
    }

    private static void Guards()
    {
        var backend = new FakeBackend();
        var loggedIn = true;
        var travel = new PlotTravel(backend, () => loggedIn, (_, _) => { });
        foreach (var guard in new[] { "unavailable", "busy", "unsupported", "logout" })
        {
            backend.Available = guard != "unavailable";
            backend.Busy = guard == "busy";
            backend.Supported = guard != "unsupported";
            loggedIn = guard != "logout";
            Equal(false, travel.Start(Destination));
            Equal(0, backend.GoCalls);
        }
        loggedIn = true;
        backend.Busy = false;
        backend.Supported = true;
        Equal(false, travel.Start(Destination with { Plot = 0 }));
        Equal(0, backend.GoCalls);
    }

    private static void Recheck()
    {
        var backend = new FakeBackend();
        var travel = new PlotTravel(backend, () => true, (_, _) => { });
        travel.Refresh();
        Equal<string?>(null, travel.GetDisabledReason(Destination));
        backend.Busy = true;
        Equal(false, travel.Start(Destination));
        Equal(0, backend.GoCalls);
    }

    private static void Lifecycle()
    {
        var backend = new FakeBackend();
        var travel = new PlotTravel(backend, () => true, (_, _) => { });
        Equal(true, travel.Start(Destination));
        Equal(Destination, backend.LastDestination);
        travel.Refresh();
        Equal(true, travel.CanStop);
        Equal(true, travel.Status.Contains("Arrival not verified"));
        backend.Busy = false;
        travel.Refresh();
        Equal(false, travel.OwnsRequest);
        Equal(true, travel.Status.Contains("Arrival not verified"));
        Equal(true, travel.Start(Destination));
        Equal(true, travel.Stop());
        Equal(1, backend.AbortCalls);
    }

    private static void Unrelated()
    {
        var backend = new FakeBackend { Busy = true };
        var travel = new PlotTravel(backend, () => true, (_, _) => { });
        travel.Refresh();
        Equal(false, travel.CanStop);
        Equal(false, travel.Stop());
        Equal(0, backend.AbortCalls);
    }

    private static void Failures()
    {
        var backend = new FakeBackend();
        var travel = new PlotTravel(backend, () => true, (_, _) => { });
        backend.ThrowCheck = true;
        Equal(false, travel.Start(Destination));
        backend.ThrowCheck = false;
        backend.ThrowGo = true;
        Equal(false, travel.Start(Destination));
        Equal(false, travel.OwnsRequest);
        backend.ThrowGo = false;
        Equal(true, travel.Start(Destination));
        backend.ThrowAbort = true;
        Equal(false, travel.Stop());
        Equal(true, travel.Status.Contains("stop failed"));
        backend.Available = false;
        travel.Refresh();
        Equal(false, travel.CanStop);
        Equal(false, travel.OwnsRequest);
        backend.Available = true;
        backend.ThrowBusy = true;
        travel.Refresh();
        Equal(false, travel.Available);
    }

    private sealed class FakeBackend : IPlotTravelBackend
    {
        public bool Available { get; set; } = true;
        public bool Busy, ThrowBusy, ThrowCheck, ThrowGo, ThrowAbort;
        public bool Supported = true;
        public int GoCalls, AbortCalls;
        public PlotDestination? LastDestination;
        public bool IsBusy() => ThrowBusy ? throw new Exception() : Busy;
        public bool CanVisit(int worldId) => ThrowCheck ? throw new Exception() : Supported;
        public void Go(PlotDestination destination)
        {
            if (ThrowGo) throw new Exception();
            GoCalls++;
            LastDestination = destination;
            Busy = true;
        }
        public void Abort()
        {
            if (ThrowAbort) throw new Exception();
            AbortCalls++;
            Busy = false;
        }
    }
}
