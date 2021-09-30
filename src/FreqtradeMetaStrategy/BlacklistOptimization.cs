#region Copyright
//  Copyright (c) Tobias Wilker and contributors
//  This file is licensed under MIT
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Serilog;
using Serilog.Configuration;

namespace FreqtradeMetaStrategy
{
    public static class BlacklistOptimization
    {
        private const string ResultFolder = "./user_data/blacklist-optimization";
        private static readonly ILogger ClassLogger = Log.ForContext(typeof(BlacklistOptimization));

        public static bool GenerateOptimalBlacklist(BlacklistOptimizationOptions options)
        {
            string resultFile = Path.Combine(ResultFolder, $"{options.Tag}-result.json");
            string compareResultFile = Path.Combine(ResultFolder, $"{options.CompareTag??string.Empty}-result.json");
            string blacklistReport = Path.Combine(ResultFolder, $"{options.Tag}-blacklist-report.html");
            string greenReport = Path.Combine(ResultFolder, $"{options.Tag}-green-report.html");
            string performanceReport = Path.Combine(ResultFolder, $"{options.Tag}-performance-report.html");
            string parameterOptimizationReport = Path.Combine(ResultFolder, $"{options.Tag}-parameter-optimization-report.html");
            string configFile = Path.Combine(ResultFolder, $"{options.Tag}-config.json");
            string timeframe = string.IsNullOrEmpty(options.TimeFrames) ? "5m 1h" : options.TimeFrames;
            int longInterval = options.LongInterval > 0 ? options.LongInterval : options.Interval * 4; 
            BlacklistOptimizationResult lastCompareResult = GetLastResult(options, compareResultFile, createEmpty:false);
            BlacklistOptimizationResult lastResult = GetLastResult(options, resultFile, lastCompareResult);

            ToolBox.FindPairsDownloadAndSetConfig(configFile, lastResult.AllPairs == null, !lastResult.DataDownloaded, options.TimeRange, options.Interval, timeframe, SetPairs, SetDataDownloaded, GetAllPairs);

            BlacklistOptimizationPairsPartitionResult pairsChunk = GetNextPairsChunk(options, lastResult);
            SaveResult(lastResult, resultFile);
            while (pairsChunk!= null)
            {
                RunTests(pairsChunk, options, configFile, lastResult.EndDate, 
                         () => SaveResult(lastResult, resultFile));
                pairsChunk = GetNextPairsChunk(options, lastResult);
                SaveResult(lastResult, resultFile);
            }

            RuleBasedBlacklistGeneration(lastResult);
            SaveResult(lastResult, resultFile);

            if (lastResult.Performance == null)
            {
                lastResult.Performance = new StrategyPerformance
                {
                    Unfiltered = CalculatePerformance(),
                    Filtered = CalculateFixedPerformance(),
                    Overall = CalculateOverallPerformance()
                };
                SaveResult(lastResult, resultFile);
            }

            // lastResult.ParameterOptimization ??= new ParameterOptimization();
            // if (!lastResult.ParameterOptimization.Completed)
            // {
            //     OptimizeParameters(options, lastResult, configFile, longInterval,
            //                        () => SaveResult(lastResult, resultFile));
            // }
            
            GenerateReport(lastResult, lastResult.Blacklist, blacklistReport, options);
            GenerateReport(lastResult, lastResult.AllPairs.Except(lastResult.Blacklist).ToArray(), greenReport, options);
            GeneratePerformanceReport(lastResult, performanceReport, options);
            //GenerateParameterOptimizationReport(lastResult, parameterOptimizationReport, options);
            
            if (lastCompareResult?.Strategy != null)
            {
                double correlation = CompareStrategies(lastCompareResult.Strategy, options.Strategy, lastResult, options.Interval*2, configFile);
                ClassLogger.Information($"Comparision of {lastCompareResult.Strategy} to {options.Strategy} - correlation: {correlation*100:F2}%");
            }
            ClassLogger.Information($"Found {lastResult.Blacklist.Length} blacklisted pairs. Performance of the strategy {options.Strategy} is: Top {options.PairsPartition} - {lastResult.Performance.Unfiltered*100:F2}% | All - {lastResult.Performance.Overall*100:F2}% | Blacklisted Top {options.PairsPartition} - {lastResult.Performance.Filtered*100:F2}%. Happy trading ^^.");
            
            return true;
            
            void SetPairs(string[] pairs)
            {
                lastResult.AllPairs = pairs;
                SaveResult(lastResult, resultFile);
            }

            void SetDataDownloaded(DateTime endDate)
            {
                lastResult.DataDownloaded = true;
                lastResult.EndDate = endDate;
                SaveResult(lastResult, resultFile);
            }

            string[] GetAllPairs() => lastResult.AllPairs;

            double CalculatePerformance()
            {
                double market = lastResult.Results[0].Results.Sum(r => r.MarketChange);
                double profit = lastResult.Results[0].Results.Sum(r => r.Profit);
                return profit / market;
            }

            double CalculateFixedPerformance()
            {
                double market = lastResult.Results[0].Results.Sum(r => r.MarketChange);
                double profit = lastResult.Results.SelectMany(r => r.Results)
                                          .SelectMany(r => r.Pairs)
                                          .Where(p => !lastResult.Blacklist.Contains(p.Pair))
                                          .Sum(p => p.Profit);
                return profit / market;
            }

            double CalculateOverallPerformance()
            {
                double market = lastResult.Results.SelectMany(r => r.Results).Sum(r => r.MarketChange);
                double profit = lastResult.Results.SelectMany(r => r.Results).Sum(r => r.Profit);
                return profit / market;
            }
        }

