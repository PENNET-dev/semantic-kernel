﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.SemanticKernel.Diagnostics;
using Microsoft.SemanticKernel.Orchestration;

namespace Microsoft.SemanticKernel.Planning.Sequential;

/// <summary>
/// Parse sequential plan text into a plan.
/// </summary>
internal static class SequentialPlanParser
{
    /// <summary>
    /// The tag name used in the plan xml for the user's goal/ask.
    /// TODO: never used
    /// </summary>
    internal const string GoalTag = "goal";

    /// <summary>
    /// The tag name used in the plan xml for the solution.
    /// </summary>
    internal const string SolutionTag = "plan";

    /// <summary>
    /// The tag name used in the plan xml for a step that calls a plugin function.
    /// </summary>
    internal const string FunctionTag = "function.";

    /// <summary>
    /// The attribute tag used in the plan xml for setting the context variable name to set the output of a function to.
    /// </summary>
    internal const string SetContextVariableTag = "setContextVariable";

    /// <summary>
    /// The attribute tag used in the plan xml for appending the output of a function to the final result for a plan.
    /// </summary>
    internal const string AppendToResultTag = "appendToResult";

    internal static Func<string, string, ISKFunction?> GetPluginFunction(IReadOnlyFunctionCollection functions)
    {
        return (pluginName, functionName) =>
        {
            if (string.IsNullOrEmpty(pluginName))
            {
                if (functions.TryGetFunction(functionName, out var pluginFunction))
                {
                    return pluginFunction;
                }
            }
            else if (functions.TryGetFunction(pluginName, functionName, out var pluginFunction))
            {
                return pluginFunction;
            }

            return null;
        };
    }

    /// <summary>
    /// Convert a plan xml string to a plan.
    /// </summary>
    /// <param name="xmlString">The plan xml string.</param>
    /// <param name="goal">The goal for the plan.</param>
    /// <param name="getPluginFunction">The callback to get a plugin function.</param>
    /// <param name="allowMissingFunctions">Whether to allow missing functions in the plan on creation.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="SKException">Thrown when the plan xml is invalid.</exception>
    internal static Plan ToPlanFromXml(this string xmlString, string goal, Func<string, string, ISKFunction?> getPluginFunction, bool allowMissingFunctions = false)
    {
        XmlDocument xmlDoc = new();
        try
        {
            xmlDoc.LoadXml("<xml>" + xmlString + "</xml>");
        }
        catch (XmlException e)
        {
            // xmlString wasn't valid xml, let's try and parse <plan> out of it

            // '<plan': Matches the literal string "<plan".
            // '\b': Represents a word boundary to ensure that "<plan" is not part of a larger word.
            // '[^>]*': Matches zero or more characters that are not the closing angle bracket (">"), effectively matching any attributes present in the opening <plan> tag.
            // '>': Matches the closing angle bracket (">") to indicate the end of the opening <plan> tag.
            // '(.*?)': Captures the content between the opening and closing <plan> tags using a non-greedy match. It matches any character (except newline) in a lazy manner, i.e., it captures the smallest possible match.
            // '</plan>': Matches the literal string "</plan>", indicating the closing tag of the <plan> element.
            Regex planRegex = new(@"<plan\b[^>]*>(.*?)</plan>", RegexOptions.Singleline);
            Match match = planRegex.Match(xmlString);

            if (!match.Success)
            {
                match = planRegex.Match($"{xmlString}</plan>"); // try again with a closing tag
            }

            if (match.Success)
            {
                string planXml = match.Value;

                try
                {
                    xmlDoc.LoadXml("<xml>" + planXml + "</xml>");
                }
                catch (XmlException ex)
                {
                    throw new SKException($"Failed to parse plan xml strings: '{xmlString}' or '{planXml}'", ex);
                }
            }
            else
            {
                throw new SKException($"Failed to parse plan xml string: '{xmlString}'", e);
            }
        }

        // Get the Solution
        XmlNodeList solution = xmlDoc.GetElementsByTagName(SolutionTag);

        var plan = new Plan(goal);

        // loop through solution node and add to Steps
        foreach (XmlNode solutionNode in solution)
        {
            var parentNodeName = solutionNode.Name;

            foreach (XmlNode childNode in solutionNode.ChildNodes)
            {
                if (childNode.Name == "#text" || childNode.Name == "#comment")
                {
                    // Do not add text or comments as steps.
                    // TODO - this could be a way to get Reasoning for a plan step.
                    continue;
                }

                if (childNode.Name.StartsWith(FunctionTag, StringComparison.OrdinalIgnoreCase))
                {
                    var pluginFunctionName = childNode.Name.Split(s_functionTagArray, StringSplitOptions.None)?[1] ?? string.Empty;
                    GetPluginFunctionNames(pluginFunctionName, out var pluginName, out var functionName);

                    if (!string.IsNullOrEmpty(functionName))
                    {
                        var pluginFunction = getPluginFunction(pluginName, functionName);

                        if (pluginFunction is not null)
                        {
                            var planStep = new Plan(pluginFunction);

                            var functionVariables = new ContextVariables();
                            var functionOutputs = new List<string>();
                            var functionResults = new List<string>();

                            var view = pluginFunction.Describe();
                            foreach (var p in view.Parameters)
                            {
                                functionVariables.Set(p.Name, p.DefaultValue);
                            }

                            if (childNode.Attributes is not null)
                            {
                                foreach (XmlAttribute attr in childNode.Attributes)
                                {
                                    if (attr.Name.Equals(SetContextVariableTag, StringComparison.OrdinalIgnoreCase))
                                    {
                                        functionOutputs.Add(attr.InnerText);
                                    }
                                    else if (attr.Name.Equals(AppendToResultTag, StringComparison.OrdinalIgnoreCase))
                                    {
                                        functionOutputs.Add(attr.InnerText);
                                        functionResults.Add(attr.InnerText);
                                    }
                                    else
                                    {
                                        functionVariables.Set(attr.Name, attr.InnerText);
                                    }
                                }
                            }

                            // Plan properties
                            planStep.Outputs = functionOutputs;
                            planStep.Parameters = functionVariables;
                            foreach (var result in functionResults)
                            {
                                plan.Outputs.Add(result);
                            }

                            plan.AddSteps(planStep);
                        }
                        else
                        {
                            if (allowMissingFunctions)
                            {
                                plan.AddSteps(new Plan(pluginFunctionName));
                            }
                            else
                            {
                                throw new SKException($"Failed to find function '{pluginFunctionName}' in plugin '{pluginName}'.");
                            }
                        }
                    }
                }

                // Similar to comments or text, do not add empty nodes as steps.
                // TODO - This could be a way to advertise desired functions for a plan.
            }
        }

        return plan;
    }

    private static void GetPluginFunctionNames(string pluginFunctionName, out string pluginName, out string functionName)
    {
        var pluginFunctionNameParts = pluginFunctionName.Split('.');
        pluginName = pluginFunctionNameParts?.Length > 1 ? pluginFunctionNameParts[0] : string.Empty;
        functionName = pluginFunctionNameParts?.Length > 1 ? pluginFunctionNameParts[1] : pluginFunctionName;
    }

    private static readonly string[] s_functionTagArray = new string[] { FunctionTag };
}
