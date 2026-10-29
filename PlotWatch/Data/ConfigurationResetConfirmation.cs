namespace PlotWatch.Data;

public sealed record ConfigurationResetResult(bool Succeeded, string Message);

/// <summary>Requires explicit confirmation before invoking a destructive configuration reset.</summary>
public sealed class ConfigurationResetConfirmation(Func<string?> resetAndSave)
{
    public bool IsPending { get; private set; }
    public string? LastError { get; private set; }

    public void Request()
    {
        IsPending = true;
        LastError = null;
    }

    public void Cancel()
    {
        IsPending = false;
        LastError = null;
    }

    public ConfigurationResetResult Confirm()
    {
        if (!IsPending)
            return new ConfigurationResetResult(false, "Reset was not requested.");

        try
        {
            var error = resetAndSave();
            if (error is not null)
            {
                LastError = error;
                return new ConfigurationResetResult(false, error);
            }

            IsPending = false;
            LastError = null;
            return new ConfigurationResetResult(true, "Configuration reset and saved.");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return new ConfigurationResetResult(false, ex.Message);
        }
    }
}
