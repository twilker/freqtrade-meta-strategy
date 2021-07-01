using System;
using System.Collections.Generic;
using System.Linq;

namespace FreqtradeMetaStrategy
{
    public class BackTestingResult
    {
        public BackTestingResult(string strategy, double profitPerDay, double tradesPerDay, Dictionary<string, double> pairsProfit, bool isUnstableStake)
        {
            Strategy = strategy;
            ProfitPerDay = profitPerDay;
            TradesPerDay = tradesPerDay;
            PairsProfit = pairsProfit;
            IsUnstableStake = isUnstableStake;
        }

        public string Strategy { get; }
        public bool IsUnstableStake { get; }
        public double ProfitPerDay { get; }
        public double TradesPerDay { get; }
        public Dictionary<string, double> PairsProfit { get; }

        public override string ToString()
        {
            return $"{nameof(Strategy)}: {Strategy}, {nameof(IsUnstableStake)}: {IsUnstableStake}, {nameof(ProfitPerDay)}: {ProfitPerDay}, {nameof(TradesPerDay)}: {TradesPerDay}, {nameof(PairsProfit)}: {Environment.NewLine}{string.Join(Environment.NewLine, PairsProfit.Select(kv => $"{kv.Key}: {kv.Value}"))}";
        }
    }
}