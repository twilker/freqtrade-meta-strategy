using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
        private static readonly ILogger ClassLogger = Log.ForContext(typeof(StrategyOptimizer));
        private static readonly Regex StrategyRecognizer = new(@"^\w\S*$", RegexOptions.Compiled);
        private static readonly Regex PaginationLinks = new(@"\<(?<link>[^\>]*)\>;\s*rel=""(?<type>[^""]*)""", RegexOptions.Compiled);
        
        public static int Optimize(FindOptimizedStrategiesOptions options)
        {
            ProgramConfiguration programConfiguration = ParseConfiguration(options);
            bool result = UpdateStrategyRepository(programConfiguration, out string[] strategies);
            if (result)
            {
                result = RetrieveTradingPairSets(programConfiguration, out Ticker[] unstableBase, out Ticker[] stableBase);
            }
            return result ? 0 : 1;
        }

        private static bool RetrieveTradingPairSets(ProgramConfiguration programConfiguration, out Ticker[] unstableBase, out Ticker[] stableBase)
        {
            unstableBase = null;
            stableBase = null;

            ClassLogger.Information($"Retrieve all trading pairs from coingecko.");
            RestClient client = new(programConfiguration.CoingeckoApiBaseUrl);
            string nextLink;
            int page = 1;
            List<Ticker> tickers = new();
            
            do
            {
                RestRequest unstableRequest = new(programConfiguration.CoingeckoGetTickersMethod
                                                                      .Replace("$(exchange)", programConfiguration.Exchange)
                                                                      .Replace("$(page)", page.ToString(CultureInfo.InvariantCulture)), 
                                                  DataFormat.Json);
                IRestResponse response = client.Get(unstableRequest);
                CoingeckoTickers result = JsonConvert.DeserializeObject<CoingeckoTickers>(response.Content,
                    new JsonSerializerSettings
                    {
                        Culture = CultureInfo.GetCultureInfo("en-US")
                    });
                tickers.AddRange(result?.Tickers??Enumerable.Empty<Ticker>());
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

            tickers.RemoveAll(t => t.IsAnomaly ||
                                   t.IsStale ||
                                   !StringComparer.OrdinalIgnoreCase.Equals(t.TrustScore, "green"));
            unstableBase = tickers.Where(t => t.BaseId == programConfiguration.UnstableBaseCoin)
                                  .ToArray();
            stableBase = tickers.Where(t => t.BaseId == programConfiguration.StableBaseCoin)
                                .ToArray();
            ClassLogger.Verbose(string.Join<Ticker>(Environment.NewLine, unstableBase));
            ClassLogger.Information($"Found {unstableBase.Length} trading pairs for {programConfiguration.UnstableBaseCoin}");
            ClassLogger.Verbose(string.Join<Ticker>(Environment.NewLine, stableBase));
            ClassLogger.Information($"Found {stableBase.Length} trading pairs for {programConfiguration.StableBaseCoin}");
            return true;
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

            return GetStrategyNames(out strategies);
        }

        private static bool GetStrategyNames(out string[] strategies)
        {
            ClassLogger.Information($"Retrieving strategies list.");
            bool result = ProcessFacade.Execute("freqtrade", "list-strategies -1", out StringBuilder output);
            strategies = output.ToString().Split(Environment.NewLine, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                               .Reverse()
                               .TakeWhile(s => StrategyRecognizer.IsMatch(s))
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