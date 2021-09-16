using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Serilog;

namespace FreqtradeMetaStrategy
{
    public static class ParameterOptimizationTest
    {
        private const string ResultFolder = "./user_data/parameter-optimization";
        private static readonly ILogger ClassLogger = Log.ForContext(typeof(ParameterOptimizationTest));
        public static bool OptimizeParameters(ParameterOptimizationOptions options)
        {
            string resultFile = Path.Combine(ResultFolder, $"{options.Tag}-result.json");
            string configFile = Path.Combine(ResultFolder, $"{options.Tag}-config.json");
            string report = Path.Combine(ResultFolder, $"{options.Tag}-report.html");
            ParameterOptimizationTestResult lastResult = GetLastResult(options, resultFile);
            
            ToolBox.FindPairsDownloadAndSetConfig(configFile, lastResult.AllPairs == null, !lastResult.DataDownloaded, options.TimeRange, options.Interval, options.TimeFrames, SetPairs, SetDataDownloaded, GetAllPairs,
                options.PairsRangeHigh);
            
            lastResult.ParameterOptimization ??= new ParameterOptimization();
            if (!lastResult.ParameterOptimization.Completed)
            {
                OptimizeParameters(options, lastResult, configFile,
                                   () => SaveResult(lastResult, resultFile));
            }

            lastResult.Scores = CalculateScores(lastResult.ParameterOptimization.Intervals);
            SaveResult(lastResult, resultFile);
            GenerateReport(lastResult, report, options);
            int optimalOpenTrades = lastResult.Scores.Where(s => s.Type == ParameterType.MaxOpenTrades)
                                              .OrderByDescending(s => s.Score).First().Value;
            int optimalPairs = lastResult.Scores.Where(s => s.Type == ParameterType.PairsCount)
                                              .OrderByDescending(s => s.Score).First().Value;
            ClassLogger.Information($"Optimal parameter of the strategy {options.Strategy} are: Max Open Trades - {optimalOpenTrades}; Pairs Count - {optimalPairs}. Happy trading ^^.");

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
        }

        private static void GenerateReport(ParameterOptimizationTestResult lastResult, string report, ParameterOptimizationOptions options)
        {
            Func<string, string> transformator = c => c.Replace("$(StrategyName)", options.Strategy)
                                                       .Replace("$(OpenTradesChart)", GenerateChart(lastResult.Scores.Where(score => score.Type == ParameterType.MaxOpenTrades)
                                                                   .ToArray()))
                                                       .Replace("$(PairsCountChart)", GenerateChart(lastResult.Scores.Where(score => score.Type == ParameterType.PairsCount)
                                                                   .ToArray()));
            ToolBox.WriteReport(report, "FreqtradeMetaStrategy.ParameterScoresReportTemplate.html", transformator);
            
            string GenerateChart(ParameterScore[] parameterScores)
            {
                StringBuilder scores = ToolBox.StartChart("Score", true);
                foreach (ParameterScore score in parameterScores.OrderBy(s => s.Value))
                {
                    scores.AppendLine($"{{ x: {score.Value}, y: {score.Score} }},");
                }
                ToolBox.EndChart(scores);
                return scores.ToString();
            }
        }

        private static void OptimizeParameters(ParameterOptimizationOptions options,
                                               ParameterOptimizationTestResult lastResult,
                                               string configFile, Action persistAction)
        {
            DateTime lastStartDate = lastResult.EndDate;
            int intervalCount = (int) Math.Ceiling((double) options.TimeRange / options.Interval);

            List<ParameterTest> parameterTests = new();
            for (int i = 0; i < intervalCount; i++)
            {
                DateTime endDate = lastStartDate - new TimeSpan(1, 0, 0, 0);
                lastStartDate = endDate - new TimeSpan(options.Interval,0,0,0);
                string endDateFormat = endDate.ToString("yyyyMMdd");
                string startDateFormat = lastStartDate.ToString("yyyyMMdd");
                for (int pairs = options.PairsRangeLow; pairs <= options.PairsRangeHigh; pairs+=options.PairsInterval)
                {
                    parameterTests.Add(new ParameterTest(startDateFormat, endDateFormat, ParameterType.PairsCount, pairs));
                }
                for (int openTrades = options.OpenTradesLow; openTrades <= options.OpenTradesHigh; openTrades+=options.OpenTradesInterval)
                {
                    parameterTests.Add(new ParameterTest(startDateFormat, endDateFormat, ParameterType.MaxOpenTrades, openTrades));
                }
            }

            RunTests(parameterTests, lastResult, options, configFile, persistAction);
        }