        private static double CompareStrategies(string baseStrategy, string compareStrategy, BlacklistOptimizationResult lastResult, int daysCount, string configFile)
        {
            DateTime startDate = lastResult.EndDate - new TimeSpan(daysCount, 0, 0, 0);
            string endDateFormat = lastResult.EndDate.ToString("yyyyMMdd");
            string startDateFormat = startDate.ToString("yyyyMMdd");
            BackTestingResult baseResult = ToolBox.BackTesting(daysCount, endDateFormat, startDateFormat, configFile,
                                                          string.Join(" ", lastResult.Results[0].PairList), 9,
                                                          baseStrategy, true);
            BackTestingResult compareResult = ToolBox.BackTesting(daysCount, endDateFormat, startDateFormat, configFile,
                                                             string.Join(" ", lastResult.Results[0].PairList), 9,
                                                             compareStrategy, true);
            return (double) baseResult.Trades.Count(t => compareResult.Trades.Any(tc => tc.OpenTime == t.OpenTime && tc.Pair == t.Pair))
                   / baseResult.Trades.Length;
        }

        private static void OptimizeParameters(BlacklistOptimizationOptions options,
                                               BlacklistOptimizationResult lastResult,
                                               string configFile, int longInterval, 
                                               Action persistAction)
        {
            string pairs = string.Join(" ", lastResult.AllPairs.Take(70));
            int daysCount = longInterval;
            DateTime endDate = lastResult.EndDate - new TimeSpan(1, 0, 0, 0);
            DateTime startDate = endDate - new TimeSpan(daysCount, 0, 0, 0);
            string endDateFormat = endDate.ToString("yyyyMMdd");
            string startDateFormat = startDate.ToString("yyyyMMdd");
            for (int openTrades = 1; openTrades < 10; openTrades++)
            {
                if (lastResult.ParameterOptimization.Intervals?
                              .Any(p => p.ParameterType == ParameterType.MaxOpenTrades &&
                                        p.ParameterValue == openTrades) == true)
                {
                    continue;
                }

                BackTestingResult result = ToolBox.BackTesting(daysCount, endDateFormat, startDateFormat, configFile, pairs, openTrades,
                                                          options.Strategy);
                lastResult.ParameterOptimization.Intervals = (lastResult.ParameterOptimization.Intervals??Enumerable.Empty<ParameterInterval>())
                                                                       .Concat(new []{new ParameterInterval
                                                                        {
                                                                            ParameterType = ParameterType.MaxOpenTrades,
                                                                            ParameterValue = openTrades,
                                                                            Result = result.ConvertToIntervalResult(startDateFormat, endDateFormat) 
                                                                        }})
                                                                       .ToArray();
                persistAction();
            }

            int bestOpenTrades = lastResult.ParameterOptimization.Intervals.OrderByDescending(i => i.Result.Profit)
                                           .First().ParameterValue;
            for (int pairsCount = 60; pairsCount < 100; pairsCount+=5)
            {
                if (lastResult.ParameterOptimization.Intervals
                              .Any(p => p.ParameterType == ParameterType.PairsCount &&
                                        p.ParameterValue == pairsCount))
                {
                    continue;
                }

                pairs = string.Join(" ", lastResult.AllPairs.Take(pairsCount));
                BackTestingResult result = ToolBox.BackTesting(daysCount, endDateFormat, startDateFormat, configFile, pairs, bestOpenTrades,
                                                          options.Strategy);
                lastResult.ParameterOptimization.Intervals = lastResult.ParameterOptimization.Intervals
                                                                       .Concat(new []{new ParameterInterval
                                                                        {
                                                                            ParameterType = ParameterType.PairsCount,
                                                                            ParameterValue = pairsCount,
                                                                            Result = result.ConvertToIntervalResult(startDateFormat, endDateFormat) 
                                                                        }})
                                                                       .ToArray();
                persistAction();
            }

            lastResult.ParameterOptimization.Completed = true;
            persistAction();
        }

