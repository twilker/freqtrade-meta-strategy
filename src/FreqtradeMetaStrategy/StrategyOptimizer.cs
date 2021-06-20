using System;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using LibGit2Sharp;
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
            //TODO remove lib2git and use command line
            //TODO separate services build image from debian
            if (!Repository.IsValid(StrategyRepoLocation))
            {
                Repository.Clone(programConfiguration.StrategyRepositoryUrl, StrategyRepoLocation);
            }

            using Repository strategyRepository = new(StrategyRepoLocation);
            Commands.Pull(strategyRepository, new Signature("bearer", "a.b@c.de", DateTimeOffset.Now), new PullOptions());
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