﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Plugins.Web;
using RepoUtils;

// ReSharper disable once InconsistentNaming
public static class Example11_WebSearchQueries
{
    public static async Task RunAsync()
    {
        Console.WriteLine("======== WebSearchQueries ========");

        IKernel kernel = Kernel.Builder.WithLoggerFactory(ConsoleLogger.LoggerFactory).Build();

        // Load native plugins
        var plugin = new SearchUrlPlugin();
        var bing = kernel.ImportPlugin(plugin, "search");

        // Run
        var ask = "What's the tallest building in Europe?";
        var result = await kernel.RunAsync(
            ask,
            bing["BingSearchUrl"]
        );

        Console.WriteLine(ask + "\n");
        Console.WriteLine(result);
    }
}
