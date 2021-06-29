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
using RestSharp;
using RestSharp.Serialization.Json;
using Serilog;
using Serilog.Core;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace FreqtradeMetaStrategy
{
    internal static class StrategyOptimizer
    {
        private const string StrategyRepoLocation = "./user_data/strategies_source";
        private const string StrategyRepoStrategiesLocation = "./user_data/strategies_source/user_data/strategies";
        private const string StrategiesAppLocation = "./user_data/strategies";
        private const int DefaultBackTestingTimeRange = 30;
        private static readonly ILogger ClassLogger = Log.ForContext(typeof(StrategyOptimizer));
        private static readonly Regex StrategyRecognizer = new(@"^\w\S*$", RegexOptions.Compiled);
        private static readonly Regex PaginationLinks = new(@"\<(?<link>[^\>]*)\>;\s*rel=""(?<type>[^""]*)""", RegexOptions.Compiled);
        private static readonly Regex BackTestingTradesPerDay = new(@"^\|\s*Trades per day\s*\|\s*(?<trades>\d+(?:\.\d+)).*\|$", RegexOptions.Compiled);
        private static readonly Regex BackTestingTotalProfit = new(@"^\|\s*Total profit %\s*\|\s*(?<profit>-?\d+(?:\.\d+)).*\|$", RegexOptions.Compiled);
        private static readonly Regex BackTestingPairProfit =
            new(
                @"^\|\s*(?<pair>[A-Z]*\/[A-Z]*)\s*\|[^\|]*\|[^\|]*\|[^\|]*\|[^\|]*\|\s*(?<total_profit>-?\d+(?:\.\d+)).*\|$"
              , RegexOptions.Compiled);
        
        public static int Optimize(FindOptimizedStrategiesOptions options)
        {
            ProgramConfiguration programConfiguration = ParseConfiguration(options);
            bool result = UpdateStrategyRepository(programConfiguration, out string[] strategies);
            
            Ticker[] unstableStake = null, stableStake = null;
            BackTestingResult[] testingResults = null;
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
                result = BackTestAllStrategies(strategies, unstableStake, stableStake, programConfiguration,out testingResults);
            }
            return result ? 0 : 1;
        }
        
        /**
         * string strategiesList = string.Join(" ", preFiltered.Select(r => r.Strategy));
                DateTime startDate = DateTime.Today - new TimeSpan(programConfiguration.StrategyRunningDays, 0, 0, 0);
                bool result = ProcessFacade.Execute("freqtrade", $"backtesting --data-format-ohlcv hdf5 --dry-run-wallet {programConfiguration.UnstableCoinWallet} --timerange {startDate:yyyyMMdd}- -p {pairs} --strategy-list {strategiesList}", out StringBuilder output);
                
                if (!result)
                {
                    throw new InvalidOperationException($"Unexpected failure of backtesting already tested strategies.");
                }
                
                BackTestingResult[] shortTermResults = EvaluateBackTestingResults(output.ToString(), programConfiguration.StrategyRunningDays);

                return results.Where(r => shortTermResults.Any(st => IsGoodEnough(st, r)))
                              .ToArray();

                bool IsGoodEnough(BackTestingResult shortTerm, BackTestingResult longTerm)
                {
                    return shortTerm.Strategy == longTerm.Strategy &&
                           shortTerm.ProfitPerDay >= programConfiguration.MinimumDailyProfit &&
                           (shortTerm.ProfitPerDay >= longTerm.ProfitPerDay ||
                            shortTerm.ProfitPerDay/longTerm.ProfitPerDay >= programConfiguration.AllowedShortTermVariance);
                }
         */

        private static bool BackTestAllStrategies(string[] strategies, Ticker[] unstableStake, Ticker[] stableStake,
                                                  ProgramConfiguration programConfiguration, out BackTestingResult[] filteredResults)
        {
            try
            {
                bool result = BackTestForCoin(programConfiguration.UnstableStakeCoin, unstableStake, programConfiguration.UnstableCoinWallet, out BackTestingResult[] unstableResults);
                filteredResults = unstableResults;

                if (result)
                {
                    result = BackTestForCoin(programConfiguration.StableStakeCoin, stableStake, programConfiguration.StableCoinWallet, out BackTestingResult[] stableResults);
                    filteredResults = unstableResults.Concat(stableResults).ToArray();
                }

                if (result)
                {
                    ClassLogger.Information($"Valid strategies:{Environment.NewLine}{string.Join<BackTestingResult>(Environment.NewLine, filteredResults)}");
                }
                
                return result;
            }
            catch (Exception e)
            {
                filteredResults = null;
                ClassLogger.Error(e, $"Error while back testing. {e}");
                return false;
            }
            
            
            BackTestingResult[] EvaluateBackTestingResults(string output, int daysCount, IEnumerable<string> strategies)
            {
                return strategies.Select(s => EvaluateBackTestingResult(output, s, daysCount))
                                 .ToArray();
            }
            
            BackTestingResult[] FilterResults(BackTestingResult[] results, string pairs)
            {
                IEnumerable<BackTestingResult> preFiltered = results
                                                            .Where(r => r.ProfitPerDay >=
                                                                        programConfiguration.MinimumDailyProfit)
                                                            .Where(r => r.TradesPerDay >=
                                                                        programConfiguration.MinimumTradesPerDay);
                return preFiltered.ToArray();
            }

            bool BackTestForCoin(string coinId, Ticker[] tickers, double wallet, out BackTestingResult[] tradingResults)
            {
                ClassLogger.Information($"Back test all strategies with {coinId}.");

                string pairs = string.Join(" ", tickers.Select(t => t.ToTradingPairString()));
                string strategiesList = string.Join(" ", strategies);
                bool result = ProcessFacade.Execute("freqtrade",
                                                    $"backtesting --data-format-ohlcv hdf5 --dry-run-wallet {wallet} -p {pairs} --strategy-list {strategiesList}",
                                                    out StringBuilder output);

                ClassLogger.Information(result ? $"Tested all strategies with {coinId}." : "Error while back testing strategies.");

                ClassLogger.Information($"Filter and test found results.");

                tradingResults = EvaluateBackTestingResults(output.ToString(), DefaultBackTestingTimeRange, strategies);
                tradingResults = FilterResults(tradingResults, pairs);
                return result;
            }
        }

        private static BackTestingResult EvaluateBackTestingResult(string output, string strategyName, int daysCount)
        {
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
                                             CultureInfo.GetCultureInfo("en-US")));
                current++;
                pairLineMatch = BackTestingPairProfit.Match(outputSplit[current]);
            }

            Match tradesPerDayMatch = outputSplit.Skip(startResultLine)
                                                 .Select(l => BackTestingTradesPerDay.Match(l))
                                                 .First(m => m.Success);
            Match totalProfitMatch = outputSplit.Skip(startResultLine)
                                                .Select(l => BackTestingTotalProfit.Match(l))
                                                .First(m => m.Success);
            double tradesPerDay = double.Parse(tradesPerDayMatch.Groups["trades"].Value,
                                               CultureInfo.GetCultureInfo("en-US"));
            double dailyProfit = double.Parse(totalProfitMatch.Groups["profit"].Value,
                                              CultureInfo.GetCultureInfo("en-US")) / (100 * daysCount);
            return new BackTestingResult(strategyName, dailyProfit, tradesPerDay, pairProfits);
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
                ClassLogger.Information($"Retrieve all trading pairs from coingecko.");
                RestClient client = new(programConfiguration.CoingeckoApiBaseUrl);
                if (!GetTickers(client, out List<Ticker> tickers))
                {
                    return false;
                }

                tickers.RemoveAll(t => t.IsAnomaly ||
                                       t.IsStale ||
                                       !StringComparer.OrdinalIgnoreCase.Equals(t.TrustScore, "green"));
                AddTrustRating(tickers, client);
                
                unstableStake = SortedAndFilteredTickers(programConfiguration.UnstableStakeCoin, tickers)
                              .Take(programConfiguration.MaxTradingPairs)
                              .ToArray();
                stableStake = SortedAndFilteredTickers(programConfiguration.StableStakeCoin, tickers)
                            .OrderByDescending(t => t.WeightedValue)
                            .Take(programConfiguration.MaxTradingPairs)
                            .ToArray();
                ClassLogger.Information($"Found {tickers.Count(t => t.TargetId == programConfiguration.UnstableStakeCoin)} trading pairs for {programConfiguration.UnstableStakeCoin}");
                ClassLogger.Verbose($"Chosen pairs for {programConfiguration.UnstableStakeCoin}{Environment.NewLine}" +
                                    string.Join(Environment.NewLine, unstableStake.Select(t => t.ToString())));
                ClassLogger.Information($"Found {tickers.Count(t => t.TargetId == programConfiguration.StableStakeCoin)} trading pairs for {programConfiguration.StableStakeCoin}");
                ClassLogger.Verbose($"Chosen pairs for {programConfiguration.StableStakeCoin}{Environment.NewLine}" +
                                    string.Join(Environment.NewLine, stableStake.Select(t => t.ToString())));
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
                            Culture = CultureInfo.GetCultureInfo("en-US")
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
            ClassLogger.Information($"Found {strategies.Length} strategies.");
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
    }
}