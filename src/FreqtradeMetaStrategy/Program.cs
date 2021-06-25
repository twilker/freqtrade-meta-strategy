using System;
using System.Collections.Generic;
using System.IO;
using CommandLine;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace FreqtradeMetaStrategy
{
    internal static class Program
    {
        //How the new optimal strategy is chosen (implemented steps are marked with *):
        //0. Store strategy performance of last run
        //1. Download latest strategy repository from git
        //2. Download data from last 3 month
        //3. Download trade volume data of all available BTC/X UDST/X pairs
        //4. Backtest all strategies against 10 pairs of both BTC and UDST with highest volume for 3 month, 1 month, 1 week, 1 day
        //5. Choose 3 best strategies based on fitness function and strategy score and strategy performance
        //6. Remove all losing pairs and backtest 3 strategies again for 3 month, 1 month, 1 week, 1 day
        //7. Choose best performing
        //8. Hyperopt strategy
        //9. Increase strategy score
        //10. Output result including strategy durashuttion (1 day or 3 days [backtest against that too])
        //
        //Infos
        //=====
        //Get volume data: curl -X GET "https://api.coingecko.com/api/v3/coins/bitcoin/tickers?exchange_ids=binance&order=volume_desc" -H "accept: application/json"
        //REST client: https://restsharp.dev/
        public static int Main(string[] args)
        {
            return Parser.Default.ParseArguments<FindOptimizedStrategiesOptions, CreateConfigurationOptions>(args)
                  .MapResult<FindOptimizedStrategiesOptions, CreateConfigurationOptions,int>(ExecuteFindStrategy, ExecuteCreateConfig, MapError);
        }

        private static int MapError(IEnumerable<Error> errors)
        {
            return 1;
        }

        private static int ExecuteCreateConfig(CreateConfigurationOptions options)
        {
            throw new NotImplementedException();
        }

        private static int ExecuteFindStrategy(FindOptimizedStrategiesOptions options)
        {
            ConfigureLogging(options);
            return StrategyOptimizer.Optimize(options);
        }

        private static void ConfigureLogging(CommonOptions commonOptions)
        {
            if (File.Exists(commonOptions.LogFilePath))
            {
                File.Delete(commonOptions.LogFilePath);
            }
            Log.Logger = new LoggerConfiguration()
                        .MinimumLevel.Verbose()
                        .WriteTo.File(new JsonFormatter(),commonOptions.LogFilePath)
                        .WriteTo.Console()
                        .CreateLogger();
        }
    }

    [Verb("create-config")]
    public class CreateConfigurationOptions : CommonOptions
    {
        
    }

    [Verb("find-strategies", true, HelpText="Find the three best strategies from a defined set of strategies.")]
    public class FindOptimizedStrategiesOptions : CommonOptions
    {
        [Option('c', "config", HelpText = "Path to the config file.", Default = "./meta-strategy-config.json")]
        public string ConfigFile { get; set; }
    }

    public class CommonOptions
    {
        [Option('l', "log", HelpText = "Path were the log file is kept.", Default = "./user_data/logs/strategizer-log.json")]
        public string LogFilePath { get; set; }
    }
}