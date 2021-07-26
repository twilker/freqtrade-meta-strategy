using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FreqtradeMetaStrategy
{
    public static class ToolBox
    {
        public static BackTestingResult EvaluateBackTestingResult(string output, string strategyName, int daysCount, bool isUnstableStake)
        {
            if (output.Contains("No trades made."))
            {
                return new BackTestingResult(strategyName, 0, 0, new Dictionary<string, double>(), isUnstableStake, 0,
                                             0, 0);
            }
            List<string> outputSplit = output.Split(Environment.NewLine,
                                                    StringSplitOptions.TrimEntries |
                                                    StringSplitOptions.RemoveEmptyEntries)
                                             .ToList();
            int startResultLine = outputSplit.IndexOf($"Result for strategy {strategyName}");
            if (startResultLine < 0)
            {
                throw new InvalidOperationException(
                    $"The below output does not contain the expected line 'Result for strategy {strategyName}'{Environment.NewLine}{output}");
            }

            int current = startResultLine + 4;
            Match pairLineMatch = BackTestingPairProfit.Match(outputSplit[current]);
            Dictionary<string, double> pairProfits = new();
            while (pairLineMatch.Success)
            {
                pairProfits.Add(pairLineMatch.Groups["pair"].Value,
                                double.Parse(pairLineMatch.Groups["total_profit"].Value,
                                             ConfigCulture));
                current++;
                pairLineMatch = BackTestingPairProfit.Match(outputSplit[current]);
            }

            Match tradesPerDayMatch = outputSplit.Skip(startResultLine)
                                                 .Select<string, Match>(l => BackTestingTradesPerDay.Match(l))
                                                 .First(m => m.Success);
            Match totalProfitMatch = outputSplit.Skip(startResultLine)
                                                .Select<string, Match>(l => BackTestingTotalProfit.Match(l))
                                                .First(m => m.Success);
            Match drawDownMatch = outputSplit.Skip(startResultLine)
                                                .Select<string, Match>(l => BackTestingDrawDown.Match(l))
                                                .First(m => m.Success);
            Match marketChangeMatch = outputSplit.Skip(startResultLine)
                                                .Select<string, Match>(l => BackTestingMarketChange.Match(l))
                                                .First(m => m.Success);
            double tradesPerDay = double.Parse(tradesPerDayMatch.Groups["trades"].Value,
                                               ConfigCulture);
            double totalProfit = double.Parse(totalProfitMatch.Groups["profit"].Value,
                                              ConfigCulture);
            double drawDown = double.Parse(drawDownMatch.Groups["drawdown"].Value,
                                              ConfigCulture);
            double market = double.Parse(marketChangeMatch.Groups["market"].Value,
                                              ConfigCulture);
            double dailyProfit = Math.Pow((totalProfit/100)+1,1.0/daysCount)-1;
            return new BackTestingResult(strategyName, dailyProfit, tradesPerDay, pairProfits, isUnstableStake, drawDown, market, totalProfit);
        }

        private static readonly Regex BackTestingTradesPerDay = new(@"^\|\s*Total\/Daily Avg Trades\s*\|\s*\d+(?:\.\d+)?\s*\/\s*(?<trades>\d+(?:\.\d+)?).*\|$", RegexOptions.Compiled);
        private static readonly Regex BackTestingTotalProfit = new(@"^\|\s*Total profit %\s*\|\s*(?<profit>-?\d+(?:\.\d+)?).*\|$", RegexOptions.Compiled);
        private static readonly Regex BackTestingDrawDown = new(@"^\|\s*Drawdown\s*\|\s*(?<drawdown>-?\d+(?:\.\d+)?).*\|$", RegexOptions.Compiled);
        private static readonly Regex BackTestingMarketChange = new(@"^\|\s*Market change\s*\|\s*(?<market>-?\d+(?:\.\d+)?).*\|$", RegexOptions.Compiled);

        private static readonly Regex BackTestingPairProfit =
            new(
                @"^\|\s*(?<pair>[A-Z0-9]*\/[A-Z0-9]*)\s*\|[^\|]*\|[^\|]*\|[^\|]*\|[^\|]*\|\s*(?<total_profit>-?\d+(?:\.\d+)?).*\|$"
              , RegexOptions.Compiled);

        public static readonly CultureInfo ConfigCulture = CultureInfo.GetCultureInfo("en-US");
    }
}