        private static void RunTests(List<ParameterTest> parameterTests, ParameterOptimizationTestResult lastResult, ParameterOptimizationOptions options, string configFile, Action persistAction)
        {
            IEnumerable<ParameterTest> untestedPairsTests = parameterTests.Where(t => t.Type == ParameterType.PairsCount)
                                                                          .Where(t => lastResult.ParameterOptimization.Intervals?.Any(t.Matches) != true);
            foreach (ParameterTest pairsTest in untestedPairsTests)
            {
                string pairs = string.Join(" ", lastResult.AllPairs.Take(pairsTest.Value));
                BackTestingResult result = ToolBox.BackTesting(options.Interval, pairsTest.EndDate, pairsTest.StartDate,
                                                               configFile, pairs, options.PairsTestOpenTrades,
                                                               options.Strategy);
                lastResult.ParameterOptimization.Intervals = (lastResult.ParameterOptimization.Intervals??Enumerable.Empty<ParameterInterval>())
                                                            .Concat(new []{new ParameterInterval
                                                             {
                                                                 ParameterType = pairsTest.Type,
                                                                 ParameterValue = pairsTest.Value,
                                                                 Result = result.ConvertToIntervalResult(pairsTest.StartDate, pairsTest.EndDate) 
                                                             }})
                                                            .ToArray();
                persistAction();
            }

            ParameterScore[] scores =
                CalculateScores(
                    lastResult.ParameterOptimization.Intervals.Where(i => i.ParameterType == ParameterType.PairsCount));
            int optimalPairs = scores.OrderByDescending(s => s.Score).First().Value;
            
            IEnumerable<ParameterTest> untestedOpenTradesTests = parameterTests.Where(t => t.Type == ParameterType.MaxOpenTrades)
                                                                          .Where(t => lastResult.ParameterOptimization.Intervals?.Any(t.Matches) != true);
            string optimalPairsSet = string.Join(" ", lastResult.AllPairs.Take(optimalPairs));
            foreach (ParameterTest openTradesTest in untestedOpenTradesTests)
            {
                BackTestingResult result = ToolBox.BackTesting(options.Interval, openTradesTest.EndDate, openTradesTest.StartDate,
                                                               configFile, optimalPairsSet, openTradesTest.Value,
                                                               options.Strategy);
                lastResult.ParameterOptimization.Intervals = (lastResult.ParameterOptimization.Intervals??Enumerable.Empty<ParameterInterval>())
                                                            .Concat(new []{new ParameterInterval
                                                             {
                                                                 ParameterType = openTradesTest.Type,
                                                                 ParameterValue = openTradesTest.Value,
                                                                 Result = result.ConvertToIntervalResult(openTradesTest.StartDate, openTradesTest.EndDate) 
                                                             }})
                                                            .ToArray();
                persistAction();
            }
        }

        private static ParameterScore[] CalculateScores(IEnumerable<ParameterInterval> parameterIntervals)
        {
            IEnumerable<IGrouping<ParameterScoreId, ParameterInterval>> grouping = parameterIntervals.GroupBy(i => new ParameterScoreId(i.ParameterType, i.Result.StartDate));
            Dictionary<ParameterId, int> scores = new();
            foreach (IGrouping<ParameterScoreId,ParameterInterval> intervals in grouping)
            {
                ParameterInterval[] ordered = intervals.OrderBy(i => i.Result.Profit - i.Result.DrawDown)
                                                       .ToArray();
                for (int i = 0; i < ordered.Length; i++)
                {
                    ParameterId id = new(ordered[i].ParameterType, ordered[i].ParameterValue);
                    scores[id] = scores.TryGetValue(id, out int score) ? score + i : i;
                }
            }

            return scores.Select(kv => new ParameterScore(kv.Key.Type, kv.Key.Value, kv.Value))
                         .ToArray();
        }

        private static void SaveResult(ParameterOptimizationTestResult result, string resultFile)
        {
            File.WriteAllText(resultFile, JsonConvert.SerializeObject(result, Formatting.Indented), Encoding.UTF8);
        }

        private static ParameterOptimizationTestResult GetLastResult(ParameterOptimizationOptions options,
                                                                     string resultFile,
                                                                     ParameterOptimizationTestResult
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
                           ? compareResult?.DataDownloaded != null
                                 ? new ParameterOptimizationTestResult
                                 {
                                     Strategy = options.Strategy,
                                     AllPairs = compareResult.AllPairs,
                                     DataDownloaded = true,
                                     EndDate = compareResult.EndDate
                                 }
                                 : new ParameterOptimizationTestResult
                                 {
                                     Strategy = options.Strategy
                                 }
                           : null;
            }

            return JsonConvert.DeserializeObject<ParameterOptimizationTestResult>(File.ReadAllText(resultFile));
        }

        private record ParameterScoreId(ParameterType Type, string StartDate);
        private record ParameterId(ParameterType Type, int Value);

        private record ParameterTest(string StartDate, string EndDate, ParameterType Type, int Value)
        {
            public bool Matches(ParameterInterval interval)
            {
                return interval.ParameterType == Type &&
                       interval.ParameterValue == Value &&
                       interval.Result.StartDate == StartDate &&
                       interval.Result.EndDate == EndDate;
            }
        }
    }
}