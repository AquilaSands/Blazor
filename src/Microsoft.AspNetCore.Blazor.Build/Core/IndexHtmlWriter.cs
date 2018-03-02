﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AngleSharp;
using AngleSharp.Html;
using AngleSharp.Parser.Html;
using Mono.Cecil;

namespace Microsoft.AspNetCore.Blazor.Build
{
    public class IndexHtmlWriter
    {
        public static void UpdateIndex(string path, string assemblyPath, IEnumerable<string> references, string outputPath)
        {
            var template = File.ReadAllText(path);
            var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
            var entryPoint = GetAssemblyEntryPoint(assemblyPath);
            var updatedContent = GetIndexHtmlContents(template, assemblyName, entryPoint, references);
            File.WriteAllText(outputPath, updatedContent);
        }

        private static string GetAssemblyEntryPoint(string assemblyPath)
        {
            using (var assemblyDefinition = AssemblyDefinition.ReadAssembly(assemblyPath))
            {
                var entryPoint = assemblyDefinition.EntryPoint;
                if (entryPoint == null)
                {
                    throw new ArgumentException($"The assembly at {assemblyPath} has no specified entry point.");
                }

                return $"{entryPoint.DeclaringType.FullName}::{entryPoint.Name}";
            }
        }

        /// <summary>
        /// Injects the Blazor boot code and supporting config data at a user-designated
        /// script tag identified with a <c>type</c> of <c>blazor-boot</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If a matching script tag is found, then it will be adjusted to inject
        /// supporting configuration data, including a <c>src</c> attribute that
        /// will load the Blazor client-side library.  Any existing attribute
        /// names that match the boot config data will be overwritten, but other
        /// user-supplied attributes will be left intact.  This allows, for example,
        /// to designate asynchronous loading or deferred running of the script
        /// reference.
        /// </para><para>
        /// If no matching script tag is found, it is assumed that the user is
        /// responsible for completing the Blazor boot process.
        /// </para>
        /// </remarks>
        public static string GetIndexHtmlContents(
            string htmlTemplate,
            string assemblyName,
            string assemblyEntryPoint,
            IEnumerable<string> binFiles)
        {
            var resultBuilder = new StringBuilder();

            // Search for a tag of the form <script type="boot-blazor"></script>, and replace
            // it with a fully-configured Blazor boot script tag
            var tokenizer = new HtmlTokenizer(
                new TextSource(htmlTemplate),
                HtmlEntityService.Resolver);
            var currentRangeStartPos = 0;
            var isInBlazorBootTag = false;
            var resumeOnNextToken = false;
            while (true)
            {
                var token = tokenizer.Get();
                if (resumeOnNextToken)
                {
                    resumeOnNextToken = false;
                    currentRangeStartPos = token.Position.Position;
                }

                switch (token.Type)
                {
                    case HtmlTokenType.StartTag:
                        {
                            // Only do anything special if this is a Blazor boot tag
                            var tag = token.AsTag();
                            if (IsBlazorBootTag(tag))
                            {
                                // First, emit the original source text prior to this special tag, since
                                // we want that to be unchanged
                                resultBuilder.Append(htmlTemplate, currentRangeStartPos, token.Position.Position - currentRangeStartPos - 1);

                                // Instead of emitting the source text for this special tag, emit a fully-
                                // configured Blazor boot script tag
                                AppendScriptTagWithBootConfig(
                                    resultBuilder,
                                    assemblyName,
                                    assemblyEntryPoint,
                                    binFiles,
                                    tag.Attributes);

                                // Set a flag so we know not to emit anything else until the special
                                // tag is closed
                                isInBlazorBootTag = true;
                            }
                            break;
                        }

                    case HtmlTokenType.EndTag:
                        // If this is an end tag corresponding to the Blazor boot script tag, we
                        // can switch back into the mode of emitting the original source text
                        if (isInBlazorBootTag)
                        {
                            isInBlazorBootTag = false;
                            resumeOnNextToken = true;
                        }
                        break;

                    case HtmlTokenType.EndOfFile:
                        // Finally, emit any remaining text from the original source file
                        resultBuilder.Append(htmlTemplate, currentRangeStartPos, htmlTemplate.Length - currentRangeStartPos);
                        return resultBuilder.ToString();
                }
            }
        }

        private static bool IsBlazorBootTag(HtmlTagToken tag)
            => string.Equals(tag.Name, "script", StringComparison.Ordinal)
            && tag.Attributes.Any(pair =>
                string.Equals(pair.Key, "type", StringComparison.Ordinal)
                && string.Equals(pair.Value, "blazor-boot", StringComparison.Ordinal));

        private static void AppendScriptTagWithBootConfig(
            StringBuilder resultBuilder,
            string assemblyName,
            string assemblyEntryPoint,
            IEnumerable<string> binFiles,
            List<KeyValuePair<string, string>> attributes)
        {
            var assemblyNameWithExtension = $"{assemblyName}.dll";

            var referencesAttribute = string.Join(",", binFiles.ToArray());

            var attributesDict = attributes.ToDictionary(x => x.Key, x => x.Value);
            attributesDict.Remove("type");
            attributesDict["src"] = "_framework/blazor.js";
            attributesDict["main"] = assemblyNameWithExtension;
            attributesDict["entrypoint"] = assemblyEntryPoint;
            attributesDict["references"] = referencesAttribute;

            resultBuilder.Append("<script");
            foreach (var attributePair in attributesDict)
            {
                if (!string.IsNullOrEmpty(attributePair.Value))
                {
                    resultBuilder.AppendFormat(" {0}=\"{1}\"",
                        attributePair.Key,
                        attributePair.Value); // TODO: HTML attribute encode
                }
                else
                {
                    resultBuilder.AppendFormat(" {0}",
                        attributePair.Key);
                }
            }
            resultBuilder.Append("></script>");
        }
    }
}
