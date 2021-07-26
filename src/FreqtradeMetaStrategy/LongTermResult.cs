using System;

namespace FreqtradeMetaStrategy
{
    public class LongTermResult
    {
        public string Strategy { get; set; }
        public IntervalResult[] Results { get; set; } = Array.Empty<IntervalResult>();
    }

    public class IntervalResult
    {
        public string StartDate { get; set; }
        public string EndDate { get; set; }
        public double Profit { get; set; }
        public double DrawDown { get; set; }
        public double MarketChange { get; set; }
        public PairProfit[] Pairs { get; set; } = Array.Empty<PairProfit>();
    }

    public class PairProfit
    {
        public string Pair { get; set; }
        public double Profit { get; set; }
    }
}