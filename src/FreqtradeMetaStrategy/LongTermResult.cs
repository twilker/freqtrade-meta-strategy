using System;

namespace FreqtradeMetaStrategy
{
    public class LongTermResult
    {
        public string Strategy { get; set; }
        public IntervalResult[] Results { get; set; } = Array.Empty<IntervalResult>();
    }
}