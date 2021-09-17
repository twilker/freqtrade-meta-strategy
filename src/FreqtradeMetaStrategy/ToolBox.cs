using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Serilog;

namespace FreqtradeMetaStrategy
{
    public static class ToolBox
    {
        private static readonly ILogger ClassLogger = Log.ForContext(typeof(ToolBox));
        
        public static void WriteReport(string file, string resourceKey, Func<string, string> transformator)
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
        
        public static void DeployResource(string file, string resource)
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
        
        private static readonly Regex ResultFileParser =
            new Regex(@"dumping json to ""(?<file_name>.*backtest-result.*\.json)""", RegexOptions.Compiled);
        public static BackTestingResult EvaluateBackTestingResult(string output, string strategyName, int daysCount, bool isUnstableStake, bool parseTrades = false)
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
            BackTestingResult result = new(strategyName, dailyProfit, tradesPerDay, pairProfits, isUnstableStake,
                                           drawDown, market, totalProfit);
            if (!parseTrades)
            {
                return result;
            }

            Match resultFileMatch = ResultFileParser.Match(output);
            if (resultFileMatch.Success)
            {
                ParseTrades(resultFileMatch.Groups["file_name"].Value, result);
            }
            return result;
        }

        private static void ParseTrades(string resultFile, BackTestingResult result)
        {
            JObject resultDocument = JObject.Parse(File.ReadAllText(resultFile, Encoding.UTF8));
            JArray trades = resultDocument.Descendants().OfType<JProperty>()
                                          .FirstOrDefault(p => p.Name == "trades" && p.Value is JArray)
                                         ?.Value as JArray;
            if (trades == null)
            {
                return;
            }

            result.Trades = trades.OfType<JObject>()
                                  .Select(trade => new BackTestTrade(trade.Property("pair")?.Value.Value<string>(),
                                                                     trade.Property("open_date")?.Value
                                                                          .Value<DateTime>() ?? default)).ToArray();
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

        private static readonly Regex PairMatcher =
            new Regex(@"'(?<pair>[0-9A-Za-z]+\/[0-9A-Za-z]+)'", RegexOptions.Compiled);

        public static void FindPairsDownloadAndSetConfig(string configFileLocation, bool findPairs, bool downloadData,
                                                         int timeRange, int interval, string timeframe, Action<string[]> setPairs,
                                                         Action<DateTime> setDataDownloaded, Func<string[]> getAllPairs,
                                                         int maxPairsDownloaded = int.MaxValue)
        {
            DeployConfig(configFileLocation);
            if (findPairs)
            {
                string[] pairs = FindTradablePairs(configFileLocation);
                setPairs(pairs);
            }

            if (downloadData)
            {
                DateTime endDate = DownloadData(timeRange, interval, getAllPairs, configFileLocation, timeframe, maxPairsDownloaded);
                setDataDownloaded(endDate);
            }

            DeployStaticConfig(configFileLocation);
        }

        private static DateTime DownloadData(int timeRange, int interval,
                                             Func<string[]> getAllPairs, string configFile,
                                             string timeframe, int maxPairsDownloaded)
        {
            string[] allPairs = getAllPairs();
            string pairs = string.Join(" ", allPairs.Take(Math.Min(maxPairsDownloaded, allPairs.Length)));
            int intervalCount = (int) Math.Ceiling((double) timeRange / interval);
            DateTime endDate = DateTime.Today;
            DateTime startDate = endDate - new TimeSpan(interval * intervalCount + 20, 0, 0, 0);
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

        private static string[] FindTradablePairs(string configFile)
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

            return pairs.ToArray();
        }

        private static void DeployStaticConfig(string configFile)
        {
            ToolBox.DeployResource(configFile, "FreqtradeMetaStrategy.blacklist-template-static-config.json");
        }

        private static void DeployConfig(string configFile)
        {
            ToolBox.DeployResource(configFile, "FreqtradeMetaStrategy.blacklist-template-config.json");
        }

        public static BackTestingResult BackTesting(int daysCount, string endDate, string startDate,
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

        public static IntervalResult ConvertToIntervalResult(this BackTestingResult result, string startDate, string endDate)
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

        public static void EndChart(StringBuilder chartData)
        {
            chartData.AppendLine("]");
            chartData.AppendLine("}");
        }

        public static StringBuilder StartChart(string title, bool visible)
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

        public static void AddData(StringBuilder chartData, string date, double value)
        {
            chartData.AppendLine($"{{ x: new Date({date[new Range(0, 4)]}, {date[new Range(4, 6)]}, {date[new Range(6, 8)]}), y: {value} }},");
        }
    }
}