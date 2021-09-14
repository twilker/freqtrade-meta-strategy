using System;
using System.Collections.Generic;
using System.Linq;

namespace FreqtradeMetaStrategy
{
    public class BackTestingResult
    {
        public BackTestingResult(string strategy, double profitPerDay, double tradesPerDay, Dictionary<string, double> pairsProfit, bool isUnstableStake, double drawDown, double marketChange, double totalProfit)
        {
            Strategy = strategy;
            ProfitPerDay = profitPerDay;
            TradesPerDay = tradesPerDay;
            PairsProfit = pairsProfit;
            IsUnstableStake = isUnstableStake;
            DrawDown = drawDown;
            MarketChange = marketChange;
            TotalProfit = totalProfit;
        }

        public string Strategy { get; }
        public bool IsUnstableStake { get; }
        public double ProfitPerDay { get; }
        public double TradesPerDay { get; }
        public double DrawDown { get; }
        public double MarketChange { get; }
        public double TotalProfit { get; }
        public Dictionary<string, double> PairsProfit { get; }
        public BackTestTrade[] Trades { get; set; }

        public override string ToString()
        {
            return $"{nameof(Strategy)}: {Strategy}, {nameof(IsUnstableStake)}: {IsUnstableStake}, {nameof(ProfitPerDay)}: {ProfitPerDay}, {nameof(TradesPerDay)}: {TradesPerDay}, {nameof(DrawDown)}: {DrawDown}, {nameof(MarketChange)}: {MarketChange}, {nameof(TotalProfit)}: {TotalProfit}, {nameof(PairsProfit)}: {Environment.NewLine}{string.Join(Environment.NewLine, PairsProfit.Select(kv => $"{kv.Key}: {kv.Value}"))}";
        }
    }
}