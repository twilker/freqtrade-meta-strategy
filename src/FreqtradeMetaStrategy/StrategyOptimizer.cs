using System;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Serilog;
using Serilog.Core;

namespace FreqtradeMetaStrategy
{
    internal static class StrategyOptimizer
    {
        private const string StrategyRepoLocation = "./user_data/strategies_source";
        private const string StrategiesAppLocation = "./user_data/strategies";
        private static readonly ILogger ClassLogger = Log.ForContext(typeof(StrategyOptimizer));
        
        public static int Optimize(FindOptimizedStrategiesOptions options)
        {
            ProgramConfiguration programConfiguration = ParseConfiguration(options);
            bool result = UpdateStrategyRepository(programConfiguration);
            return result ? 0 : 1;
        }

        private static bool UpdateStrategyRepository(ProgramConfiguration programConfiguration)
        {
            if (!CloneStrategiesRepository(programConfiguration))
            {
                return false;
            }
            
            //TODO add new method for below code.
            ClassLogger.Information($"Cleanup strategy folder.");
            try
            {
                if (Directory.Exists(StrategiesAppLocation))
                {
                    Directory.Delete(StrategiesAppLocation, true);
                }

                Directory.CreateDirectory(StrategiesAppLocation);
                
                ClassLogger.Information($"Copy all strategies to strategy folder.");
                
                foreach (string filePath in Directory.GetFiles(StrategyRepoLocation, "*.*, SearchOptions.AllDirectories"))
                {
                    File.Copy(filePath, Path.Combine(StrategiesAppLocation, Path.GetFileName(filePath)));
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
            if (!ProcessFacade.Execute("git", $"clone {programConfiguration.StrategyRepositoryUrl} {StrategyRepoLocation}"))
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