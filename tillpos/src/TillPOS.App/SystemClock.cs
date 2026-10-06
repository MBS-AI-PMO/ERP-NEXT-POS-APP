using TillPOS.Presentation;

namespace TillPOS.App;

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
