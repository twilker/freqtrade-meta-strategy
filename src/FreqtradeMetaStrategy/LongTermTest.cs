using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Serilog;

namespace FreqtradeMetaStrategy
{
    public static class LongTermTest
    {
        private const string ResultFolder = "./user_data/long-term-result";
        public static bool TestStrategy(LongTermTestOptions options)
        {
            string resultFile = Path.Combine(ResultFolder, $"{options.Tag}-result.json");
            LongTermResult lastResult = GetLastResult(options, resultFile);
            DateTime lastStartDate = GetLastStartDate(lastResult, out int completedIntervals);
            int intervalCount = (int) Math.Ceiling((double) options.TimeRange / options.Interval);
            if (completedIntervals <= intervalCount && !options.SkipDownload)
            {
                DownloadHistoryData(lastStartDate, completedIntervals, intervalCount, options);
            }
            while (completedIntervals <= intervalCount)
            {
                DateTime endDate = lastStartDate - new TimeSpan(1, 0, 0, 0);
                DateTime startDate = endDate - new TimeSpan(options.Interval,0,0,0);
                string endDateFormat = endDate.ToString("yyyyMMdd");
                string startDateFormat = startDate.ToString("yyyyMMdd");
                BackTestingResult result = BackTestInterval(options, endDateFormat, startDateFormat);
                UpdateResultFile(lastResult, result, resultFile, startDateFormat, endDateFormat);
                lastStartDate = startDate;
                completedIntervals++;
            }

            GenerateReport(lastResult, options);
            return true;
        }

        private static void DownloadHistoryData(DateTime endDate, int completedIntervals, int intervalCount,
                                                LongTermTestOptions options)
        {
            DateTime startDate =
                endDate - new TimeSpan(options.Interval * (intervalCount - completedIntervals) + 20, 0, 0, 0);
            string endDateFormat = endDate.ToString("yyyyMMdd");
            string startDateFormat = startDate.ToString("yyyyMMdd");
            bool result = ProcessFacade.Execute("freqtrade",
                                                $"download-data --data-format-ohlcv hdf5 -t 5m 1h --timerange {startDateFormat}-{endDateFormat} -c {options.ConfigFile}");
            if (!result)
            {
                throw new InvalidOperationException(
                    $"Unexpected failure of downloading data.");
            }
        }

