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

            ScoresResult scoresResult = CalculateScores(lastResult.ParameterOptimization.Intervals);
            lastResult.Scores = scoresResult.Scores;
            lastResult.AccumulatedScores = scoresResult.AccumulatedScores;
            lastResult.HistoricScores = scoresResult.HistoricScores;
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
                                                                   .ToArray(),
                                                                    lastResult.AccumulatedScores.Where(score => score.Type == ParameterType.MaxOpenTrades)
                                                                              .ToArray()))
                                                       .Replace("$(PairsCountChart)", GenerateChart(lastResult.Scores.Where(score => score.Type == ParameterType.PairsCount)
                                                                   .ToArray(),
                                                                    lastResult.AccumulatedScores.Where(score => score.Type == ParameterType.PairsCount)
                                                                              .ToArray()))
                                                       .Replace("$(HistoricOpenTradesChart)", GenerateHistoryChart(lastResult.HistoricScores.Where(score => score.Type == ParameterType.MaxOpenTrades)
                                                                   .ToArray()))
                                                       .Replace("$(HistoricPairsCountChart)", GenerateHistoryChart(lastResult.HistoricScores.Where(score => score.Type == ParameterType.PairsCount)
                                                                   .ToArray()));
            ToolBox.WriteReport(report, "FreqtradeMetaStrategy.ParameterScoresReportTemplate.html", transformator);
            
            string GenerateChart(ParameterScore[] parameterScores, ParameterScore[] accumulatedScores)
            {
                StringBuilder scores = ToolBox.StartChart("Score", true);
                StringBuilder winners = ToolBox.StartChart("Wins", true);
                StringBuilder accumulated = ToolBox.StartChart("Accumulated Profit", true);
                foreach (ParameterScore score in parameterScores.OrderBy(s => s.Value))
                {
                    scores.AppendLine($"{{ x: {score.Value}, y: {score.Score} }},");
                    winners.AppendLine($"{{ x: {score.Value}, y: {score.Winner} }},");
                }
                foreach (ParameterScore score in accumulatedScores.OrderBy(s => s.Value))
                {
                    accumulated.AppendLine($"{{ x: {score.Value}, y: {score.Score} }},");
                }
                ToolBox.EndChart(scores);
                ToolBox.EndChart(winners);
                ToolBox.EndChart(accumulated);
                return scores + "," + Environment.NewLine + winners + "," + Environment.NewLine + accumulated;
            }
            
            string GenerateHistoryChart(HistoricParameterScore[] parameterScores)
            {
                HistoryChart[] charts = parameterScores.GroupBy(s => s.Value)
                                                       .OrderBy(g => g.Key)
                                                       .Select(g => new HistoryChart(
                                                                   ToolBox.StartChart(g.Key.ToString("D"), false),
                                                                   g.ToArray()))
                                                       .ToArray();
                foreach (HistoryChart chart in charts)
                {
                    foreach (HistoricParameterScore score in chart.Scores.OrderBy(s => s.Date))
                    {
                        ToolBox.AddData(chart.Chart, score.Date, score.Score);
                    }
                    ToolBox.EndChart(chart.Chart);
                }

                return string.Join("," + Environment.NewLine, charts.Select(c => c.Chart.ToString()));
            }
        }

        private record HistoryChart(StringBuilder Chart, HistoricParameterScore[] Scores);

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

            ScoresResult scores =
                CalculateScores(
                    lastResult.ParameterOptimization.Intervals.Where(i => i.ParameterType == ParameterType.PairsCount));
            int optimalPairs = scores.Scores.OrderByDescending(s => s.Score).First().Value;
            
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

        private static ScoresResult CalculateScores(IEnumerable<ParameterInterval> parameterIntervals)
        {
            parameterIntervals = parameterIntervals.ToArray();
            IEnumerable<IGrouping<ParameterScoreId, ParameterInterval>> grouping = parameterIntervals.GroupBy(i => new ParameterScoreId(i.ParameterType, i.Result.StartDate));
            IEnumerable<IGrouping<ParameterId, ParameterInterval>> valueGrouping = parameterIntervals.GroupBy(i => new ParameterId(i.ParameterType, i.ParameterValue));
            Dictionary<ParameterId, int> scores = new();
            Dictionary<ParameterId, int> winners = new();
            Dictionary<HistoricParameterId, int> historicScores = new();
            int openTradesValuesCount = parameterIntervals.Where(i => i.ParameterType == ParameterType.MaxOpenTrades)
                                                          .Select(i => i.ParameterValue)
                                                          .Distinct()
                                                          .Count();
            int maximumOpenTradesWinners = parameterIntervals.Where(i => i.ParameterType == ParameterType.MaxOpenTrades)
                                                             .Select(i => i.Result.StartDate)
                                                             .Distinct()
                                                             .Count();
            int maximumOpenTradesScore = openTradesValuesCount * maximumOpenTradesWinners;
            int pairsValuesCount = parameterIntervals.Where(i => i.ParameterType == ParameterType.PairsCount)
                                                          .Select(i => i.ParameterValue)
                                                          .Distinct()
                                                          .Count();
            int maximumPairsWinners = parameterIntervals.Where(i => i.ParameterType == ParameterType.PairsCount)
                                                             .Select(i => i.Result.StartDate)
                                                             .Distinct()
                                                             .Count();
            int maximumPairsScore = pairsValuesCount * maximumPairsWinners;
            foreach (IGrouping<ParameterScoreId,ParameterInterval> intervals in grouping)
            {
                ParameterInterval[] ordered = intervals.OrderBy(i => i.Result.Profit - i.Result.DrawDown)
                                                       .ToArray();
                for (int i = 0; i < ordered.Length; i++)
                {
                    ParameterId id = new(ordered[i].ParameterType, ordered[i].ParameterValue);
                    scores[id] = scores.TryGetValue(id, out int score) ? score + i + 1 : i + 1;
                    if (i == ordered.Length-1)
                    {
                        winners[id] = winners.TryGetValue(id, out int winner) ? winner + 1 : 1;
                    }

                    HistoricParameterId historicId = new(ordered[i].ParameterType, ordered[i].ParameterValue,
                                                         ordered[i].Result.StartDate);
                    historicScores[historicId] = i + 1;
                }
            }

            Dictionary<ParameterId, double> accumulated = valueGrouping.ToDictionary(
                intervals => intervals.Key,
                intervals =>
                    intervals.Aggregate<ParameterInterval, double>(
                        1, (current, interval) => current * (1 + interval.Result.Profit / 100)));

            ParameterScore[] scoresResult = scores.Select(kv => new ParameterScore(kv.Key.Type, kv.Key.Value,
                                                                                   NormalizedScore(kv), NormalizedWinner(kv)))
                                                  .ToArray();
            HistoricParameterScore[] historicScoresResult = historicScores.Select(kv => new HistoricParameterScore(
                                                                               kv.Key.Type, kv.Key.Value,
                                                                               NormalizedHistoricScore(kv),
                                                                               kv.Key.Date))
                                                                          .ToArray();
            ParameterScore[] accumulatedScores = accumulated.Select(kv => new ParameterScore(kv.Key.Type, kv.Key.Value, NormalizedAccumulatedScore(kv), 0))
                                                           .ToArray();
            return new ScoresResult(scoresResult, accumulatedScores, historicScoresResult);

            double NormalizedScore(KeyValuePair<ParameterId, int> kv)
            {
                int maximumScore = kv.Key.Type == ParameterType.MaxOpenTrades
                                       ? maximumOpenTradesScore
                                       : maximumPairsScore;
                return ((double)kv.Value / maximumScore) * 100;
            }

            double NormalizedHistoricScore(KeyValuePair<HistoricParameterId, int> kv)
            {
                int maximumScore = kv.Key.Type == ParameterType.MaxOpenTrades
                                       ? openTradesValuesCount
                                       : pairsValuesCount;
                return ((double)kv.Value / maximumScore) * 100;
            }
            
            double NormalizedWinner(KeyValuePair<ParameterId, int> kv)
            {
                int maximumWinner = kv.Key.Type == ParameterType.MaxOpenTrades
                                       ? maximumOpenTradesWinners
                                       : maximumPairsWinners;
                int totalWinner = winners.TryGetValue(kv.Key, out int winner) ? winner : 0;
                return ((double)totalWinner / maximumWinner) * 100;
            }

            double NormalizedAccumulatedScore(KeyValuePair<ParameterId, double> kv)
            {
                double maximumScore = kv.Key.Type == ParameterType.MaxOpenTrades
                                        ? accumulated.Where(s => s.Key.Type == ParameterType.MaxOpenTrades).Max(s => s.Value)
                                        : accumulated.Where(s => s.Key.Type == ParameterType.PairsCount).Max(s => s.Value);
                return (kv.Value / maximumScore) * 100;
            }
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
        private record HistoricParameterId(ParameterType Type, int Value, string Date);
        private record ScoresResult(ParameterScore[] Scores, ParameterScore[] AccumulatedScores, HistoricParameterScore[] HistoricScores);

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