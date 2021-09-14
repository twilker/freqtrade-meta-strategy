#region Copyright
//  Copyright (c) Tobias Wilker and contributors
//  This file is licensed under MIT
#endregion

using System;

namespace FreqtradeMetaStrategy
{
    public class BlacklistOptimizationResult
    {
        public string Strategy { get; set; }
        public bool DataDownloaded { get; set; }
        public string[] AllPairs { get; set; }
        public DateTime EndDate { get; set; }
        public BlacklistOptimizationPairsPartitionResult[] Results { get; set; }
        public string[] Blacklist { get; set; }
        public StrategyPerformance Performance { get; set; }
        public ParameterOptimization ParameterOptimization { get; set; }
    }
}