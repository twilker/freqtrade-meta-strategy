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
            string blacklistReport = Path.Combine(ResultFolder, $"{options.Tag}-blacklist-report.html");
            string greenReport = Path.Combine(ResultFolder, $"{options.Tag}-green-report.html");
            string performanceReport = Path.Combine(ResultFolder, $"{options.Tag}-performance-report.html");
            string configFile = Path.Combine(ResultFolder, $"{options.Tag}-config.json");
            BlacklistOptimizationResult lastResult = GetLastResult(options, resultFile);
            DeployConfig(configFile);
            if (lastResult.AllPairs == null)
            {
                FindTradablePairs(lastResult, configFile);
                SaveResult(lastResult, resultFile);
            }

            if (!lastResult.DataDownloaded)
            {
                DateTime endDate = DownloadData(options, lastResult, configFile);
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
            
            GenerateReport(lastResult, lastResult.Blacklist, blacklistReport, options);
            GenerateReport(lastResult, lastResult.AllPairs.Except(lastResult.Blacklist).ToArray(), greenReport, options);
            GeneratePerformanceReport(lastResult, performanceReport, options);
            ClassLogger.Information($"Found {lastResult.Blacklist.Length} blacklisted pairs. Performance of the strategy {options.Strategy} is: Top {options.PairsPartition} - {lastResult.Performance.Unfiltered*100:F2}% | All - {lastResult.Performance.Overall*100:F2}% | Blacklisted Top {options.PairsPartition} - {lastResult.Performance.Filtered*100:F2}%. Happy trading ^^.");
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
            using Stream embeddedStream = Assembly.GetExecutingAssembly()
                                                  .GetManifestResourceStream(
                                                       "FreqtradeMetaStrategy.BlacklistReportTemplate.html");
            if (embeddedStream == null)
            {
                throw new InvalidOperationException("Report template not found.");
            }

            using StreamReader reportTemplateStream = new(embeddedStream);
            string content = reportTemplateStream.ReadToEnd()
                                                 .Replace("$(StrategyName)", options.Strategy)
                                                 .Replace("$(PairProfit)", pairProfits);
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
        }

        private record HistoryData(string Date, double Value);

        private static HistoryData[] GetHistory(string pair, BlacklistOptimizationResult result)
        {
            return result.Results.First(r => r.PairList.Contains(pair))
                         .Results.Select(i => new HistoryData(i.StartDate, i.Pairs.FirstOrDefault(p => p.Pair == pair)?.Profit??0))
                         .ToArray();
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
            bool result = ProcessFacade.Execute("freqtrade",
                                                $"backtesting --data-format-ohlcv hdf5  --timerange {startDate}-{endDate} -s {options.Strategy} -c {configFile} -p {pairs}",
                                                out StringBuilder output);
            if (!result)
            {
                throw new InvalidOperationException(
                    $"Unexpected failure of back testing strategy {options.Strategy}.");
            }

            BackTestingResult newResult = ToolBox.EvaluateBackTestingResult(output.ToString(), options.Strategy, options.Interval, false);
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

        private static DateTime DownloadData(BlacklistOptimizationOptions options, BlacklistOptimizationResult lastResult, string configFile)
        {
            string pairs = string.Join(" ", lastResult.AllPairs);
            int intervalCount = (int) Math.Ceiling((double) options.TimeRange / options.Interval);
            DateTime endDate = DateTime.Today;
            DateTime startDate = endDate - new TimeSpan(options.Interval * intervalCount + 20, 0, 0, 0);
            string endDateFormat = endDate.ToString("yyyyMMdd");
            string startDateFormat = startDate.ToString("yyyyMMdd");
            ClassLogger.Information($"Download data for optimization.");
            bool result = ProcessFacade.Execute("freqtrade", $"download-data -t 5m 1h --data-format-ohlcv hdf5 --timerange {startDateFormat}-{endDateFormat} -p {pairs} -c {configFile}");

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

        private static BlacklistOptimizationResult GetLastResult(BlacklistOptimizationOptions options, string resultFile)
        {
            FileInfo fileInfo = new(resultFile);
            if (!fileInfo.Directory?.Exists != true)
            {
                fileInfo.Directory?.Create();
            }

            if (!fileInfo.Exists)
            {
                return new BlacklistOptimizationResult
                {
                    Strategy = options.Strategy
                };
            }

            return JsonConvert.DeserializeObject<BlacklistOptimizationResult>(File.ReadAllText(resultFile));
        }
    }
}