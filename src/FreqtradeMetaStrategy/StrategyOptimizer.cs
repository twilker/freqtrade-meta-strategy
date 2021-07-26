using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using RestSharp.Serialization.Json;
using Serilog;
using Serilog.Core;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace FreqtradeMetaStrategy
{
    //TODO
    //try with all strategies
    //Luck max 5% for pairs profit
    //config changes as extra config
    //Change to agents net
    //validate old results
    //consider trade volume based on wallet size/5
    //try strategy evaluation based on 6/3 days
    //Zineszins formula for days to double wallet 
    internal static class StrategyOptimizer
    {
        private const string StrategyRepoLocation = "./user_data/strategies_source";
        private const string StrategyRepoStrategiesLocation = "./user_data/strategies_source/user_data/strategies";
        private const string StrategiesAppLocation = "./user_data/strategies";
        private const int DefaultBackTestingTimeRange = 3;
        private static readonly ILogger ClassLogger = Log.ForContext(typeof(StrategyOptimizer));
        private static readonly Regex StrategyRecognizer = new(@"^\w\S*$", RegexOptions.Compiled);
        private static readonly Regex PaginationLinks = new(@"\<(?<link>[^\>]*)\>;\s*rel=""(?<type>[^""]*)""", RegexOptions.Compiled);
        private static readonly Regex Stoploss = new(@"stoploss\s*=\s*(?<stoploss>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
        private static readonly Regex TrailingStop = new(@"trailing_stop\s*=\s*(?<trailing_stop>True|False)", RegexOptions.Compiled);
        private static readonly Regex TrailingStopPositive = new(@"trailing_stop_positive\s*=\s*(?<trailing_stop_positive>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
        private static readonly Regex TrailingStopPositiveOffset = new(@"trailing_stop_positive_offset\s*=\s*(?<trailing_stop_positive_offset>-?\d+(?:\.\d*)?)", RegexOptions.Compiled);
        private static readonly Regex TrailingOnlyOffsetIsReached = new(@"trailing_only_offset_is_reached\s*=\s*(?<trailing_only_offset_is_reached>True|False)", RegexOptions.Compiled);

        public static int Optimize(FindOptimizedStrategiesOptions options)
        {
            ProgramConfiguration programConfiguration = ParseConfiguration(options);
            JObject originalConfig = ParseOriginalConfig();
            bool result = UpdateStrategyRepository(programConfiguration, out string[] strategies);
            if (options.Strategies?.Any() == true)
            {
                strategies = strategies.Intersect(options.Strategies).ToArray();
            }
            ClassLogger.Information($"Found {strategies.Length} strategies.");
            
            Ticker[] unstableStake = Array.Empty<Ticker>(), stableStake = Array.Empty<Ticker>();
            BackTestingResult[] testingResults = Array.Empty<BackTestingResult>();
            OptimizedStrategy[] optimized = Array.Empty<OptimizedStrategy>();
            if (result)
            {
                result = RetrieveTradingPairSets(programConfiguration, out unstableStake, out stableStake);
            }

            if (result)
            {
                result = DownloadDataForBackTesting(unstableStake, stableStake);
            }

            if (result)
            {
                result = BatchBackTestAllStrategies(strategies, unstableStake, stableStake, programConfiguration, originalConfig,out testingResults);
            }

            if (result)
            {
                result = OptimizeStrategies(testingResults, programConfiguration, originalConfig, out optimized);
            }

            ClassLogger.Information($"Results ({unstableStake.Length} unstable pairs|{stableStake.Length} stable pairs):");
            foreach (OptimizedStrategy strategy in optimized)
            {
                ClassLogger.Information(strategy.ToString());
                List<Ticker> sortedTickers =
                    GetVolumeSortedTickers(strategy.BackTestingResult.IsUnstableStake ? unstableStake : stableStake);
                foreach (Ticker ticker in strategy.BackTestingResult.PairsProfit.Select(pair => sortedTickers.First(t => t.ToTradingPairString() == pair.Key)))
                {
                    ClassLogger.Verbose($"{sortedTickers.IndexOf(ticker)} - {ticker}");
                }
            }
            
            RestoreOriginalConfig();
            
            return result ? 0 : 1;

            JObject ParseOriginalConfig()
            {
                using FileStream openStream =
                    File.Open(Path.Combine("user_data", "config.json"), FileMode.Open, FileAccess.Read);
                using StreamReader reader = new(openStream, Encoding.Default);
                using JsonReader jsonReader = new JsonTextReader(reader);
                originalConfig = JObject.Load(jsonReader);

                return originalConfig;
            }

            void RestoreOriginalConfig()
            {
                using FileStream writeStream = File.Open(Path.Combine("user_data", "config.json"), FileMode.Open, FileAccess.Write);
                writeStream.SetLength(0);
                using StreamWriter writer = new(writeStream, Encoding.Default);
                writer.Write(originalConfig.ToString(Formatting.Indented));
            }
            
            List<Ticker> GetVolumeSortedTickers(Ticker[] tickers)
            {
                return tickers.OrderByDescending(t => t.Volume)
                              .ToList();
            }
        }

        private static bool OptimizeStrategies(BackTestingResult[] testingResults,
                                               ProgramConfiguration programConfiguration, JObject originalConfig,
                                               out OptimizedStrategy[] optimizedStrategies)
        {
            ClassLogger.Information($"Back the {programConfiguration.MaxStrategySuggestions} best strategies.");

            BackTestingResult[] optimizingStrategies = testingResults.OrderByDescending(r => r.ProfitPerDay)
                                                                     .Take(programConfiguration.MaxStrategySuggestions)
                                                                     .ToArray();
            try
            {
                optimizedStrategies = optimizingStrategies.Select(Optimize)
                                                          .ToArray();
                
                ClassLogger.Information($"Optimized all strategies.");
                return true;
            }
            catch (Exception e)
            {
                optimizedStrategies = null;
                ClassLogger.Error(e, $"Error while optimizing all strategies. {e}");
                return false;
            }

            OptimizedStrategy Optimize(BackTestingResult testingResult)
            {
                ClassLogger.Information($"Optimize {testingResult.Strategy}.");
                JObject optimizedConfig;
                IEnumerable<string> pairs = testingResult.PairsProfit.Keys;
                string stakeCoin = testingResult.PairsProfit.First().Key.Split("/").Last();
                optimizedConfig = ManipulateConfiguration(originalConfig, config =>
                {
                    config["exchange"]["pair_whitelist"] = new JArray(pairs);
                    config["stake_currency"] = stakeCoin;
                });
                optimizedConfig = HyperoptStrategy(optimizedConfig, ref testingResult);
                BackTestingResult shortTermResult = ReevaluateBackTestingResult(programConfiguration, testingResult, programConfiguration.StrategyRunningDays);

                StoreOptimizedConfig();

                return new OptimizedStrategy(testingResult.Strategy, testingResult.ProfitPerDay, shortTermResult.ProfitPerDay, testingResult);

                void StoreOptimizedConfig()
                {
                    string configPath =
                        Path.Combine("user_data", "strategies", $"{testingResult.Strategy}_Config.json");
                    using FileStream writeStream = File.Open(configPath, FileMode.Create, FileAccess.Write);
                    using StreamWriter writer = new(writeStream, Encoding.Default);
                    writer.Write(optimizedConfig.ToString(Formatting.Indented));
                    writer.Flush();
                    ClassLogger.Information($"Stored strategy config in {Path.Combine("user_data", "strategies", $"{testingResult.Strategy}_Config.json")}");
                }
                
                JObject HyperoptStrategy(JObject config, ref BackTestingResult testingResult)
                {
                    ClassLogger.Information($"Hyperopt stoploss.");
                    bool result = ProcessFacade.Execute("freqtrade",
                                                        $"hyperopt --hyperopt-loss {programConfiguration.HyperoptFunction} --spaces stoploss trailing --strategy {testingResult.Strategy} --data-format-ohlcv hdf5 --dry-run-wallet {(testingResult.IsUnstableStake?programConfiguration.UnstableCoinWallet:programConfiguration.StableCoinWallet)} -e 100",
                                                        out StringBuilder outputBuilder);
                    if (!result)
                    {
                        ClassLogger.Information($"Hyperopt failed.");
                        return config;
                    }
                    string output = outputBuilder.ToString();
                    Match match = Stoploss.Match(output);
                    if (!match.Success ||
                        !double.TryParse(match.Groups["stoploss"].Value, out double stoploss))
                    {
                        ClassLogger.Information($"Hyperopt stoploss could not be parsed: {(match.Success?match.Groups["stoploss"].Value:"False")}");
                        ClassLogger.Verbose(output);
                        return config;
                    }

                    match = TrailingStop.Match(output);
                    if (!match.Success ||
                        !bool.TryParse(match.Groups["trailing_stop"].Value, out bool trailingStop))
                    {
                        ClassLogger.Information($"Hyperopt trailing_stop could not be parsed: {(match.Success?match.Groups["trailing_stop"].Value:"False")}");
                        ClassLogger.Verbose(output);
                        return config;
                    }

                    match = TrailingStopPositive.Match(output);
                    if (!match.Success ||
                        !double.TryParse(match.Groups["trailing_stop_positive"].Value, out double trailingStopPositive))
                    {
                        ClassLogger.Information($"Hyperopt trailing_stop_positive could not be parsed: {(match.Success?match.Groups["trailing_stop_positive"].Value:"False")}");
                        ClassLogger.Verbose(output);
                        return config;
                    }

                    match = TrailingStopPositiveOffset.Match(output);
                    if (!match.Success ||
                        !double.TryParse(match.Groups["trailing_stop_positive_offset"].Value, out double trailingStopPositiveOffset))
                    {
                        ClassLogger.Information($"Hyperopt trailing_stop_positive_offset could not be parsed: {(match.Success?match.Groups["trailing_stop_positive_offset"].Value:"False")}");
                        ClassLogger.Verbose(output);
                        return config;
                    }

                    match = TrailingOnlyOffsetIsReached.Match(output);
                    if (!match.Success ||
                        !bool.TryParse(match.Groups["trailing_only_offset_is_reached"].Value, out bool trailingOnlyOffsetIsReached))
                    {
                        ClassLogger.Information($"Hyperopt trailing_only_offset_is_reached could not be parsed: {(match.Success?match.Groups["trailing_only_offset_is_reached"].Value:"False")}");
                        ClassLogger.Verbose(output);
                        return config;
                    }

                    JObject newConfig = ManipulateConfiguration(config, o =>
                    {
                        if (o.ContainsKey("stoploss"))
                        {
                            o["stoploss"] = stoploss;
                        }
                        else
                        {
                            o.Add("stoploss", stoploss);
                        }
                        if (o.ContainsKey("trailing_stop"))
                        {
                            o["trailing_stop"] = trailingStop;
                        }
                        else
                        {
                            o.Add("trailing_stop", trailingStop);
                        }
                        if (o.ContainsKey("trailing_stop_positive"))
                        {
                            o["trailing_stop_positive"] = trailingStopPositive;
                        }
                        else
                        {
                            o.Add("trailing_stop_positive", trailingStopPositive);
                        }
                        if (o.ContainsKey("trailing_stop_positive_offset"))
                        {
                            o["trailing_stop_positive_offset"] = trailingStopPositiveOffset;
                        }
                        else
                        {
                            o.Add("trailing_stop_positive_offset", trailingStopPositiveOffset);
                        }
                        if (o.ContainsKey("trailing_only_offset_is_reached"))
                        {
                            o["trailing_only_offset_is_reached"] = trailingOnlyOffsetIsReached;
                        }
                        else
                        {
                            o.Add("trailing_only_offset_is_reached", trailingOnlyOffsetIsReached);
                        }
                    });
                    
                    BackTestingResult optimizedResult = ReevaluateBackTestingResult(programConfiguration, testingResult,DefaultBackTestingTimeRange);
                    if (optimizedResult.ProfitPerDay > testingResult.ProfitPerDay)
                    {
                        ClassLogger.Information($"Hyperopt successfully {optimizedResult}.");
                        testingResult = optimizedResult;
                        return newConfig;
                    }

                    ClassLogger.Information($"Hyperopt performed bad ({optimizedResult.ProfitPerDay} < {testingResult.ProfitPerDay}).");
                    return config;
                }
            }
        }

        private static BackTestingResult ReevaluateBackTestingResult(ProgramConfiguration programConfiguration,
                                                                 BackTestingResult testingResult, int testRange)
        {
            DateTime startDate = DateTime.Today - new TimeSpan(testRange, 0, 0, 0);
            bool result = ProcessFacade.Execute("freqtrade",
                                                $"backtesting --data-format-ohlcv hdf5 --dry-run-wallet {(testingResult.IsUnstableStake ? programConfiguration.UnstableCoinWallet : programConfiguration.StableCoinWallet)} --timerange {startDate:yyyyMMdd}- -s {testingResult.Strategy}",
                                                out StringBuilder output);
            if (!result)
            {
                throw new InvalidOperationException(
                    $"Unexpected failure of back testing already tested strategy {testingResult.Strategy}.");
            }

            BackTestingResult newResult = ToolBox.EvaluateBackTestingResult(output.ToString(), testingResult.Strategy,
                                                                       DefaultBackTestingTimeRange, testingResult.IsUnstableStake);
            return newResult;
        }

        private static bool BatchBackTestAllStrategies(string[] strategies, Ticker[] unstableStake,
                                                       Ticker[] stableStake,
                                                       ProgramConfiguration programConfiguration,
                                                       JObject originalConfig,
                                                       out BackTestingResult[] filteredResults)
        {
            ClassLogger.Information($"Batch test strategies.");
            List<BackTestingResult> testResults = new();
            filteredResults = null;
            if (!BatchTestStrategies(unstableStake, true))
            {
                return false;
            }
            if (!BatchTestStrategies(stableStake, false))
            {
                return false;
            }

            filteredResults = FilterResults(testResults, programConfiguration);
            ClassLogger.Information($"Filtered back testing results:");
            foreach (BackTestingResult result in filteredResults)
            {
                ClassLogger.Information(result.ToString());
            }
            return true;

            bool BatchTestStrategies(Ticker[] stake, bool isUnstableStake)
            {
                int batches = stake.Length / programConfiguration.BackTestingPairsBatchSize;
                int startIndex = 0;
                BatchResultCollector collector = new(stake);
                for (int i = 0; i < batches; i++)
                {
                    int batchSize = i == batches - 1
                                        ? stake.Length - startIndex
                                        : programConfiguration.BackTestingPairsBatchSize;
                    Ticker[] batch = stake.AsSpan(startIndex, batchSize).ToArray();
                    startIndex += batchSize;
                    BackTestingResult[] batchResults;
                    bool result = isUnstableStake
                                      ? BackTestAllStrategies(strategies, batch, Array.Empty<Ticker>(), programConfiguration,
                                                                       originalConfig, out batchResults)
                                           :BackTestAllStrategies(strategies, Array.Empty<Ticker>(), batch, programConfiguration,
                                                                  originalConfig, out batchResults);
                    if (!result)
                    {
                        return false;
                    }

                    collector.Collect(batchResults);
                }

                ClassLogger.Verbose($"Batch test results:");
                collector.Log(ClassLogger);
                foreach (string strategy in strategies)
                {
                    ClassLogger.Verbose($"Test optimal batch for {strategy}.");
                    Ticker[] bestPairs = collector.GetStrategyTickers(strategy, programConfiguration.BackTestingPairsBatchSize)
                                                  .ToArray();
                    ClassLogger.Verbose($"Test best pairs for {strategy}:");
                    foreach (Ticker pair in bestPairs)
                    {
                        ClassLogger.Verbose($"{pair.ToTradingPairString()}: {pair.Volume}");
                    }

                    ManipulateConfiguration(originalConfig, config => config["stake_currency"] = bestPairs[0].Target);
                    string pairs = string.Join(" ", bestPairs.Select(t => t.ToTradingPairString()));
                    DateTime startDate = DateTime.Today - new TimeSpan(DefaultBackTestingTimeRange, 0, 0, 0);
                    bool result = ProcessFacade.Execute("freqtrade",
                                                        $"backtesting --data-format-ohlcv hdf5 --dry-run-wallet {(isUnstableStake?programConfiguration.UnstableCoinWallet:programConfiguration.StableCoinWallet)} --timerange {startDate:yyyyMMdd}- -p {pairs} -s {strategy}",
                                                        out StringBuilder output);
                    if (!result)
                    {
                        ClassLogger.Verbose($"Error while finding optimal package for {strategy}.");
                        continue;
                    }

                    testResults.Add(ToolBox.EvaluateBackTestingResult(output.ToString(), strategy, DefaultBackTestingTimeRange, true));
                }

                return true;
            }
        }
        
        private static bool BackTestAllStrategies(string[] strategies, Ticker[] unstableStake, Ticker[] stableStake,
                                                  ProgramConfiguration programConfiguration, JObject originalConfig,
                                                  out BackTestingResult[] filteredResults)
        {
            try
            {
                bool result = BackTestForCoin(programConfiguration.UnstableStakeCoin, unstableStake, programConfiguration.UnstableCoinWallet, true, out BackTestingResult[] unstableResults);
                filteredResults = unstableResults;

                if (result)
                {
                    result = BackTestForCoin(programConfiguration.StableStakeCoin, stableStake, programConfiguration.StableCoinWallet, false, out BackTestingResult[] stableResults);
                    filteredResults = unstableResults.Concat(stableResults).ToArray();
                }

                if (result)
                {
                    ClassLogger.Information($"Valid strategies:");
                    foreach (BackTestingResult testingResult in filteredResults)
                    {
                        ClassLogger.Information(testingResult.ToString());
                    }
                }
                
                return result;
            }
            catch (Exception e)
            {
                filteredResults = null;
                ClassLogger.Error(e, $"Error while back testing. {e}");
                return false;
            }
            
            BackTestingResult[] EvaluateBackTestingResults(string output, int daysCount, IEnumerable<string> strategies, bool isUnstableStake)
            {
                return strategies.Select(s => ToolBox.EvaluateBackTestingResult(output, s, daysCount, isUnstableStake))
                                 .ToArray();
            }

            bool BackTestForCoin(string coinId, Ticker[] tickers, double wallet, bool isUnstableStake,
                                 out BackTestingResult[] tradingResults)
            {
                if (tickers.Length == 0)
                {
                    tradingResults = Array.Empty<BackTestingResult>();
                    return true;
                }
                ClassLogger.Information($"Back test all strategies with {coinId}.");

                ManipulateConfiguration(originalConfig, config => config["stake_currency"] = tickers[0].Target);
                string pairs = string.Join(" ", tickers.Select(t => t.ToTradingPairString()));
                string strategiesList = string.Join(" ", strategies);
                DateTime startDate = DateTime.Today - new TimeSpan(DefaultBackTestingTimeRange, 0, 0, 0);
                bool result = ProcessFacade.Execute("freqtrade",
                                                    $"backtesting --data-format-ohlcv hdf5 --dry-run-wallet {wallet} --timerange {startDate:yyyyMMdd}- -p {pairs} --strategy-list {strategiesList}",
                                                    out StringBuilder output);

                ClassLogger.Information(result ? $"Tested all strategies with {coinId}." : "Error while back testing strategies.");

                ClassLogger.Information($"Filter and test found results.");

                tradingResults = EvaluateBackTestingResults(output.ToString(), DefaultBackTestingTimeRange, strategies, isUnstableStake);
                return result;
            }
        }

        private static BackTestingResult[] FilterResults(IEnumerable<BackTestingResult> results, ProgramConfiguration programConfiguration)
        {
            IEnumerable<BackTestingResult> preFiltered = results.Where(r => r.ProfitPerDay >= programConfiguration.MinimumDailyProfit)
                                                                .Where(r => r.TradesPerDay >= programConfiguration.MinimumTradesPerDay);
            return preFiltered.ToArray();
        }

        private static JObject ManipulateConfiguration(JObject originalConfig, Action<JObject> manipulate)
        {
            JObject config = (JObject) originalConfig.DeepClone();
            manipulate(config);
            using FileStream fileStream =
                File.Open(Path.Combine("user_data", "config.json"), FileMode.Open, FileAccess.Write);
            fileStream.Seek(0, SeekOrigin.Begin);
            fileStream.SetLength(0);
            using StreamWriter writer = new(fileStream, Encoding.Default);
            writer.Write(config.ToString(Formatting.Indented));
            return config;
        }

        private static bool DownloadDataForBackTesting(Ticker[] unstableBase, Ticker[] stableBase)
        {
            string pairs = string.Join(" ", unstableBase.Concat(stableBase)
                                                        .Select(t => t.ToTradingPairString()));
            ClassLogger.Information($"Download data for back testing.");
            bool result = ProcessFacade.Execute("freqtrade", $"download-data -t 1m 5m 15m --data-format-ohlcv hdf5 --data-format-trades hdf5 -p {pairs}");

            ClassLogger.Information(result ? "Data successfully downloaded." : "Error while downloading data.");
            return result;
        }

        private static bool RetrieveTradingPairSets(ProgramConfiguration programConfiguration, out Ticker[] unstableStake, out Ticker[] stableStake)
        {
            unstableStake = null;
            stableStake = null;

            try
            {
                if (!TryGetTickers(out List<Ticker> tickers))
                {
                    return false;
                }

                unstableStake = FilteredTickers(programConfiguration.UnstableStakeCoin, tickers)
                   .ToArray();
                stableStake = FilteredTickers(programConfiguration.StableStakeCoin, tickers)
                   .ToArray();
                ClassLogger.Information($"Found {tickers.Count(t => t.TargetId == programConfiguration.UnstableStakeCoin)} trading pairs for {programConfiguration.UnstableStakeCoin}");
                ClassLogger.Verbose($"Chosen pairs for {programConfiguration.UnstableStakeCoin}");
                foreach (Ticker ticker in unstableStake)
                {
                    ClassLogger.Verbose(ticker.ToString());
                }
                ClassLogger.Information($"Found {tickers.Count(t => t.TargetId == programConfiguration.StableStakeCoin)} trading pairs for {programConfiguration.StableStakeCoin}");
                ClassLogger.Verbose($"Chosen pairs for {programConfiguration.StableStakeCoin}");
                foreach (Ticker ticker in stableStake)
                {
                    ClassLogger.Verbose(ticker.ToString());
                }
            }
            catch (Exception e)
            {
                ClassLogger.Error(e, $"Error while retrieving trading pairs. {e}");
                return false;
            }
            return true;

            Ticker[] SortedAndFilteredTickers(string targetId, List<Ticker> tickers)
            {
                Ticker[] filtered = tickers.Where(t => t.TargetId == targetId)
                                           .ToArray();
                List<Ticker> volumeSorting = filtered.OrderByDescending(t => t.Volume).ToList();
                List<Ticker> trustSorting = filtered.OrderByDescending(t => t.TrustRating).ToList();
                foreach (Ticker ticker in filtered)
                {
                    ticker.TrustRank = trustSorting.IndexOf(ticker);
                    ticker.VolumeRank = volumeSorting.IndexOf(ticker);
                    ticker.WeightedValue = (filtered.Length - ticker.TrustRank) * programConfiguration.TrustWeight +
                                           (filtered.Length - ticker.VolumeRank) * programConfiguration.VolumeWeight;
                }

                return filtered.OrderByDescending(t => t.WeightedValue).Distinct().ToArray();
            }

            Ticker[] FilteredTickers(string targetId, List<Ticker> tickers)
            {
                Ticker[] filtered = tickers.Where(t => t.TargetId == targetId)
                                           .Where(t => t.TrustRating >= programConfiguration.MinimumTrustScore)
                                           .Distinct()
                                           .ToArray();
                return filtered;
            }

            IRestResponse GetRateLimitedResponse(RestClient client, RestRequest request)
            {
                IRestResponse response = client.Get(request);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    ClassLogger.Warning($"Rate limit reached waiting for {programConfiguration.RateLimitTimeout} ms and try again.");
                    Thread.Sleep(programConfiguration.RateLimitTimeout);
                    return GetRateLimitedResponse(client, request);
                }

                return response;
            }
            
            void AddTrustRating(List<Ticker> tickers, RestClient client)
            {
                foreach (IGrouping<string, Ticker> tickerGroup in tickers.GroupBy(t => t.BaseId))
                {
                    ClassLogger.Information($"Retrieve coin details for {tickerGroup.Key}.");
                    RestRequest request = new(programConfiguration.CoingeckoGetCoinDataMethod
                                                                          .Replace("$(coin)", tickerGroup.Key),
                                                      DataFormat.Json);
                    IRestResponse response = GetRateLimitedResponse(client, request);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        ClassLogger.Information($"Bad response for coin details {response.StatusCode}.");
                        continue;
                    }

                    CoinData result = JsonConvert.DeserializeObject<CoinData>(response.Content,
                                                                              new JsonSerializerSettings
                                                                              {
                                                                                  Culture = CultureInfo.GetCultureInfo(
                                                                                      "en-US")
                                                                              });
                    if (result == null)
                    {
                        continue;
                    }

                    int availableScores = 0;
                    double sum = 0;
                    if (result.CoingeckoScore.HasValue)
                    {
                        sum += result.CoingeckoScore.Value;
                        availableScores++;
                    }
                    if (result.CommunityScore.HasValue)
                    {
                        sum += result.CommunityScore.Value;
                        availableScores++;
                    }
                    if (result.VotesUpScore.HasValue)
                    {
                        sum += result.VotesUpScore.Value;
                        availableScores++;
                    }

                    double trustRating = sum / availableScores;
                    foreach (Ticker ticker in tickerGroup)
                    {
                        ticker.TrustRating = trustRating;
                    }
                    ClassLogger.Information($"Coin detail retrieved TrustRating: {trustRating}.");
                }
            }

            bool GetTickers(RestClient client, out List<Ticker> tickers)
            {
                string nextLink;
                int page = 1;
                tickers = new();

                do
                {
                    RestRequest request = new(programConfiguration.CoingeckoGetTickersMethod
                                                                  .Replace("$(exchange)", programConfiguration.Exchange)
                                                                  .Replace("$(page)", page.ToString(CultureInfo.InvariantCulture)),
                                              DataFormat.Json);
                    IRestResponse response = GetRateLimitedResponse(client, request);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return false;
                    }

                    CoingeckoTickers result = JsonConvert.DeserializeObject<CoingeckoTickers>(response.Content,
                        new JsonSerializerSettings
                        {
                            Culture = ToolBox.ConfigCulture
                        });
                    tickers.AddRange(result?.Tickers ?? Enumerable.Empty<Ticker>());
                    nextLink = (response.Headers.FirstOrDefault(p => p.Name == "Link")
                                       ?.Value?.ToString() ?? string.Empty)
                              .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => PaginationLinks.Match(s))
                              .Where(m => m.Success)
                              .FirstOrDefault(
                                   m => StringComparer.OrdinalIgnoreCase.Equals(m.Groups["type"].Value, "last"))
                             ?.Groups["link"].Value;
                    page++;
                } while (!string.IsNullOrEmpty(nextLink));

                return true;
            }

            bool TryGetTickers(out List<Ticker> tickers)
            {
                if (TryGetCachedTickers(out tickers))
                {
                    return true;
                }
                ClassLogger.Information($"Retrieve all trading pairs from coingecko.");
                RestClient client = new(programConfiguration.CoingeckoApiBaseUrl);
                if (!GetTickers(client, out tickers))
                {
                    return false;
                }

                tickers.RemoveAll(t => t.IsAnomaly ||
                                       t.IsStale ||
                                       !StringComparer.OrdinalIgnoreCase.Equals(t.TrustScore, "green"));
                AddTrustRating(tickers, client);
                UpdateCache(tickers);
                return true;

                bool TryGetCachedTickers(out List<Ticker> tickers)
                {
                    string cacheFile = Path.Combine("user_data", "tickers_cache", "cache.json");
                    tickers = null;
                    try
                    {
                        if (!File.Exists(cacheFile))
                        {
                            return false;
                        }

                        TickersCache cache =  JsonConvert.DeserializeObject<TickersCache>(File.ReadAllText(cacheFile, Encoding.UTF8));
                        if (cache == null ||
                            cache.ValidUntil < DateTime.Now)
                        {
                            return false;
                        }
                        tickers = new List<Ticker>(cache.Tickers);
                        return true;
                    }
                    catch (Exception e)
                    {
                        ClassLogger.Warning(e, $"Error while reading the ticker cache. {e}");
                        return false;
                    }
                }

                void UpdateCache(List<Ticker> tickers)
                {
                    string cacheDirectory = Path.Combine("user_data", "tickers_cache");
                    if (!Directory.Exists(cacheDirectory))
                    {
                        Directory.CreateDirectory(cacheDirectory);
                    }

                    TickersCache cache = new(DateTime.Now + new TimeSpan(2, 0, 0), tickers.ToArray());
                    File.WriteAllText(Path.Combine(cacheDirectory, "cache.json"),
                                      JsonConvert.SerializeObject(cache, Formatting.Indented), Encoding.UTF8);
                }
            }
        }

        private static bool UpdateStrategyRepository(ProgramConfiguration programConfiguration, out string[] strategies)
        {
            strategies = null;
            if (!CloneStrategiesRepository(programConfiguration))
            {
                return false;
            }
            
            if (!CopyStrategiesToAppFolder())
            {
                return false;
            }

            return GetStrategyNames(programConfiguration, out strategies);
        }

        private static bool GetStrategyNames(ProgramConfiguration programConfiguration, out string[] strategies)
        {
            ClassLogger.Information($"Retrieving strategies list.");
            bool result = ProcessFacade.Execute("freqtrade", "list-strategies -1", out StringBuilder output);
            strategies = output.ToString().Split(Environment.NewLine, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                               .Reverse()
                               .TakeWhile(s => StrategyRecognizer.IsMatch(s))
                               .Except(programConfiguration.StrategyBlacklist)
                               .ToArray();
            return result;
        }

        private static bool CopyStrategiesToAppFolder()
        {
            ClassLogger.Information($"Cleanup strategy folder.");
            try
            {
                if (Directory.Exists(StrategiesAppLocation))
                {
                    Directory.Delete(StrategiesAppLocation, true);
                }

                Directory.CreateDirectory(StrategiesAppLocation);

                ClassLogger.Information($"Copy all strategies to strategy folder.");

                foreach (string filePath in Directory.GetFiles(StrategyRepoStrategiesLocation, "*", SearchOption.AllDirectories))
                {
                    ClassLogger.Verbose($"Copy strategy {Path.GetFileName(filePath)}.");
                    File.Copy(filePath, Path.Combine(StrategiesAppLocation, Path.GetFileName(filePath)),
                              true);
                }
            }
            catch (Exception e)
            {
                ClassLogger.Error(e, $"Error while updating steategy folder. {e}");
                return false;
            }

            return true;
        }

        private static bool CloneStrategiesRepository(ProgramConfiguration programConfiguration)
        {
            ClassLogger.Information($"Recreate strategies repository directory in {Path.GetFullPath(StrategyRepoLocation)}");
            try
            {
                if (Directory.Exists(StrategyRepoLocation))
                {
                    Directory.Delete(StrategyRepoLocation, true);
                }

                Directory.CreateDirectory(StrategyRepoLocation);
            }
            catch (Exception e)
            {
                ClassLogger.Error(e, $"Error while recreating directory. {e}");
                return false;
            }

            ClassLogger.Information($"Cloning strategies repository {programConfiguration.StrategyRepositoryUrl}.");
            if (!ProcessFacade.Execute("git", $"clone --progress {programConfiguration.StrategyRepositoryUrl} {StrategyRepoLocation}"))
            {
                return false;
            }

            return true;
        }

        private static ProgramConfiguration ParseConfiguration(FindOptimizedStrategiesOptions options)
        {
            try
            {
                if (!File.Exists(options.ConfigFile))
                {
                    return new ProgramConfiguration();
                }

                return JsonConvert.DeserializeObject<ProgramConfiguration>(
                    File.ReadAllText(options.ConfigFile, Encoding.UTF8));
            }
            catch (Exception e)
            {
                Log.Warning(e, $"Error while parsing the config file in {options.ConfigFile}.{Environment.NewLine}{e}");
                return new ProgramConfiguration();
            }
        }
        
        private class BatchResultCollector
        {
            private readonly Dictionary<string, Dictionary<Ticker, double>> pairs = new();
            private readonly Dictionary<string, Ticker> allTickers;

            public BatchResultCollector(IEnumerable<Ticker> allTickers)
            {
                this.allTickers = allTickers.ToDictionary(t => t.ToTradingPairString(), t => t);
            }

            public void Collect(IEnumerable<BackTestingResult> results)
            {
                foreach (BackTestingResult result in results)
                {
                    if (!pairs.ContainsKey(result.Strategy))
                    {
                        pairs.Add(result.Strategy, new Dictionary<Ticker, double>());
                    }

                    foreach (KeyValuePair<string,double> pair in result.PairsProfit)
                    {
                        pairs[result.Strategy][allTickers[pair.Key]] = pair.Value;
                    }
                }
            }

            public IEnumerable<Ticker> GetStrategyTickers(string strategy, int cutoff)
            {
                return pairs[strategy].OrderByDescending(kv => kv.Value)
                                      .Take(cutoff)
                                      .Select(kv => kv.Key);
            }

            public void Log(ILogger classLogger)
            {
                foreach (KeyValuePair<string,Dictionary<Ticker,double>> strategy in pairs)
                {
                    classLogger.Verbose(strategy.Key);
                    classLogger.Verbose("======================");
                    foreach (KeyValuePair<Ticker,double> kv in strategy.Value)
                    {
                        classLogger.Verbose($"{kv.Key.ToTradingPairString()}: {kv.Value} - {kv.Key.Volume}");
                    }
                }
            }
        }
    }
}