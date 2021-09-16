using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            return Parser.Default.ParseArguments<FindOptimizedStrategiesOptions, CreateConfigurationOptions, LongTermTestOptions, BlacklistOptimizationOptions, ParameterOptimizationOptions>(args)
                  .MapResult<FindOptimizedStrategiesOptions, CreateConfigurationOptions, LongTermTestOptions, BlacklistOptimizationOptions, ParameterOptimizationOptions,int>(ExecuteFindStrategy, ExecuteCreateConfig, ExecuteLongTermTest, ExecuteBlacklistOptimization, ExecuteParameterOptimization, MapError);
        }

        private static int ExecuteParameterOptimization(ParameterOptimizationOptions arg)
        {
            ConfigureLogging(arg);
            return ParameterOptimizationTest.OptimizeParameters(arg) ? 0 : 1;
        }

        private static int ExecuteBlacklistOptimization(BlacklistOptimizationOptions arg)
        {
            ConfigureLogging(arg);
            return BlacklistOptimization.GenerateOptimalBlacklist(arg) ? 0 : 1;
        }

        private static int ExecuteLongTermTest(LongTermTestOptions arg)
        {
            ConfigureLogging(arg);
            return LongTermTest.TestStrategy(arg) ? 0 : 1;
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
        
        [Option('s', "strategies", HelpText = "A list of strategies to optimize - this restricts the amount of strategies. It still considers the black list in the config.", Separator = ',')]
        public IEnumerable<string> Strategies { get; set; }
    }

    [Verb("long-term-test", HelpText="Make a long term test for a single strategy.")]
    public class LongTermTestOptions : CommonOptions
    {
        [Option('c', "config", HelpText = "Path to the config file.", Required = true)]
        public string ConfigFile { get; set; }
        
        [Option('s', "strategy", HelpText = "The strategy to test.", Required = true)]
        public string Strategy { get; set; }
        
        [Option('t', "tag", HelpText = "Tag to identify run.", Required = true)]
        public string Tag { get; set; }
        
        [Option('i', "interval", HelpText = "The interval in days.", Required = true)]
        public int Interval { get; set; }
        
        [Option('r', "time-range", HelpText = "The whole time range.", Required = true)]
        public int TimeRange { get; set; }
        
        [Option("skip-download", HelpText = "Skips the download.")]
        public bool SkipDownload { get; set; }
    }

    [Verb("blacklist-optimization", HelpText="Generate an optimized blacklist for a single strategy.")]
    public class BlacklistOptimizationOptions : CommonOptions
    {
        [Option('s', "strategy", HelpText = "The strategy to test.", Required = true)]
        public string Strategy { get; set; }
        
        [Option('t', "tag", HelpText = "Tag to identify run.", Required = true)]
        public string Tag { get; set; }
        
        [Option('i', "interval", HelpText = "The interval in days.", Required = true)]
        public int Interval { get; set; }
        
        [Option('r', "time-range", HelpText = "The whole time range.", Required = true)]
        public int TimeRange { get; set; }
        
        [Option('p', "pairs-partition", HelpText = "Number of pairs to run in one interval.", Required = true)]
        public int PairsPartition { get; set; }
        
        [Option('f', "time-frames", HelpText = "Override time frames.", Required = false)]
        public string TimeFrames { get; set; }
        
        [Option('c', "compare-tag", HelpText = "Strategy to compare with this strategy. The same pair list will be used.", Required = false)]
        public string CompareTag { get; set; }
        
        [Option("long-interval", HelpText = "Long interval used for parameter optimization.", Required = false)]
        public int LongInterval { get; set; }
    }

    [Verb("parameter-optimization", HelpText="Generate an optimized set of parameters for a single strategy.")]
    public class ParameterOptimizationOptions : CommonOptions
    {
        [Option('s', "strategy", HelpText = "The strategy to test.", Required = true)]
        public string Strategy { get; set; }
        
        [Option('t', "tag", HelpText = "Tag to identify run.", Required = true)]
        public string Tag { get; set; }
        
        [Option('i', "interval", HelpText = "The interval in days.", Required = true)]
        public int Interval { get; set; }
        
        [Option('r', "time-range", HelpText = "The whole time range.", Required = true)]
        public int TimeRange { get; set; }
        
        [Option("pair-range-low", HelpText = "Override low end of the pairs range test.", Required = false, Default = 60)]
        public int PairsRangeLow { get; set; }
        
        [Option("pair-range-high", HelpText = "Override high end of the pairs range test.", Required = false, Default = 100)]
        public int PairsRangeHigh { get; set; }
        
        [Option("pair-interval", HelpText = "Override the interval of the pairs range test.", Required = false, Default = 5)]
        public int PairsInterval { get; set; }
        
        [Option("pair-test-open-trades", HelpText = "Override the max open trades of the pairs range test.", Required = false, Default = 3)]
        public int PairsTestOpenTrades { get; set; }
        
        [Option("open-trades-low", HelpText = "Override low end of the open trades test.", Required = false, Default = 1)]
        public int OpenTradesLow { get; set; }
        
        [Option("open-trades-high", HelpText = "Override high end of the open trades test.", Required = false, Default = 9)]
        public int OpenTradesHigh { get; set; }
        
        [Option("open-trades-interval", HelpText = "Override the interval of the open trades test.", Required = false, Default = 1)]
        public int OpenTradesInterval { get; set; }
        
        [Option('f', "time-frames", HelpText = "Override time frames.", Required = false, Default = "5m 1h")]
        public string TimeFrames { get; set; }
    }

    public class CommonOptions
    {
        [Option('l', "log", HelpText = "Path were the log file is kept.", Default = "./user_data/logs/strategizer-log.json")]
        public string LogFilePath { get; set; }
    }
}