        private static void GeneratePerformanceReport(BlacklistOptimizationResult lastResult, string performanceReport, BlacklistOptimizationOptions options)
        {
            WriteReport(performanceReport, options, GeneratePerformanceChart());
            
            string GeneratePerformanceChart()
            {
                StringBuilder profit = ToolBox.StartChart("Profit", true);
                StringBuilder market = ToolBox.StartChart("Market Change", true);
                foreach (IntervalResult result in lastResult.Results[0].Results)
                {
                    ToolBox.AddData(profit, result.StartDate, result.Profit);
                    ToolBox.AddData(market, result.StartDate, result.MarketChange);
                }

                ToolBox.EndChart(profit);
                ToolBox.EndChart(market);
                return profit + "," + Environment.NewLine + market;
            }
        }

        private static void GenerateParameterOptimizationReport(BlacklistOptimizationResult lastResult, string parameterOptimizationReport, BlacklistOptimizationOptions options)
        {
            Func<string, string> transformator = c => c.Replace("$(StrategyName)", options.Strategy)
                                                       .Replace("$(OpenTradesChart)", GenerateChart(lastResult.ParameterOptimization.Intervals.Where(i => i.ParameterType == ParameterType.MaxOpenTrades)
                                                                   .ToArray()))
                                                       .Replace("$(PairsCountChart)", GenerateChart(lastResult.ParameterOptimization.Intervals.Where(i => i.ParameterType == ParameterType.PairsCount)
                                                                   .ToArray()));
            ToolBox.WriteReport(parameterOptimizationReport, "FreqtradeMetaStrategy.ParameterOptReportTemplate.html", transformator);
            
            string GenerateChart(ParameterInterval[] parameterIntervals)
            {
                StringBuilder profit = ToolBox.StartChart("Profit", true);
                StringBuilder drawDown = ToolBox.StartChart("Draw Down", true);
                foreach (ParameterInterval interval in parameterIntervals)
                {
                    profit.AppendLine($"{{ x: {interval.ParameterValue}, y: {interval.Result.Profit} }},");
                    drawDown.AppendLine($"{{ x: {interval.ParameterValue}, y: {interval.Result.DrawDown} }},");
                }

                ToolBox.EndChart(profit);
                ToolBox.EndChart(drawDown);
                return profit + "," + Environment.NewLine + drawDown;
            }
        }

        private static void GenerateReport(BlacklistOptimizationResult lastResult, string[] pairsForReport, string file,
                                           BlacklistOptimizationOptions options)
        {
            WriteReport(file, options, GeneratePairsChartData());

            string GeneratePairsChartData()
            {
                Dictionary<string, StringBuilder> pairsProfitBuilders = StartPairsProfitCharts();
                foreach (string pair in pairsForReport)
                {
                    foreach ((string date, double value) in GetHistory(pair, lastResult))
                    {
                        ToolBox.AddData(pairsProfitBuilders[pair], date, value);
                    }
                }
                foreach (StringBuilder chartData in pairsProfitBuilders.Values)
                {
                    ToolBox.EndChart(chartData);
                }

                return string.Join($",{Environment.NewLine}", pairsProfitBuilders.Values);
            }

            Dictionary<string, StringBuilder> StartPairsProfitCharts()
            {
                return pairsForReport.ToDictionary(p => p, p => ToolBox.StartChart(p, false));
            }
        }

