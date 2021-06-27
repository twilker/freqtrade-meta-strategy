using System;
using System.Diagnostics;
using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FreqtradeMetaStrategy
{
    public static class ProcessFacade
    {
        private static readonly ILogger ClassLogger = Log.ForContext(typeof(ProcessFacade));

        public static bool Execute(string command, string arguments)
        {
            return Execute(command, arguments, out _);
        }
        
        public static bool Execute(string command, string arguments, out StringBuilder completeOutput)
        {
            completeOutput = new StringBuilder();
            StringBuilder localOutput = completeOutput;
            bool outputReadStarted = false, errorReadStarted = false;
            ILogger processLogger = Log.ForContext("SourceContext", command);
            ProcessStartInfo startInfo = new(command, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            ClassLogger.Information($"Starting process {command} {arguments}");
            Process process = Process.Start(startInfo);
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.OutputDataReceived += ProcessOnOutputDataReceived;
                    process.ErrorDataReceived += ProcessOnErrorDataReceived;
                    process.EnableRaisingEvents = true;
                    process.BeginOutputReadLine();
                    outputReadStarted = true;
                    process.BeginErrorReadLine();
                    errorReadStarted = true;

                    process.WaitForExit();

                    ClassLogger.Information($"Process {command} has exited with code {process.ExitCode}");

                    return process.HasExited && process.ExitCode == 0;
                }
            }
            catch (Exception e)
            {
                processLogger.Warning(e,$"Error while starting process: {e}", false);
                //this happens when the process exits somewhere in this if clause
            }
            finally
            {
                if (process != null)
                {
                    process.OutputDataReceived -= ProcessOnOutputDataReceived;
                    process.ErrorDataReceived -= ProcessOnErrorDataReceived;
                    if (outputReadStarted)
                    {
                        process.CancelOutputRead();
                    }

                    if (errorReadStarted)
                    {
                        process.CancelErrorRead();
                    }
                    process.Dispose();
                }
            }

            return process?.HasExited == true && process.ExitCode == 0;
            
            void ProcessOnOutputDataReceived(object sender, DataReceivedEventArgs e)
            {
                localOutput.AppendLine(e.Data);
                processLogger.Information(e.Data);
            }
            
            void ProcessOnErrorDataReceived(object sender, DataReceivedEventArgs e)
            {
                localOutput.AppendLine(e.Data);
                processLogger.Warning(e.Data);
            }
        }
    }
}