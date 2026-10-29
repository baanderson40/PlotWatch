namespace PlotWatch.Commands;

/// <summary>Tracks one command registration and prevents removal after ownership changes.</summary>
public sealed class OwnedCommandRegistration
{
    private readonly Func<bool> add;
    private readonly Func<bool> isCurrentHandlerOurs;
    private readonly Func<bool> exists;
    private readonly Action remove;

    public string Name { get; }
    public bool RegisteredByUs { get; private set; }

    public OwnedCommandRegistration(
        string name,
        Func<bool> add,
        Func<bool> isCurrentHandlerOurs,
        Func<bool> exists,
        Action remove)
    {
        Name = name;
        this.add = add;
        this.isCurrentHandlerOurs = isCurrentHandlerOurs;
        this.exists = exists;
        this.remove = remove;
    }

    public bool Register()
    {
        RegisteredByUs = add();
        return RegisteredByUs;
    }

    public string GetStatus()
    {
        if (RegisteredByUs)
        {
            if (isCurrentHandlerOurs())
                return "Registered by PlotWatch";
            return exists() ? "Replaced by another handler" : "Missing after registration";
        }

        return exists() ? "Conflict (not owned by PlotWatch)" : "Not registered";
    }

    public void Dispose()
    {
        if (!RegisteredByUs)
            return;

        if (isCurrentHandlerOurs())
            remove();

        RegisteredByUs = false;
    }
}