        private static void WriteReport(string file, BlacklistOptimizationOptions options, string pairProfits)
        {
            Func<string, string> transformator = c => c.Replace("$(StrategyName)", options.Strategy)
                                                       .Replace("$(PairProfit)", pairProfits);
            string resourceKey = "FreqtradeMetaStrategy.BlacklistReportTemplate.html";
            ToolBox.WriteReport(file, resourceKey, transformator);
        }

        private static void RuleBasedBlacklistGeneration(BlacklistOptimizationResult result)
        {
            List<string> blacklist = new();
            foreach (string pair in result.AllPairs)
            {
                ClassLogger.Information($"Evaluate {pair}");
                HistoryData[] values = GetHistory(pair, result);
                if (IsOverallNegative(values) ||
                    HasBiggerNegativeThenPositive(values) ||
                    MoreNegativeThanPositive(values) ||
                    OnlyNegativeInRecentTimes(values))
                {
                    blacklist.Add(pair);
                }
            }

            result.Blacklist = blacklist.ToArray();
            
            bool IsOverallNegative(HistoryData[] values)
            {
                return values.Sum(v => v.Value) < 0;
            }
            
            bool HasBiggerNegativeThenPositive(HistoryData[] values)
            {
                double min = values.Min(v => v.Value);
                double max = values.Max(v => v.Value);
                return min < 0 &&
                       Math.Abs(min) > 3 * max;
            }
            
            bool MoreNegativeThanPositive(HistoryData[] values)
            {
                return values.Count(v => v.Value < 0) >
                       values.Count(v => v.Value > 0);
            }
            
            bool OnlyNegativeInRecentTimes(HistoryData[] values)
            {
                int tradeIntervals = values.Count(v => v.Value != 0);
                return values.Where(v=>v.Value !=0)
                             .Skip((int)Math.Ceiling((double)(tradeIntervals/2)))
                             .All(v => v.Value <=0) &&
                       values.Where(v=>v.Value !=0)
                             .Skip((int)Math.Ceiling((double)(tradeIntervals/2)))
                             .Any(v => v.Value <=0) ||
                       values.Skip((int)Math.Ceiling((double)(values.Length/2)))
                             .All(v => v.Value <=0) &&
                       values.Skip((int)Math.Ceiling((double)(values.Length/2)))
                             .Any(v => v.Value <0);
            }
            
            bool StrongNegativeInLastInterval(HistoryData[] values, string pair)
            {
                double cutoff = result.Results.First(p => p.PairList.Contains(pair))
                                      .Results[0].DrawDown / -2;
                return values.Last().Value < cutoff;
            }
        }

        private record HistoryData(string Date, double Value);

        private static HistoryData[] GetHistory(string pair, BlacklistOptimizationResult result)
        {
            return result.Results.First(r => r.PairList.Contains(pair))
                         .Results.Select(i => new HistoryData(i.StartDate, i.Pairs.FirstOrDefault(p => p.Pair == pair)?.Profit??0))
                         .Reverse().ToArray();
        }

        private static void RunTests(BlacklistOptimizationPairsPartitionResult pairsChunk,
                                     BlacklistOptimizationOptions options, string configFile,
                                     DateTime endDate, Action persistAction)
        {
            DateTime lastStartDate = GetLastStartDate(pairsChunk, endDate, out int completedIntervals);
            int intervalCount = (int) Math.Ceiling((double) options.TimeRange / options.Interval);
            
            while (completedIntervals <= intervalCount)
            {
                endDate = lastStartDate - new TimeSpan(1, 0, 0, 0);
                DateTime startDate = endDate - new TimeSpan(options.Interval,0,0,0);
                string endDateFormat = endDate.ToString("yyyyMMdd");
                string startDateFormat = startDate.ToString("yyyyMMdd");
                BackTestingResult result = BackTestInterval(options, endDateFormat, startDateFormat, configFile,
                                                            pairsChunk);
                UpdateResult(pairsChunk, result, persistAction, startDateFormat, endDateFormat);
                lastStartDate = startDate;
                completedIntervals++;
            }

            pairsChunk.Completed = true;
            persistAction();
        }