        private static void GenerateReport(LongTermResult lastResult, LongTermTestOptions options)
        {
            using Stream embeddedStream = Assembly.GetExecutingAssembly()
                                                   .GetManifestResourceStream("FreqtradeMetaStrategy.ReportTemplate.html");
            if (embeddedStream == null)
            {
                throw new InvalidOperationException("Report template not found.");
            }
            using StreamReader reportTemplateStream = new(embeddedStream);
            string content = reportTemplateStream.ReadToEnd()
                                                 .Replace("$(StrategyName)", options.Strategy)
                                                 .Replace("$(TotalResult)", GenerateProfitChartData())
                                                 .Replace("$(PairProfit)", GeneratePairsChartData());
            string reportFile = Path.Combine(ResultFolder, $"{options.Tag}Report.html");
            File.WriteAllText(reportFile, content, Encoding.UTF8);
            
            string GenerateProfitChartData()
            {
                StringBuilder profitContent = StartChart("Total Profit", true);
                StringBuilder drawDownContent = StartChart("Draw Down", true);
                StringBuilder marketContent = StartChart("Market Change", true);
                foreach (IntervalResult intervalResult in lastResult.Results)
                {
                    AddData(profitContent, intervalResult.StartDate, intervalResult.Profit);
                    AddData(drawDownContent, intervalResult.StartDate, intervalResult.DrawDown);
                    AddData(marketContent, intervalResult.StartDate, intervalResult.MarketChange);
                }
                EndChart(profitContent);
                EndChart(drawDownContent);
                EndChart(marketContent);
                return $"{profitContent},{Environment.NewLine}" +
                       $"{drawDownContent},{Environment.NewLine}" +
                       $"{marketContent}";
            }
            
            string GeneratePairsChartData()
            {
                Dictionary<string, StringBuilder> pairsProfitBuilders = StartPairsProfitCharts();
                foreach (IntervalResult intervalResult in lastResult.Results)
                {
                    foreach (string key in pairsProfitBuilders.Keys)
                    {
                        AddData(pairsProfitBuilders[key], intervalResult.StartDate,
                                intervalResult.Pairs.FirstOrDefault(p => p.Pair == key)?.Profit ?? 0);
                    }
                }
                foreach (StringBuilder chartData in pairsProfitBuilders.Values)
                {
                    EndChart(chartData);
                }

                return string.Join($",{Environment.NewLine}", pairsProfitBuilders.Values);
            }

            StringBuilder StartChart(string title, bool visible)
            {
                StringBuilder chartData = new();
                chartData.AppendLine("{");
                chartData.AppendLine("showInLegend: true,");
                chartData.AppendLine("type: \"line\",");
                chartData.AppendLine($"name: \"{title}\",");
                chartData.AppendLine($"visible: {(visible?"true":"false")},");
                chartData.AppendLine("toolTipContent: \"{name} - {x}: {y}%\",");
                chartData.AppendLine("dataPoints: [");
                return chartData;
            }

            void EndChart(StringBuilder chartData)
            {
                chartData.AppendLine("]");
                chartData.AppendLine("}");
            }
            
            void AddData(StringBuilder chartData, string date, double value)
            {
                chartData.AppendLine(
                    $"{{ x: new Date({date[new Range(0, 4)]}, {date[new Range(4, 6)]}, {date[new Range(6, 8)]}), y: {value} }},");
            }
            
            Dictionary<string, StringBuilder> StartPairsProfitCharts()
            {
                return lastResult.Results.FirstOrDefault(r => r.Pairs.Any())
                                ?.Pairs.ToDictionary(p => p.Pair, p => StartChart(p.Pair, false))
                       ?? new Dictionary<string, StringBuilder>();
            }
        }

        private static void UpdateResultFile(LongTermResult lastResult, BackTestingResult result, string resultFile,
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
            File.WriteAllText(resultFile, JsonConvert.SerializeObject(lastResult, Formatting.Indented), Encoding.UTF8);
        }

        private static BackTestingResult BackTestInterval(LongTermTestOptions options, string endDate, string startDate)
        {
            bool result = ProcessFacade.Execute("freqtrade",
                                                $"backtesting --data-format-ohlcv hdf5  --timerange {startDate}-{endDate} -s {options.Strategy} -c {options.ConfigFile}",
                                                out StringBuilder output);
            if (!result)
            {
                throw new InvalidOperationException(
                    $"Unexpected failure of back testing strategy {options.Strategy}.");
            }

            BackTestingResult newResult = ToolBox.EvaluateBackTestingResult(output.ToString(), options.Strategy, options.Interval, false);
            return newResult;
        }

        private static DateTime GetLastStartDate(LongTermResult lastResult, out int completedIntervals)
        {
            completedIntervals = 0;
            if (!lastResult.Results.Any())
            {
                return DateTime.Today;
            }

            completedIntervals = lastResult.Results.Length;
            string date = lastResult.Results.Last().StartDate;
            return new DateTime(int.Parse(date[new Range(0,4)]),
                                int.Parse(date[new Range(4,6)]),
                                int.Parse(date[new Range(6,8)]));
        }

        private static LongTermResult GetLastResult(LongTermTestOptions options, string resultFile)
        {
            FileInfo fileInfo = new(resultFile);
            if (!fileInfo.Directory?.Exists != true)
            {
                fileInfo.Directory?.Create();
            }

            if (!fileInfo.Exists)
            {
                return new LongTermResult
                {
                    Strategy = options.Strategy
                };
            }

            return JsonConvert.DeserializeObject<LongTermResult>(File.ReadAllText(resultFile));
        }
    }
}