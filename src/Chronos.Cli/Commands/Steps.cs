namespace Chronos.Cli.Commands;

/// <summary>Exit codes of the setup commands, and the way they run a step.</summary>
/// <remarks>
/// The MSI's <c>ChronosRegister</c> is <c>Return="check"</c>: any non-zero code is error 1722 and a
/// rollback. So <c>install</c> on a machine that already has the service updates it and returns 0.
/// </remarks>
internal static class Steps
{
    public const int Done = 0;

    public const int NotElevated = 2;

    public const int Failed = 4;

    /// <summary>Runs a step and reports failure on <paramref name="error"/>. Catches every exception on purpose: a stack trace from an MSI custom action helps nobody.</summary>
    public static bool Run(string failure, Action step, TextWriter error)
    {
        try
        {
            step();

            return true;
        }
        catch (Exception exception)
        {
            error.WriteLine($"{failure}: {exception.Message}");

            return false;
        }
    }
}
