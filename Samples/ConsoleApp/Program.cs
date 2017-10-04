﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sillycore;

namespace ConsoleApp
{
    class Program
    {
        static ILogger<Program> _logger;

        static void Main(string[] args)
        {
            SillycoreAppBuilder.Instance
                .UseUtcTimes()
                .Build();

            _logger = SillycoreApp.LoggerFactory.CreateLogger<Program>();
            _logger.LogInformation("dsadsada");
        }
    }
}