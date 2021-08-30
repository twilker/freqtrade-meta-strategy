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
            return true;
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

        private static void DeployConfig(string configFile)
        {
            FileInfo fileInfo = new(configFile);
            if (!fileInfo.Directory?.Exists != true)
            {
                fileInfo.Directory?.Create();
            }

            if (fileInfo.Exists)
            {
                fileInfo.Delete();
            }

            using Stream resourceStream = Assembly.GetExecutingAssembly()
                                                  .GetManifestResourceStream("FreqtradeMetaStrategy.blacklist-template-config.json");
            using Stream fileStream = fileInfo.OpenWrite();
            if (resourceStream == null)
            {
                throw new InvalidOperationException("config template not found.");
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