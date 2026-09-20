namespace BilinguaFlow.Infrastructure;

public interface ISystemClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
