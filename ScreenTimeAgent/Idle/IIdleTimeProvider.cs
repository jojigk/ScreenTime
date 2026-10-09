namespace ScreenTimeAgent.Idle;

public interface IIdleTimeProvider
{
    TimeSpan GetIdleTime();
}
