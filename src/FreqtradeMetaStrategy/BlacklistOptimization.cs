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

        private static readonly Regex PairMatcher =
            new Regex(@"'(?<pair>[0-9A-Za-z]+\/[0-9A-Za-z]+)'", RegexOptions.Compiled);
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
            BlacklistOptimizationResult lastCompareResult = GetLastResult(options, compareResultFile);
            BlacklistOptimizationResult lastResult = GetLastResult(options, resultFile, lastCompareResult);
            DeployConfig(configFile);
            if (lastResult.AllPairs == null)
            {
                FindTradablePairs(lastResult, configFile);
                SaveResult(lastResult, resultFile);
            }

            if (!lastResult.DataDownloaded)
            {
                DateTime endDate = DownloadData(options, lastResult, configFile, timeframe);
                lastResult.DataDownloaded = true;
                lastResult.EndDate = endDate;
                SaveResult(lastResult, resultFile);
            }

            DeployStaticConfig(configFile);
            BlacklistOptimizationPairsPartitionResult pairsChunk = GetNextPairsChunk(options, lastResult);
            SaveResult(lastResult, resultFile);
            while (pairsChunk!= null)
            {
                RunTests(pairsChunk, options, configFile, lastResult.EndDate, 
                         () => SaveResult(lastResult, resultFile));
                pairsChunk = GetNextPairsChunk(options, lastResult);
                SaveResult(lastResult, resultFile);
            }

            if (lastResult.Blacklist == null)
            {
                RuleBasedBlacklistGeneration(lastResult);
                SaveResult(lastResult, resultFile);
            }

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

            lastResult.ParameterOptimization ??= new ParameterOptimization();
            if (!lastResult.ParameterOptimization.Completed)
            {
                OptimizeParameters(options, lastResult, configFile, () => SaveResult(lastResult, resultFile));
            }
            
            GenerateReport(lastResult, lastResult.Blacklist, blacklistReport, options);
            GenerateReport(lastResult, lastResult.AllPairs.Except(lastResult.Blacklist).ToArray(), greenReport, options);
            GeneratePerformanceReport(lastResult, performanceReport, options);
            GenerateParameterOptimizationReport(lastResult, parameterOptimizationReport, options);
            ClassLogger.Information($"Found {lastResult.Blacklist.Length} blacklisted pairs. Performance of the strategy {options.Strategy} is: Top {options.PairsPartition} - {lastResult.Performance.Unfiltered*100:F2}% | All - {lastResult.Performance.Overall*100:F2}% | Blacklisted Top {options.PairsPartition} - {lastResult.Performance.Filtered*100:F2}%. Happy trading ^^.");
            if (lastCompareResult?.Strategy != null)
            {
                double correlation = CompareStrategies(lastCompareResult.Strategy, options.Strategy, lastResult, options.Interval*2, configFile);
                ClassLogger.Information($"Comparision of {lastCompareResult.Strategy} to {options.Strategy} - correlation: {correlation*100:F2}%");
            }
            return true;

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
            BackTestingResult baseResult = BackTesting(daysCount, endDateFormat, startDateFormat, configFile,
                                                       string.Join(" ", lastResult.Results[0].PairList), 9,
                                                       baseStrategy, true);
            BackTestingResult compareResult = BackTesting(daysCount, endDateFormat, startDateFormat, configFile,
                                                       string.Join(" ", lastResult.Results[0].PairList), 9,
                                                       compareStrategy, true);
            return (double) baseResult.Trades.Count(t => compareResult.Trades.Any(tc => tc.OpenTime == t.OpenTime))
                   / baseResult.Trades.Length;
        }

        private static void OptimizeParameters(BlacklistOptimizationOptions options,
                                               BlacklistOptimizationResult lastResult,
                                               string configFile, Action persistAction)
        {
            string pairs = string.Join(" ", lastResult.AllPairs.Take(70));
            int daysCount = options.Interval * 4;
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

                BackTestingResult result = BackTesting(daysCount, endDateFormat, startDateFormat, configFile, pairs, openTrades,
                    options.Strategy);
                lastResult.ParameterOptimization.Intervals = (lastResult.ParameterOptimization.Intervals??Enumerable.Empty<ParameterInterval>())
                                                                       .Concat(new []{new ParameterInterval
                                                                        {
                                                                            ParameterType = ParameterType.MaxOpenTrades,
                                                                            ParameterValue = openTrades,
                                                                            Result = ConvertToIntervalResult(result, startDateFormat, endDateFormat) 
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
                BackTestingResult result = BackTesting(daysCount, endDateFormat, startDateFormat, configFile, pairs, bestOpenTrades,
                    options.Strategy);
                lastResult.ParameterOptimization.Intervals = lastResult.ParameterOptimization.Intervals
                                                                       .Concat(new []{new ParameterInterval
                                                                        {
                                                                            ParameterType = ParameterType.PairsCount,
                                                                            ParameterValue = pairsCount,
                                                                            Result = ConvertToIntervalResult(result, startDateFormat, endDateFormat) 
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
                StringBuilder profit = StartChart("Profit", true);
                StringBuilder market = StartChart("Market Change", true);
                foreach (IntervalResult result in lastResult.Results[0].Results)
                {
                    AddData(profit, result.StartDate, result.Profit);
                    AddData(market, result.StartDate, result.MarketChange);
                }
                EndChart(profit);
                EndChart(market);
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
            WriteReport(parameterOptimizationReport, "FreqtradeMetaStrategy.ParameterOptReportTemplate.html", transformator);
            
            string GenerateChart(ParameterInterval[] parameterIntervals)
            {
                StringBuilder profit = StartChart("Profit", true);
                StringBuilder drawDown = StartChart("Draw Down", true);
                foreach (ParameterInterval interval in parameterIntervals)
                {
                    profit.AppendLine($"{{ x: {interval.ParameterValue}, y: {interval.Result.Profit} }},");
                    drawDown.AppendLine($"{{ x: {interval.ParameterValue}, y: {interval.Result.DrawDown} }},");
                }
                EndChart(profit);
                EndChart(drawDown);
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
                        AddData(pairsProfitBuilders[pair], date, value);
                    }
                }
                foreach (StringBuilder chartData in pairsProfitBuilders.Values)
                {
                    EndChart(chartData);
                }

                return string.Join($",{Environment.NewLine}", pairsProfitBuilders.Values);
            }

            Dictionary<string, StringBuilder> StartPairsProfitCharts()
            {
                return pairsForReport.ToDictionary(p => p, p => StartChart(p, false));
            }
        }

        private static void WriteReport(string file, BlacklistOptimizationOptions options, string pairProfits)
        {
            Func<string, string> transformator = c => c.Replace("$(StrategyName)", options.Strategy)
                                                       .Replace("$(PairProfit)", pairProfits);
            string resourceKey = "FreqtradeMetaStrategy.BlacklistReportTemplate.html";
            WriteReport(file, resourceKey, transformator);
        }

        private static void WriteReport(string file, string resourceKey, Func<string, string> transformator)
        {
            using Stream embeddedStream = Assembly.GetExecutingAssembly()
                                                  .GetManifestResourceStream(resourceKey);
            if (embeddedStream == null)
            {
                throw new InvalidOperationException("Report template not found.");
            }

            using StreamReader reportTemplateStream = new(embeddedStream);
            string content = transformator(reportTemplateStream.ReadToEnd());
            File.WriteAllText(file, content, Encoding.UTF8);
        }

        private static void AddData(StringBuilder chartData, string date, double value)
        {
            chartData.AppendLine($"{{ x: new Date({date[new Range(0, 4)]}, {date[new Range(4, 6)]}, {date[new Range(6, 8)]}), y: {value} }},");
        }

        private static void EndChart(StringBuilder chartData)
        {
            chartData.AppendLine("]");
            chartData.AppendLine("}");
        }

        private static StringBuilder StartChart(string title, bool visible)
        {
            StringBuilder chartData = new();
            chartData.AppendLine("{");
            chartData.AppendLine("showInLegend: true,");
            chartData.AppendLine("type: \"line\",");
            chartData.AppendLine($"name: \"{title}\",");
            chartData.AppendLine($"visible: {(visible ? "true" : "false")},");
            chartData.AppendLine("toolTipContent: \"{name} - {x}: {y}%\",");
            chartData.AppendLine("dataPoints: [");
            return chartData;
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
                    OnlyNegativeInRecentTimes(values) ||
                    StrongNegativeInLastInterval(values, pair))
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
            IntervalResult intervalResult = ConvertToIntervalResult(result, startDate, endDate);
            lastResult.Results = lastResult.Results.Concat(new[] {intervalResult})
                                           .ToArray();
            persistAction();
        }

        private static IntervalResult ConvertToIntervalResult(BackTestingResult result, string startDate, string endDate)
        {
            IntervalResult intervalResult = new()
            {
                Profit = result.TotalProfit,
                DrawDown = result.DrawDown,
                MarketChange = result.MarketChange,
                StartDate = startDate,
                EndDate = endDate,
                Pairs = result.PairsProfit
                              .Select(kv => new PairProfit
                               {
                                   Pair = kv.Key,
                                   Profit = kv.Value
                               })
                              .ToArray()
            };
            return intervalResult;
        }

        private static BackTestingResult BackTestInterval(BlacklistOptimizationOptions options, string endDate,
                                                          string startDate, string configFile,
                                                          BlacklistOptimizationPairsPartitionResult
                                                              lastResult)
        {
            string pairs = string.Join(" ", lastResult.PairList);
            int openTrades = 1;
            return BackTesting(options.Interval, endDate, startDate, configFile, pairs, openTrades, options.Strategy);
        }

        private static BackTestingResult BackTesting(int daysCount, string endDate, string startDate,
                                                     string configFile, string pairs, int openTrades, string strategy, 
                                                     bool parseTrades = false)
        {
            bool result = ProcessFacade.Execute("freqtrade",
                                                $"backtesting --data-format-ohlcv hdf5  --timerange {startDate}-{endDate} -s {strategy} -c {configFile} -p {pairs} --max-open-trades {openTrades}",
                                                out StringBuilder output);
            if (!result)
            {
                throw new InvalidOperationException(
                    $"Unexpected failure of back testing strategy {strategy}.");
            }

            BackTestingResult newResult =
                ToolBox.EvaluateBackTestingResult(output.ToString(), strategy, daysCount, false, parseTrades);
            return newResult;
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

        private static DateTime DownloadData(BlacklistOptimizationOptions options,
                                             BlacklistOptimizationResult lastResult, string configFile,
                                             string timeframe)
        {
            string pairs = string.Join(" ", lastResult.AllPairs);
            int intervalCount = (int) Math.Ceiling((double) options.TimeRange / options.Interval);
            DateTime endDate = DateTime.Today;
            DateTime startDate = endDate - new TimeSpan(options.Interval * intervalCount + 20, 0, 0, 0);
            string endDateFormat = endDate.ToString("yyyyMMdd");
            string startDateFormat = startDate.ToString("yyyyMMdd");
            ClassLogger.Information($"Download data for optimization.");
            bool result = ProcessFacade.Execute("freqtrade", $"download-data -t {timeframe} --data-format-ohlcv hdf5 --timerange {startDateFormat}-{endDateFormat} -p {pairs} -c {configFile}");

            if (!result)
            {
                throw new InvalidOperationException(
                    $"Error while downloading data.");
            }
            return endDate;
        }

        private static void FindTradablePairs(BlacklistOptimizationResult lastResult, string configFile)
        {
            bool result = ProcessFacade.Execute("freqtrade",
                                                $"test-pairlist -c {configFile}",
                                                out StringBuilder output);
            if (!result)
            {
                throw new InvalidOperationException(
                    $"Unexpected failure of retrieving all pairs.");
            }

            List<string> pairs = new();
            string content = output.ToString();
            string lastLine = content.Split(new[] { Environment.NewLine },
                                            StringSplitOptions.RemoveEmptyEntries).Last();
            Match pairsMatch = PairMatcher.Match(lastLine);
            while (pairsMatch.Success)
            {
                pairs.Add(pairsMatch.Groups["pair"].Value);
                pairsMatch = pairsMatch.NextMatch();
            }

            if (!pairs.Any())
            {
                throw new InvalidOperationException($"Pairs not found in line: {lastLine}. Complete output: {content}");
            }

            lastResult.AllPairs = pairs.ToArray();
        }

        private static void DeployStaticConfig(string configFile)
        {
            DeployResource(configFile, "FreqtradeMetaStrategy.blacklist-template-static-config.json");
        }

        private static void DeployConfig(string configFile)
        {
            DeployResource(configFile, "FreqtradeMetaStrategy.blacklist-template-config.json");
        }

        private static void DeployResource(string file, string resource)
        {
            FileInfo fileInfo = new(file);
            if (!fileInfo.Directory?.Exists != true)
            {
                fileInfo.Directory?.Create();
            }

            if (fileInfo.Exists)
            {
                fileInfo.Delete();
            }

            using Stream resourceStream = Assembly.GetExecutingAssembly()
                                                  .GetManifestResourceStream(resource);
            using Stream fileStream = fileInfo.OpenWrite();
            if (resourceStream == null)
            {
                throw new InvalidOperationException($"resource \"{resource}\" not found. Available: {string.Join("; ", Assembly.GetExecutingAssembly().GetManifestResourceNames())}");
            }
            resourceStream.CopyTo(fileStream);
        }

        private static void SaveResult(BlacklistOptimizationResult result, string resultFile)
        {
            File.WriteAllText(resultFile, JsonConvert.SerializeObject(result, Formatting.Indented), Encoding.UTF8);
        }

        private static BlacklistOptimizationResult GetLastResult(BlacklistOptimizationOptions options,
                                                                 string resultFile,
                                                                 BlacklistOptimizationResult
                                                                     compareResult = null)
        {
            FileInfo fileInfo = new(resultFile);
            if (!fileInfo.Directory?.Exists != true)
            {
                fileInfo.Directory?.Create();
            }

            if (!fileInfo.Exists)
            {
                return compareResult?.Performance != null
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
                           };
            }

            return JsonConvert.DeserializeObject<BlacklistOptimizationResult>(File.ReadAllText(resultFile));
        }
    }
}