        private static void UpdateResult(BlacklistOptimizationPairsPartitionResult lastResult, BackTestingResult result, Action persistAction,
                                         string startDate, string endDate)
        {
            IntervalResult intervalResult = result.ConvertToIntervalResult(startDate, endDate);
            lastResult.Results = lastResult.Results.Concat(new[] {intervalResult})
                                           .ToArray();
            persistAction();
        }

        private static BackTestingResult BackTestInterval(BlacklistOptimizationOptions options, string endDate,
                                                          string startDate, string configFile,
                                                          BlacklistOptimizationPairsPartitionResult
                                                              lastResult)
        {
            string pairs = string.Join(" ", lastResult.PairList);
            int openTrades = 1;
            return ToolBox.BackTesting(options.Interval, endDate, startDate, configFile, pairs, openTrades, options.Strategy);
        }

        private static DateTime GetLastStartDate(BlacklistOptimizationPairsPartitionResult lastResult, DateTime endDate, out int completedIntervals)
        {
            completedIntervals = 0;
            if (!lastResult.Results.Any())
            {
                return endDate;
            }

            completedIntervals = lastResult.Results.Length;
            string date = lastResult.Results.Last().StartDate;
            return new DateTime(int.Parse(date[new Range(0,4)]),
                                int.Parse(date[new Range(4,6)]),
                                int.Parse(date[new Range(6,8)]));
        }

        private static BlacklistOptimizationPairsPartitionResult GetNextPairsChunk(BlacklistOptimizationOptions options, BlacklistOptimizationResult lastResult)
        {
            BlacklistOptimizationPairsPartitionResult last = lastResult.Results?.LastOrDefault();
            if (last?.Completed == false)
            {
                return last;
            }

            int startIndex = lastResult.Results?.Sum(r => r.PairList.Length) ?? 0;
            int batchSize = startIndex+options.PairsPartition < lastResult.AllPairs.Length
                                ? options.PairsPartition
                                : lastResult.AllPairs.Length - startIndex;
            string[] batch = batchSize > 0
                                 ? lastResult.AllPairs.AsSpan(startIndex, batchSize).ToArray()
                                 : Array.Empty<string>();
            if (!batch.Any())
            {
                return null;
            }

            BlacklistOptimizationPairsPartitionResult result = new BlacklistOptimizationPairsPartitionResult { PairList = batch };
            lastResult.Results = (lastResult.Results
                                  ?? Enumerable.Empty<BlacklistOptimizationPairsPartitionResult>())
                                .Concat(new[] { result })
                                .ToArray();
            return result;
        }

        private static void SaveResult(BlacklistOptimizationResult result, string resultFile)
        {
            File.WriteAllText(resultFile, JsonConvert.SerializeObject(result, Formatting.Indented), Encoding.UTF8);
        }

        private static BlacklistOptimizationResult GetLastResult(BlacklistOptimizationOptions options,
                                                                 string resultFile,
                                                                 BlacklistOptimizationResult
                                                                     compareResult = null, bool createEmpty = true)
        {
            FileInfo fileInfo = new(resultFile);
            if (!fileInfo.Directory?.Exists != true)
            {
                fileInfo.Directory?.Create();
            }

            if (!fileInfo.Exists)
            {
                return createEmpty
                           ? compareResult?.Performance != null
                                 ? new BlacklistOptimizationResult
                                 {
                                     Strategy = options.Strategy,
                                     AllPairs = compareResult.AllPairs,
                                     DataDownloaded = true,
                                     EndDate = compareResult.EndDate
                                 }
                                 : new BlacklistOptimizationResult
                                 {
                                     Strategy = options.Strategy
                                 }
                           : null;
            }

            return JsonConvert.DeserializeObject<BlacklistOptimizationResult>(File.ReadAllText(resultFile));
        }
    }
}