using System;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Serilog;

namespace FreqtradeMetaStrategy
{
    internal static class StrategyOptimizer
    {
        public const string StrategyRepoLocation = "./user_data/strategies_source";
        
        public static int Optimize(FindOptimizedStrategiesOptions options)
        {
            ProgramConfiguration programConfiguration = ParseConfiguration(options);
            UpdateStrategyRepository(programConfiguration);
            return 0;
        }

        private static void UpdateStrategyRepository(ProgramConfiguration programConfiguration)
        {
            if (Directory.Exists(StrategyRepoLocation))
            {
                Directory.Delete(StrategyRepoLocation, true);
            }
            Directory.Create(StrategyRepoLocation);
            
            // Repository.Clone(programConfiguration.StrategyRepositoryUrl, StrategyRepoLocation);
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