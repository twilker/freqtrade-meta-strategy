using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Serilog;
using Serilog.Core;

namespace FreqtradeMetaStrategy
{
    internal static class StrategyOptimizer
    {
        private const string StrategyRepoLocation = "./user_data/strategies_source";
        private const string StrategyRepoStrategiesLocation = "./user_data/strategies_source/user_data/strategies";
        private const string StrategiesAppLocation = "./user_data/strategies";
        private static readonly ILogger ClassLogger = Log.ForContext(typeof(StrategyOptimizer));
        private static readonly Regex StrategyRecognizer = new Regex(@"^\w\S*$", RegexOptions.Compiled);
        
        public static int Optimize(FindOptimizedStrategiesOptions options)
        {
            ProgramConfiguration programConfiguration = ParseConfiguration(options);
            bool result = UpdateStrategyRepository(programConfiguration, out string[] strategies);
            if (result)
            {
                result = RetrieveTradingPairSets(programConfiguration, out string[] btcBase, out string[] usdtBase);
            }
            return result ? 0 : 1;
        }

        private static bool RetrieveTradingPairSets(ProgramConfiguration programConfiguration, out string[] btcBase, out string[] usdtBase)
        {
            btcBase = null;
            usdtBase = null;
            
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