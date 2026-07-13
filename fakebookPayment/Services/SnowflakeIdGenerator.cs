namespace Fakebook.Payment.Services;

public interface IIdGenerator { long NextId(); }

public sealed class SnowflakeIdGenerator(int workerId) : IIdGenerator
{
    private const long EpochMilliseconds = 1_735_689_600_000L;
    private readonly object _lock = new();
    private long _lastTimestamp = -1;
    private long _sequence;

    public long NextId()
    {
        lock (_lock)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (timestamp < _lastTimestamp) throw new InvalidOperationException("System clock moved backwards.");
            if (timestamp == _lastTimestamp)
            {
                _sequence = (_sequence + 1) & 4095;
                if (_sequence == 0)
                    do { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } while (timestamp <= _lastTimestamp);
            }
            else _sequence = 0;

            _lastTimestamp = timestamp;
            return ((timestamp - EpochMilliseconds) << 22) | ((long)workerId << 12) | _sequence;
        }
    }
}

