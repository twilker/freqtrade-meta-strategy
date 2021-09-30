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
        public static int Main(string[] args)
        {
            return Parser.Default.ParseArguments<BlacklistOptimizationOptions, ParameterOptimizationOptions>(args)
                  .MapResult<BlacklistOptimizationOptions, ParameterOptimizationOptions,int>(ExecuteBlacklistOptimization, ExecuteParameterOptimization, MapError);
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

        private static int MapError(IEnumerable<Error> errors)
        {
            return 1;
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