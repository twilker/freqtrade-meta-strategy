using System;

namespace FreqtradeMetaStrategy
{
    public class BackTestTrade
    {
        public BackTestTrade(string pair, DateTime openTime)
        {
            Pair = pair;
            OpenTime = openTime;
        }

        public string Pair { get; }
        public DateTime OpenTime { get; }
    }
}